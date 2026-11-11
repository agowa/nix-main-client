using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive.TunerHosts;

/// <summary>
/// A tuner host whose "antenna" is the public internet: it offers the ZDF live channels as Jellyfin
/// Live TV channels and points Jellyfin at the broadcasters' own HLS playlists. No proxy, no playlist
/// files, no hardware.
/// </summary>
public sealed class ZdfTunerHost : ITunerHost, IConfigurableTunerHost
{
    private readonly ZdfChannelRepository _repository;
    private readonly ILogger<ZdfTunerHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="ZdfTunerHost"/> class.</summary>
    public ZdfTunerHost(ZdfChannelRepository repository, ILogger<ZdfTunerHost> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ZDF Live TV";

    /// <inheritdoc />
    // Also the value stored in TunerHostInfo.Type and the id used by DiscoverDevices().
    public string Type => ZdfChannelCatalog.HostId;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        var resolved = await _repository.GetChannelsAsync(!enableCache, cancellationToken).ConfigureAwait(false);

        var channels = new List<ChannelInfo>(resolved.Count);
        var number = 1;

        foreach (var channel in resolved)
        {
            var definition = channel.Definition;

            channels.Add(new ChannelInfo
            {
                // The bare catalogue key. Everything else the server later asks for is looked up by this id.
                Id = definition.Key,
                Name = definition.Name,

                // Cosmetic: positions in the guide. Programme data is matched by TunerChannelId below,
                // never by these numbers.
                Number = (number++).ToString(CultureInfo.InvariantCulture),

                Path = channel.Url,

                // The id this channel has in the German XMLTV guide. Jellyfin tries ChannelInfo.Id first
                // and TunerChannelId second when it looks for programme data, so setting it here is what
                // attaches the EPG without any manual channel mapping.
                TunerChannelId = definition.EpgId,

                CallSign = definition.Name,

                // A listings provider that is not enabled "for all tuners" filters on this and throws if
                // it is empty, so it always has to be set.
                TunerHostId = ZdfChannelCatalog.HostId,

                ChannelType = ChannelType.TV,
                ChannelGroup = definition.Group,
                IsHD = definition.IsHd
            });
        }

        _logger.LogDebug("ZDF Live TV: offering {Count} channels", channels.Count);
        return channels;
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = await _repository.ResolveAsync(channelId, cancellationToken).ConfigureAwait(false);

        // An empty list tells the Live TV service to ask the next tuner host - do not throw here,
        // that would abort the loop for every other installed tuner.
        if (channel is null)
        {
            return [];
        }

        return [BuildMediaSource(channel)];
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var channel = await _repository.ResolveAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null)
        {
            // FileNotFoundException is how a tuner host says "not mine"; the Live TV service catches it
            // and tries the next host.
            throw new FileNotFoundException($"'{channelId}' is not a ZDF Live TV channel.");
        }

        var mediaSource = BuildMediaSource(channel);
        var liveStream = new ZdfLiveStream(mediaSource, ZdfChannelCatalog.HostId)
        {
            OriginalStreamId = streamId
        };

        // The caller does not open the stream - BaseTunerHost normally does this before returning.
        await liveStream.Open(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("ZDF Live TV: opening {Name} from {Url}", channel.Definition.Name, channel.Url);
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
                Id = ZdfChannelCatalog.HostId,
                Type = Type,
                FriendlyName = Name,
                DeviceId = ZdfChannelCatalog.HostId,

                // Not used for anything - the channel URLs come from the repository. It has to look like
                // a URL because the web UI validates the field.
                Url = "https://www.zdf.de/live-tv"
            }
        });
    }

    /// <inheritdoc />
    public Task Validate(TunerHostInfo info)
    {
        // The tuner needs no settings: channels and URLs come from the plugin configuration.
        if (!string.Equals(info.Url, "https://www.zdf.de/live-tv", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("ZDF Live TV: ignoring Url '{Url}' - this tuner does not read a playlist", info.Url);
        }

        return Task.CompletedTask;
    }

    private static MediaSourceInfo BuildMediaSource(ResolvedChannel channel)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        var headers = new Dictionary<string, string>
        {
            [HeaderNames.UserAgent] = config.UserAgent
        };

        // The ZDF-operated CDNs are the only ones that have been seen to want a Referer.
        if (config.SendRefererHeader && channel.Url.Contains("zdf-hls-", StringComparison.OrdinalIgnoreCase))
        {
            headers[HeaderNames.Referer] = "https://www.zdf.de/";
        }

        var mediaSource = new MediaSourceInfo
        {
            Path = channel.Url,
            Protocol = MediaProtocol.Http,
            Id = StableId(channel.Url),
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
                    IsInterlaced = true
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

    private static class HeaderNames
    {
        public const string UserAgent = "User-Agent";
        public const string Referer = "Referer";
    }
}
