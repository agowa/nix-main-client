using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive.Listings;

/// <summary>
/// The programme guide for the Twitch channels, built from what is streaming right now.
/// </summary>
/// <remarks>
/// There is no XMLTV for "who is live on Twitch", and a Live TV view with an empty guide is hard to use,
/// so this plugin describes its own channels:
/// <list type="bullet">
///   <item><description>A running stream becomes one programme carrying the stream title and category, starting when this plugin first saw that title and reaching <see cref="PluginConfiguration.GuideAheadMinutes"/> into the future.</description></item>
///   <item><description>An offline channel gets an explicit "offline" entry instead of a blank slot, which is what tells the difference between a streamer who is asleep and a guide that was never filled in.</description></item>
/// </list>
/// Where the broadcaster publishes one, these are joined by the slots from their own Schedule tab
/// (<see cref="TwitchChannelRepository.GetScheduleAsync"/>), which are not guesses and end a live entry
/// early instead of letting it sit on top of an announced appointment. Everything generated here is a
/// guess about the future, so its horizon stays short and configurable; programme ids change when a
/// title changes, which is how Jellyfin learns that a stream moved on to something else.
/// </remarks>
public sealed class TwitchGuideProvider : IListingsProvider
{
    private readonly TwitchChannelRepository _repository;
    private readonly ILogger<TwitchGuideProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="TwitchGuideProvider"/> class.</summary>
    public TwitchGuideProvider(TwitchChannelRepository repository, ILogger<TwitchGuideProvider> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Twitch Live TV";

    /// <inheritdoc />
    // Same type string as the tuner host: this guide belongs to those channels.
    public string Type => TwitchChannelCatalog.HostId;

    /// <inheritdoc />
    /// <remarks>
    /// The ids returned here are what Jellyfin matches the tuner's channels against - a channel is
    /// matched when its <c>TunerChannelId</c> equals one of them, which is why both sides use the login.
    /// </remarks>
    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        var channels = await _repository.GetChannelsAsync(false, cancellationToken).ConfigureAwait(false);

        return channels
            .Select(c => new ChannelInfo
            {
                Id = c.Login,
                Name = c.DisplayName,
                Number = null,
                TunerChannelId = c.Login,
                ChannelType = MediaBrowser.Model.LiveTv.ChannelType.TV,
                ChannelGroup = c.Status.IsLive ? TwitchChannelCatalog.LiveGroup : TwitchChannelCatalog.OfflineGroup
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info,
        string? channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = await _repository.GetChannelsAsync(false, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var wanted = channelId.Trim().ToLowerInvariant();
            channels = channels.Where(c => string.Equals(c.Login, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var nowUtc = DateTime.UtcNow;
        var horizonUtc = nowUtc + TimeSpan.FromMinutes(Math.Max(1, config.GuideAheadMinutes));
        var programs = new List<ProgramInfo>(channels.Count);

        foreach (var channel in channels)
        {
            // What the broadcaster put on their own Schedule tab. Empty for most channels - Twitch
            // makes nobody publish one - and cached per channel for six hours, so this is cheap even
            // for a long list. Everything below is written so that a channel with no schedule behaves
            // exactly as it did before this existed.
            var scheduled = await _repository.GetScheduleAsync(channel.Login, channel.Status.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (channel.Status.IsLive)
            {
                var live = BuildLiveProgram(channel, scheduled, startDateUtc, horizonUtc);

                // Callers ask for a window ("what is on tonight"), and the guide also asks for one hour
                // of history; an entry entirely outside it would be answered as if it belonged there.
                if (live.EndDate > startDateUtc && live.StartDate < endDateUtc)
                {
                    programs.Add(live);
                }
            }
            else
            {
                // Offline: say so instead of leaving the slot empty - but only up to the point where
                // the channel's own schedule claims something is about to start, or "Offline" would sit
                // on top of an appointment. A login Twitch does not know gets its own wording, because
                // that is a mistake in the settings rather than a streamer who is asleep.
                // The first slot that has not started yet, if any. Its start is where "Offline" stops
                // being true, so the two entries never claim the same hour.
                var upcoming = scheduled.FirstOrDefault(i => i.StartUtc > nowUtc);
                var offlineUntil = upcoming is null || upcoming.StartUtc >= horizonUtc
                    ? horizonUtc
                    : upcoming.StartUtc;

                if (offlineUntil - nowUtc > TimeSpan.FromMinutes(5) && offlineUntil > startDateUtc)
                {
                    programs.Add(BuildOfflineProgram(channel, startDateUtc, offlineUntil, nowUtc));
                }
            }

            foreach (var slot in scheduled)
            {
                // The live entry already covers what is on air; a slot that overlaps it would be the
                // same hour twice in the guide.
                if (channel.Status.IsLive && slot.StartUtc < horizonUtc)
                {
                    continue;
                }

                if (slot.EndUtc <= startDateUtc || slot.StartUtc >= endDateUtc)
                {
                    continue;
                }

                programs.Add(BuildScheduledProgram(channel, slot));
            }
        }

        _logger.LogDebug(
            "Twitch Live TV: described {Count} programmes for {Channels} channels",
            programs.Count,
            channels.Count);

        return programs;
    }

    /// <summary>Builds the entry for a stream that is running right now.</summary>
    /// <remarks>
    /// A running stream has no published end, so it reaches <paramref name="horizonUtc"/> - except
    /// where the channel's own schedule says the next slot begins, which is a better guess than any
    /// timeout and stops "Lofi Girl 24/7" from sitting on top of an announced one-off.
    /// </remarks>
    private static ProgramInfo BuildLiveProgram(
        TwitchChannel channel,
        IReadOnlyList<TwitchScheduledItem> scheduled,
        DateTime startDateUtc,
        DateTime horizonUtc)
    {
        // A stream that has been running for hours started before the requested window; that is
        // normal and better than pretending it began now.
        var startUtc = channel.TitleSinceUtc ?? channel.LiveSinceUtc ?? startDateUtc;

        var endUtc = horizonUtc;
        foreach (var slot in scheduled)
        {
            if (slot.StartUtc > startUtc && slot.StartUtc < endUtc)
            {
                endUtc = slot.StartUtc;
                break;
            }
        }

        var title = string.IsNullOrWhiteSpace(channel.Status.Title) ? "Live" : channel.Status.Title.Trim();
        var overview = new List<string>();

        if (!string.IsNullOrWhiteSpace(channel.Status.Game))
        {
            overview.Add(channel.Status.Game!);
        }

        if (channel.Status.Viewers is > 0)
        {
            overview.Add(string.Format(CultureInfo.InvariantCulture, "{0:N0} watching", channel.Status.Viewers.Value));
        }

        if (channel.LiveSinceUtc is not null)
        {
            var since = channel.LiveSinceUtc.Value.ToLocalTime();
            overview.Add("live since " + since.ToString("HH:mm", CultureInfo.InvariantCulture) + " local");
        }

        // The stream thumbnail is the picture Twitch itself uses for this broadcast, so it doubles as
        // both the tile and the backdrop. Jellyfin reads all four fields below off ProgramInfo when it
        // writes the programme item (GuideManager.UpdateImage).
        var preview = channel.Status.PreviewImageUrl;

        return new ProgramInfo
        {
            Id = StableId(channel.Login, title, startUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)),
            ChannelId = channel.Login,
            Name = title,
            Overview = string.Join(" - ", overview),
            ShortOverview = channel.Status.Game,
            StartDate = startUtc,
            EndDate = endUtc < startDateUtc ? startDateUtc.AddMinutes(1) : endUtc,

            // The guide marks these as live rather than as a recording of something scheduled.
            IsLive = true,
            Genres = string.IsNullOrWhiteSpace(channel.Status.Game) ? [] : [channel.Status.Game!],
            HomePageUrl = "https://www.twitch.tv/" + channel.Login,
            ImageUrl = preview ?? channel.Status.GameBoxArtUrl,
            ThumbImageUrl = preview,
            BackdropImageUrl = preview,
            LogoImageUrl = channel.Status.ProfileImageUrl,
            Etag = StableId(channel.Login, title, startUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture))
        };
    }

    /// <summary>Builds the entry that explains why a channel is dark.</summary>
    private static ProgramInfo BuildOfflineProgram(TwitchChannel channel, DateTime startDateUtc, DateTime untilUtc, DateTime nowUtc)
    {
        var name = channel.Status.Exists ? "Offline" : "No such Twitch account";
        return new ProgramInfo
        {
            Id = StableId(channel.Login, name),
            ChannelId = channel.Login,
            Name = name,
            Overview = channel.Status.Exists
                ? $"{channel.DisplayName} is not streaming. Tune in when they go live, or check https://www.twitch.tv/{channel.Login}"
                : $"'{channel.Login}' is not a Twitch account - fix the login in the Twitch Live TV plugin settings.",
            StartDate = startDateUtc < nowUtc ? startDateUtc : nowUtc,

            // Only as far as the live entries reach, so one refresh covers the same span either way.
            EndDate = untilUtc,
            IsLive = false,
            LogoImageUrl = channel.Status.ProfileImageUrl,
            Etag = StableId(channel.Login, name, "offline")
        };
    }

    /// <summary>Builds an entry out of one slot of the broadcaster's published schedule.</summary>
    private static ProgramInfo BuildScheduledProgram(TwitchChannel channel, TwitchScheduledItem slot)
    {
        var overview = new List<string>();

        if (!string.IsNullOrWhiteSpace(slot.Category))
        {
            overview.Add(slot.Category!);
        }

        overview.Add(slot.IsRecurring ? "recurring schedule slot" : "announced once");
        overview.Add("as published by the channel");

        // A slot naming the category that is streaming right now can borrow its cover art; anything
        // else would mean a lookup per category for data the broadcaster may change tomorrow.
        var sameCategory = !string.IsNullOrWhiteSpace(slot.Category)
                           && string.Equals(slot.Category, channel.Status.Game, StringComparison.OrdinalIgnoreCase);

        return new ProgramInfo
        {
            Id = StableId(channel.Login, "schedule", slot.Id),
            ChannelId = channel.Login,
            Name = slot.Title,
            Overview = string.Join(" - ", overview),
            ShortOverview = slot.Category,
            StartDate = slot.StartUtc,
            EndDate = slot.EndUtc,

            // Not live: nothing is being broadcast yet, and Jellyfin uses this to decide whether the
            // entry may be played or only looked at.
            IsLive = false,
            Genres = string.IsNullOrWhiteSpace(slot.Category) ? [] : [slot.Category!],
            HomePageUrl = "https://www.twitch.tv/" + channel.Login,
            ImageUrl = sameCategory ? channel.Status.GameBoxArtUrl : null,
            BackdropImageUrl = sameCategory ? channel.Status.PreviewImageUrl : null,
            LogoImageUrl = channel.Status.ProfileImageUrl,
            Etag = StableId(channel.Login, "schedule", slot.Id, slot.StartUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture))
        };
    }

    /// <inheritdoc />
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
    {
        // Nothing to log in to and nothing to point at: the guide is generated from the same
        // configuration as the channels themselves.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
    {
        // Package selection is a concept of the paid guide services this interface also serves.
        return Task.FromResult(new List<NameIdPair>());
    }

    /// <summary>A deterministic id, because Jellyfin keys programmes on it.</summary>
    private static string StableId(params string[] parts)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();
}
