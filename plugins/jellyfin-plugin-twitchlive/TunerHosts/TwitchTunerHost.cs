using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    private readonly TwitchChannelRepository _repository;
    private readonly ILogger<TwitchTunerHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="TwitchTunerHost"/> class.</summary>
    public TwitchTunerHost(TwitchChannelRepository repository, ILogger<TwitchTunerHost> logger)
    {
        _repository = repository;
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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = await _repository.GetChannelsAsync(!enableCache, cancellationToken).ConfigureAwait(false);

        var result = new List<ChannelInfo>(channels.Count);
        var number = 1;

        foreach (var channel in channels)
        {
            var isLive = channel.Status.IsLive;
            if (!isLive && !config.IncludeOfflineChannels)
            {
                continue;
            }

            result.Add(new ChannelInfo
            {
                // The bare login. Everything the server asks for later is looked up by this id.
                Id = channel.Login,
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

                // A listings provider that is not enabled "for all tuners" filters on this and throws if
                // it is empty, so it always has to be set.
                TunerHostId = TwitchChannelCatalog.HostId,

                ChannelType = ChannelType.TV,
                ChannelGroup = isLive ? TwitchChannelCatalog.LiveGroup : TwitchChannelCatalog.OfflineGroup,

                // Nothing is being broadcast while a channel is offline, so its picture format is not
                // known either.
                IsHD = isLive ? true : null,

                Tags = [isLive ? "live" : "offline"]
            });
        }

        _logger.LogDebug("Twitch Live TV: offering {Count} of {Configured} configured channels", result.Count, channels.Count);
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

    /// <summary>A stable, filesystem-safe id for a URL (MediaSourceInfo.Id is used as a cache key).</summary>
    private static string StableId(string url)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
}
