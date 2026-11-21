using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive.TunerHosts;

/// <summary>
/// A tuner host whose "antenna" is the public internet: it offers the configured Twitch channels as
/// Jellyfin Live TV channels and points Jellyfin at Twitch's own HLS playlists. No proxy, no playlist
/// files, no hardware.
/// </summary>
public sealed class TwitchTunerHost : ITunerHost, IConfigurableTunerHost
{
    /// <summary>The URL offered by "Detect"; only the web UI's form validation looks at it.</summary>
    internal const string VirtualUrl = "https://www.twitch.tv/directory";

    /// <summary>Prefix of every channel id this plugin hands out.</summary>
    private const string ChannelIdPrefix = TwitchChannelCatalog.HostId + "_";

    private readonly TwitchChannelRepository _repository;
    private readonly LiveTvDevices _devices;
    private readonly AvatarAssets _avatars;
    private readonly ChannelArtwork _artwork;
    private readonly ILogger<TwitchTunerHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="TwitchTunerHost"/> class.</summary>
    public TwitchTunerHost(
        TwitchChannelRepository repository,
        LiveTvDevices devices,
        AvatarAssets avatars,
        ChannelArtwork artwork,
        ILogger<TwitchTunerHost> logger)
    {
        _repository = repository;
        _devices = devices;
        _avatars = avatars;
        _artwork = artwork;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Twitch Live TV";

    /// <inheritdoc />
    // Also the value stored in TunerHostInfo.Type and the id used by DiscoverDevices().
    public string Type => TwitchChannelCatalog.HostId;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>
    /// Gets the channels to offer.
    /// </summary>
    /// <remarks>
    /// Live and offline channels are separated by group rather than by leaving one of them out: a
    /// channel that disappears every time its streamer sleeps would also lose its place in the guide,
    /// and Jellyfin only notices removals on the second refresh after it. With
    /// <see cref="PluginConfiguration.IncludeOfflineChannels"/> off they are left out instead.
    /// </remarks>
    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        // The server asks every registered tuner host for its channels, whether or not the operator
        // added a device for it - see LiveTvDevices. Answering with nothing until one exists is what
        // keeps this plugin's channels out of the list when only the other installed plugin has been
        // set up as a tuner.
        var device = _devices.GetPrimaryDevice();
        if (device is null)
        {
            return [];
        }

        // A source added from the dropdown rather than by detection arrives without a name, which the
        // tuner list then labels "Unknown" - see LiveTvDevices for why that is worth fixing here.
        await _devices.EnsureFriendlyNamesAsync(Name, cancellationToken).ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = await _repository.GetChannelsAsync(!enableCache, cancellationToken).ConfigureAwait(false);

        // Decided up front rather than inside the loop, because what is offered also decides which streamer
        // pictures are worth fetching at all - an offline channel that is being left out does not need one.
        var offered = config.IncludeOfflineChannels
            ? channels
            : channels.Where(c => c.Status.IsLive).ToList();

        // Getting our own copy of every offered picture onto disk happens here rather than somewhere inside the
        // repository: a channel's artwork has to exist before the server is told about it, because what it stores
        // from then on is this path. Anything still missing when the batch's share of the time is used up keeps
        // downloading in the background and is offered by address meanwhile - see AvatarAssets.
        var avatarsByLogin = new Dictionary<string, string?>(offered.Count, StringComparer.OrdinalIgnoreCase);

        if (config.ShowChannelLogos)
        {
            foreach (var channel in offered)
            {
                avatarsByLogin[channel.Login] = channel.Status.ProfileImageUrl;
            }
        }

        await _avatars.FetchAsync(avatarsByLogin, cancellationToken).ConfigureAwait(false);

        var result = new List<ChannelInfo>(offered.Count);
        var number = 1;

        foreach (var channel in offered)
        {
            var isLive = channel.Status.IsLive;

            // Whichever of the two this plugin can offer: its own file if it has one, Twitch's address until it
            // does. The file is preferred because nothing has to be fetched to show it - and because a picture
            // this plugin holds itself is replaced in place when the streamer changes it, without any channel
            // having to be rewritten (see ChannelArtwork for what happens to addresses stored before that).
            var picture = config.ShowChannelLogos
                ? _avatars.PathFor(channel.Login) ?? channel.Status.ProfileImageUrl
                : null;

            // Which of the two artwork fields a logo belongs in depends on whether it still has to be fetched -
            // GuideManager.UpdateChannel prefers ImagePath, and an address is downloaded by the server the first
            // time anything asks for the tile. Computed here because an object initializer cannot say that twice.
            var remotePicture = picture is not null && AvatarAssets.IsRemoteAddress(picture);

            result.Add(new ChannelInfo
            {
                // Prefixed with the plugin's own type: the Live TV service merges the channels of every
                // installed tuner into one list whose items are keyed by this id alone, so a Twitch
                // login called "zdf" would otherwise collide with another plugin's channel of that name.
                Id = ChannelIdPrefix + channel.Login,
                Name = channel.DisplayName,

                // Cosmetic: positions in the guide. Programme data is matched by TunerChannelId below.
                Number = (number++).ToString(CultureInfo.InvariantCulture),

                // Deliberately empty: a Twitch playlist address needs a playback token that is bound to
                // this machine's IP address and expires within minutes, so it cannot live in a channel
                // record. It is fetched when someone actually tunes in.
                Path = null,

                // The id this plugin's own guide provider uses for the same streamer, which is what
                // attaches programme data without manual mapping. It doubles as the id an external
                // XMLTV file would have to use.
                TunerChannelId = channel.Login,

                CallSign = channel.DisplayName,

                // The id of the tuner device as stored in livetv.xml - not this plugin's type string.
                // A listings provider that was enabled for specific tuners rather than "all" is matched
                // against exactly this value (ListingsManager.IsListingProviderEnabledForTuner), so
                // putting anything else here means the guide is silently skipped for every channel.
                TunerHostId = device.Id,

                ChannelType = ChannelType.TV,
                ChannelGroup = isLive ? TwitchChannelCatalog.LiveGroup : TwitchChannelCatalog.OfflineGroup,

                // Nothing is being broadcast while a channel is offline, so its picture format is not
                // known either.
                IsHD = isLive ? true : null,

                Tags = BuildTags(isLive, channel.FromFollows),

                // The account's own picture, which arrived with the live/offline answer - so offering it costs
                // no extra API request. In whichever of the two fields describes it: a file this plugin fetched
                // itself as a path (nothing left to download), an address as a URL, which the server downloads the
                // first time something asks for the tile and then keeps whether or not it still resolves.
                ImagePath = remotePicture ? null : picture,
                ImageUrl = remotePicture ? picture : null
            });
        }

        // Last, because it is the part nobody else will do: a channel that still holds an address from an earlier
        // list - one the streamer has since replaced, or one this plugin has now fetched for itself - keeps it for
        // good unless something steps in. See ChannelArtwork.
        var logosByChannelId = new Dictionary<string, string?>(result.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in result)
        {
            // Whichever field the picture ended up in: the repair compares what a channel holds against this value
            // and does not care whether the answer is an address or one of our own files.
            logosByChannelId[channel.Id] = channel.ImagePath ?? channel.ImageUrl;
        }

        await _artwork.RepairAsync(logosByChannelId, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Twitch Live TV: offering {Count} of {Configured} configured channels on tuner device '{Device}'",
            result.Count,
            channels.Count,
            device.Id);
        return result;
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = await ResolveForTuningAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            // An empty list tells the Live TV service to ask the next tuner host - do not throw here,
            // that would abort the loop for every other installed tuner.
            return [];
        }

