using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// A live stream that is nothing but a remote URL.
/// </summary>
/// <remarks>
/// Nothing is downloaded here. The media source keeps Protocol=Http and the playlist URL in Path, and
/// because this class deliberately does not implement <c>IDirectStreamProvider</c>, Jellyfin passes
/// <see cref="MediaSourceInfo.Path"/> to jellyfin-ffmpeg instead of piping bytes through the server -
/// which is exactly how the built-in M3U tuner plays HLS too (its <c>LiveStream.GetStream()</c> reads a
/// temporary file that only the shared-stream path ever writes). Letting ffmpeg consume the playlist is
/// also what makes adaptive bitrate switching work on the client's behalf.
/// </remarks>
public sealed class ZdfLiveStream : ILiveStream
{
    /// <summary>Initializes a new instance of the <see cref="ZdfLiveStream"/> class.</summary>
    public ZdfLiveStream(MediaSourceInfo mediaSource, string tunerHostId)
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
