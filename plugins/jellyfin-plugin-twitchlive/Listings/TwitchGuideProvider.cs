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
/// Both are guesses about the future - Twitch publishes no schedule - so the horizon is deliberately
/// short and configurable. Programme ids change when a title changes, which is how Jellyfin learns that
/// a stream moved on to something else.
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
            if (channel.Status.IsLive)
            {
                // A stream that has been running for hours started before the requested window; that is
                // normal and better than pretending it began now.
                var startUtc = channel.TitleSinceUtc ?? channel.LiveSinceUtc ?? startDateUtc;
                if (startUtc > endDateUtc || horizonUtc < startDateUtc)
                {
                    continue;
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

                programs.Add(new ProgramInfo
                {
                    Id = StableId(channel.Login, title),
                    ChannelId = channel.Login,
                    Name = title,
                    Overview = string.Join(" - ", overview),
                    ShortOverview = channel.Status.Game,
                    StartDate = startUtc,
                    EndDate = horizonUtc < endDateUtc ? horizonUtc : endDateUtc,

                    // The guide marks these as live rather than as a recording of something scheduled.
                    IsLive = true,
                    Genres = string.IsNullOrWhiteSpace(channel.Status.Game) ? [] : [channel.Status.Game!],
                    HomePageUrl = "https://www.twitch.tv/" + channel.Login,
                    Etag = StableId(channel.Login, title, startUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture))
                });

                continue;
            }

            // Offline: say so instead of leaving the slot empty. A login Twitch does not know gets its own
            // wording, because that is a mistake in the settings rather than a streamer who is asleep.
            var name = channel.Status.Exists ? "Offline" : "No such Twitch account";
            programs.Add(new ProgramInfo
            {
                Id = StableId(channel.Login, name),
                ChannelId = channel.Login,
                Name = name,
                Overview = channel.Status.Exists
                    ? $"{channel.DisplayName} is not streaming. Tune in when they go live, or check https://www.twitch.tv/{channel.Login}"
                    : $"'{channel.Login}' is not a Twitch account - fix the login in the Twitch Live TV plugin settings.",
                StartDate = startDateUtc < nowUtc ? startDateUtc : nowUtc,

                // Only as far as the live entries reach, so one refresh covers the same span either way.
                EndDate = horizonUtc < endDateUtc ? horizonUtc : endDateUtc,
                IsLive = false,
                Etag = StableId(channel.Login, name, "offline")
            });
        }

        _logger.LogDebug(
            "Twitch Live TV: described {Count} programmes for {Channels} channels",
            programs.Count,
            channels.Count);

        return programs;
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
