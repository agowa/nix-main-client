using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// A live stream that is nothing but a remote playlist URL.
/// </summary>
/// <remarks>
/// Nothing is downloaded here. The media source keeps Protocol=Http and the usher URL in Path, and
/// because this class deliberately does not implement <c>IDirectStreamProvider</c>, Jellyfin passes
/// <see cref="MediaSourceInfo.Path"/> to jellyfin-ffmpeg instead of piping bytes through the server -
/// the same route the built-in M3U tuner takes for HLS. That also matters for Twitch specifically: the
/// playlist is adaptive, so it is ffmpeg that picks and switches renditions while watching, not a
/// fixed quality chosen here.
/// </remarks>
public sealed class TwitchLiveStream : ILiveStream
{
    /// <summary>Initializes a new instance of the <see cref="TwitchLiveStream"/> class.</summary>
    public TwitchLiveStream(MediaSourceInfo mediaSource, string tunerHostId)
    {
        MediaSource = mediaSource;
        TunerHostId = tunerHostId;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        ConsumerCount = 1;
    }

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; } = string.Empty;

    /// <inheritdoc />
    public string TunerHostId { get; }

    /// <inheritdoc />
    public bool EnableStreamSharing { get; private set; } = true;

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <summary>Gets the moment the stream was opened, for logging.</summary>
    public DateTime DateOpened { get; private set; }

    /// <inheritdoc />
    public Task Close()
    {
        EnableStreamSharing = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
        => throw new NotSupportedException(
            "This live stream is a remote playlist; Jellyfin plays it by handing MediaSource.Path to the encoder.");

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken)
    {
        DateOpened = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: no connection is held by the stream itself.
    }
}