        var url = await _repository.GetStreamUrlAsync(channel.Login, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            return [];
        }

        return [BuildMediaSource(channel, url)];
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var channel = await ResolveForTuningAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            // FileNotFoundException is how a tuner host says "not mine"; the Live TV service catches it
            // and tries the next host.
            throw new FileNotFoundException($"'{channelId}' is not a Twitch Live TV channel.");
        }

        var url = await _repository.GetStreamUrlAsync(channel.Login, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            // Reached only if the channel went offline between the two calls; saying so beats letting
            // ffmpeg fail on usher's 404.
            throw new InvalidOperationException($"'{channel.DisplayName}' is not streaming right now.");
        }

        var mediaSource = BuildMediaSource(channel, url);
        var liveStream = new TwitchLiveStream(mediaSource, TwitchChannelCatalog.HostId)
        {
            OriginalStreamId = streamId
        };

        // The caller does not open the stream - BaseTunerHost normally does this before returning.
        await liveStream.Open(cancellationToken).ConfigureAwait(false);

        // The token in the query string is a credential of sorts and useless in a log line.
        var query = url.IndexOf('?');
        var withoutToken = query == -1 ? url : url[..query];

        _logger.LogInformation(
            "Twitch Live TV: opening {Name} ({Title}) from {Url}",
            channel.DisplayName,
            channel.Status.Title,
            withoutToken);

        return liveStream;
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
    {
        // Nothing to scan for, but offering the one virtual device lets the tuner page add the entry
        // with a click instead of typing something into an empty form.
        return Task.FromResult(new List<TunerHostInfo>
        {
            new()
            {
                Id = TwitchChannelCatalog.HostId,
                Type = Type,
                FriendlyName = Name,
                DeviceId = TwitchChannelCatalog.HostId,

                // Not used for anything - the channels come from the plugin configuration. It has to
                // look like a URL because the web UI validates the field.
                Url = VirtualUrl
            }
        });
    }

    /// <inheritdoc />
    public Task Validate(TunerHostInfo info)
    {
        // The tuner needs no settings: channels come from the plugin's own configuration.
        if (!string.Equals(info.Url, VirtualUrl, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Twitch Live TV: ignoring Url '{Url}' - this tuner does not read a playlist", info.Url);
        }

        // Naming belongs here because this is the only moment the server hands us the device it is about to
        // write: TunerHostManager.SaveTunerHost() validates the very copy of the entry that it then stores in
        // livetv.xml. A source added by picking this plugin in the dropdown arrives without a name - that
        // form only sends one if the field was filled in - and until something names it the tuner list titles
        // the row "Unknown", which reads like a failed install (see LiveTvDevices). Doing it here means the
        // label is right as the page redraws itself instead of after the next guide refresh.
        if (string.IsNullOrWhiteSpace(info.FriendlyName))
        {
            info.FriendlyName = Name;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a channel for tuning, re-checking Twitch once if the cached answer says offline.
    /// </summary>
    /// <remarks>
    /// A tune-in is the moment when being wrong in one direction is most annoying: the cache can be up
    /// to <see cref="PluginConfiguration.StatusCacheSeconds"/> old, and "that streamer is live, I can
    /// see them on my phone" should not fail. The re-check asks about every channel at once, so it costs
    /// one request no matter how many are configured - and only happens for offline ones.
    /// </remarks>
    private async Task<TwitchChannel?> ResolveForTuningAsync(string channelId, CancellationToken cancellationToken)
    {
        var channel = await _repository.FindAsync(channelId, false, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        if (!channel.Status.IsLive)
        {
            channel = await _repository.FindAsync(channelId, true, cancellationToken).ConfigureAwait(false);
        }

        if (channel is null)
        {
            return null;
        }

        if (!channel.Status.IsLive)
        {
            // Not an error to report by exception: handing back no media source is how this plugin says
            // "nothing to play", and usher would only answer 404 anyway.
            _logger.LogInformation("Twitch Live TV: '{Name}' is not streaming, so there is nothing to tune to", channel.DisplayName);
            return null;
        }

        return channel;
    }

    private static MediaSourceInfo BuildMediaSource(TwitchChannel channel, string url)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var headers = new Dictionary<string, string>();

        // With an account token on usher's request, Twitch sees who is watching and can leave the ad
        // markers out of the playlist if that account has a subscription to this channel or Twitch
        // Turbo. Without it the same stream arrives with ads; nothing else about playback changes.
        if (config.UseTokenForPlayback && !string.IsNullOrWhiteSpace(config.AuthToken))
        {
            headers["Authorization"] = "OAuth " + config.AuthToken.Trim();
        }

        // Twitch's CDNs answer a plain request - the playback token is already tied to this machine's IP
        // address - so no header is sent unless an operator asks for one.
        if (!string.IsNullOrWhiteSpace(config.UserAgent))
        {
            headers["User-Agent"] = config.UserAgent;
        }

        var mediaSource = new MediaSourceInfo
        {
            Name = "Twitch HLS",
            Path = url,
            Protocol = MediaProtocol.Http,
            Id = StableId(url),
            IsInfiniteStream = true,
            IsRemote = true,
            RequiresOpening = true,
            RequiresClosing = true,
            ReadAtNativeFramerate = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            RequiredHttpHeaders = headers,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    // -1: the position inside the container is not known before probing.
                    Index = -1,
                    IsInterlaced = false
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = -1
                }
            ]
        };

        mediaSource.InferTotalBitrate();
        return mediaSource;
    }

    /// <summary>
    /// Builds the tags for one channel.
    /// </summary>
    /// <remarks>
    /// Jellyfin copies these onto the library item (<c>GuideManager</c> does
    /// <c>item.Tags = channelInfo.Tags</c>) and never reads <c>ChannelGroup</c>, so a tag is the only
    /// thing a viewer can actually filter on. "followed" marks channels that arrived through the
    /// account's follow list rather than through the settings box, which makes an imported channel
    /// recognisable without opening the settings again - and shows who renumbered the guide.
    /// </remarks>
    /// <param name="isLive">Whether the channel is streaming right now.</param>
    /// <param name="fromFollows">Whether this login came from the follow list.</param>
    /// <returns>The tags to offer.</returns>
    private static string[] BuildTags(bool isLive, bool fromFollows)
        => fromFollows ? [isLive ? "live" : "offline", "followed"] : [isLive ? "live" : "offline"];

    /// <summary>A stable, filesystem-safe id for a URL (MediaSourceInfo.Id is used as a cache key).</summary>
    private static string StableId(string url)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
}
