using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Corrects the picture one of this plugin's channels is holding when that picture is no longer the streamer's.
/// </summary>
/// <remarks>
/// A Twitch account's picture moves whenever its owner uploads a new one, and the address it used stops being
/// served. The server would not notice: <c>GuideManager.UpdateChannel()</c> only touches artwork inside
/// <c>if (!item.HasImage(ImageType.Primary))</c>, so whatever a channel was given the first time is what it keeps -
/// which makes handing out an address a permanent commitment, and makes every later fix permanent too. A dead
/// address is worse than merely stale, because <c>ImageController.GetImage()</c> converts a remote image to local
/// with <c>removeOnFailure: true</c>: the first request for such a tile answers an error *and* drops the stored
/// reference, and the web client has no fallback for an item whose image will not load - it shows the broken
/// picture instead of the letter tile it would have drawn with no image at all.
/// <para>
/// So this class does the one thing the server will not, and only that: it compares what a channel holds against
/// what this plugin offers now, and writes the difference when one of three things is true - what it holds is
/// <em>missing</em> (an address answering 403/404/410, or a file that is gone), it is <em>this plugin's own artwork
/// under an older name</em> (a Twitch CDN address, or a file from this plugin's picture folder), or it is a copy
/// the server downloaded into <em>this channel's own image folder</em> while the plugin was still handing out
/// addresses. Everything else is left alone, and a channel with no image at all is left to <c>GuideManager</c>,
/// which fills those in itself from <see cref="ChannelInfo"/>.
/// </para>
/// <para>
/// The second trigger is what keeps yesterday's avatar off the guide once <see cref="AvatarAssets"/> holds the
/// current one: an address on Twitch's CDN can only have got there through this plugin, so replacing it with what
/// the plugin offers now is not a guess. The third is the same statement about installs that predate
/// <see cref="AvatarAssets"/>, where the address has already been fetched and stored as a file - see
/// <see cref="IsChannelOwnImageFolder"/> for why that one is taken despite being artwork this plugin did not put
/// there, and note that it only ever replaces a picture with a current picture of the same streamer.
/// </para>
/// </remarks>
public sealed class ChannelArtwork
{
    /// <summary>How long a pass that found nothing new to offer stays valid.</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(15);

    /// <summary>How long to wait after startup before asking for the channel list (see <see cref="QueueAfterStart"/>).</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly ILibraryManager _libraryManager;
    private readonly AvatarAssets _avatars;
    private readonly ILogger<ChannelArtwork> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What was offered the last time this ran, and when that was.</summary>
    private string? _lastOffered;

    private DateTime _lastRunUtc = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="ChannelArtwork"/> class.</summary>
    /// <param name="libraryManager">Used to read and write the channel items in the library.</param>
    /// <param name="avatars">This plugin's own pictures, and the only thing it will replace artwork with.</param>
    /// <param name="logger">The logger.</param>
    public ChannelArtwork(ILibraryManager libraryManager, AvatarAssets avatars, ILogger<ChannelArtwork> logger)
    {
        _libraryManager = libraryManager;
        _avatars = avatars;
        _logger = logger;
    }

    /// <summary>
    /// Asks for one channel list shortly after startup, so a correction this code makes runs without waiting.
    /// </summary>
    /// <remarks>
    /// Everything in here needs the channel list, and the server only produces one when its guide task runs -
    /// whose only default trigger is an interval of 24 hours measured from the last run, with no trigger at
    /// startup (<c>RefreshGuideScheduledTask.GetDefaultTriggers()</c>). So after a plugin update that changes
    /// artwork, or after a streamer changed their picture while the server was down, an operator could restart and
    /// still look at yesterday's tile for most of a day. Asking for this plugin's own channel list is enough: it
    /// fetches what is missing and runs this repair over the channels already in the library, without touching the
    /// guide itself - unlike <see cref="GuideRefresh"/>, which is what a settings change needs.
    /// </remarks>
    /// <param name="applicationHost">Used to reach this plugin's tuner host once the container is built.</param>
    /// <param name="logger">Where to say what happened, if anything goes wrong.</param>
    public static void QueueAfterStart(IApplicationHost applicationHost, ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Not immediately: the plugin is constructed while the server is still starting up, and its
                // services - including this plugin's tuner host - are only reachable once the container is built.
                await Task.Delay(StartupDelay).ConfigureAwait(false);

                var tuner = applicationHost.ServiceProvider?.GetServices<ITunerHost>()
                    .FirstOrDefault(h => string.Equals(h.Type, TwitchChannelCatalog.HostId, StringComparison.OrdinalIgnoreCase));

                if (tuner is null)
                {
                    // Only reachable if the plugin's own services were never registered, i.e. Live TV is off.
                    logger.LogDebug("Twitch Live TV: no tuner host to ask for a channel list after startup");
                    return;
                }

                // Cached answers are fine here - what this pass is for is the artwork, and Twitch's own cache is
                // short (StatusCacheSeconds). With no device configured the call returns an empty list.
                await tuner.GetChannels(enableCache: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Twitch Live TV: could not check the channels' artwork after startup; it will be checked with the next guide refresh");
            }
        });
    }

    /// <summary>
    /// Replaces the artwork of this plugin's channels that is not what they should be showing.
    /// </summary>
    /// <param name="logosByChannelId">What is offered per channel id - a file or an address, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once every affected channel has been written back.</returns>
    public async Task RepairAsync(IReadOnlyDictionary<string, string?> logosByChannelId, CancellationToken cancellationToken)
    {
        // Called from the channel list, which the guide refresh and the tuner page both ask for. Two of them at
        // once would only repeat each other's work, and neither may wait for the other.
        if (logosByChannelId.Count == 0 || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // The Live TV service asks for the channel list several times per refresh, and reading every live TV
            // channel in the library is not free, so a pass that would look at exactly the same pictures it looked
            // at a moment ago is skipped. A streamer changing their picture alters what is offered and therefore
            // gets through straight away - which is the point.
            var offered = string.Join("|", logosByChannelId.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value));

            if (string.Equals(offered, _lastOffered, StringComparison.Ordinal)
                && DateTime.UtcNow - _lastRunUtc < Throttle)
            {
                return;
            }

            _lastOffered = offered;
            _lastRunUtc = DateTime.UtcNow;

            await RepairCoreAsync(logosByChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Artwork is the least important part of a channel list; never let it cost the channels.
            _logger.LogWarning(ex, "Twitch Live TV: could not check the channels' artwork");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RepairCoreAsync(IReadOnlyDictionary<string, string?> logosByChannelId, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            DtoOptions = new DtoOptions(true)
        });

        var repaired = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The channel id this plugin handed out is what the server stores as ExternalId.
            if (string.IsNullOrEmpty(item.ExternalId) || !logosByChannelId.TryGetValue(item.ExternalId, out var wanted))
            {
                continue;
            }

            var current = item.GetImageInfo(ImageType.Primary, 0);
            var path = current?.Path;

            // Nothing stored yet: GuideManager gives such a channel the picture it is being offered right now.
            if (current is null || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (string.Equals(path.Trim(), wanted?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Second trigger, and the one that has to act before anything breaks: this channel still holds an
            // address into Twitch's CDN, or a file of this plugin's own folder. Both can only have come from an
            // earlier list - so they are the same streamer's picture at an older address, which is either about to
            // stop resolving or has already been replaced by the current one.
            var ours = _avatars.IsOwnPicture(path) || IsTwitchAvatarAddress(path!);

            // Third trigger: a file the server downloaded for THIS channel when it was still being handed an
            // address, which is where every install predating AvatarAssets sits. This is the one case where this
            // plugin overwrites artwork it did not put there itself, and it is taken because of what a Live TV
            // channel is: SourceType.LiveTV, no image providers, nothing on disk scanned for metadata, so the only
            // ways anything gets into that folder are the server converting what a tuner offered (which is what
            // this finds) and SetItemImage called by somebody who went looking for it deliberately. Leaving it
            // alone would be the safer rule but not the useful one - such a channel shows the avatar from the day
            // its tile was first opened, however many times the streamer changes it afterwards. Only replaced with
            // a picture this plugin holds itself, never with nothing, and always said out loud below.
            var serverCopy = !ours
                             && wanted is not null
                             && !AvatarAssets.IsRemoteAddress(wanted)
                             && IsChannelOwnImageFolder(item, path!);

            if (!ours && !serverCopy && !await IsMissingAsync(path!, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(wanted))
            {
                // In memory only - the file itself belongs to Jellyfin and is not ours to delete.
                item.RemoveImage(current!);
            }
            else
            {
                item.SetImage(new ItemImageInfo { Path = wanted, Type = ImageType.Primary }, 0);
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);

            repaired++;

            if (ours || serverCopy)
            {
                _logger.LogInformation(
                    "Twitch Live TV: '{Channel}' was still pointing at {Old}; it now uses the copy this plugin keeps ({New})",
                    item.Name,
                    path,
                    wanted!);
            }
            else
            {
                _logger.LogInformation(
                    "Twitch Live TV: '{Channel}' was still pointing at {Old}, which is not there; it now uses {New}",
                    item.Name,
                    path,
                    string.IsNullOrWhiteSpace(wanted) ? "no picture of its own" : wanted!);
            }
        }

        if (repaired > 0)
        {
            _logger.LogInformation("Twitch Live TV: replaced the artwork of {Count} channel(s)", repaired);
        }
    }

    /// <summary>Says whether an image reference the server is holding cannot be loaded.</summary>
    private async Task<bool> IsMissingAsync(string path, CancellationToken cancellationToken)
    {
        if (!AvatarAssets.IsRemoteAddress(path))
        {
            return !File.Exists(path);
        }

        // The same verdict the picture fetching is built on, so a CDN that is merely unreachable right now does
        // not talk us out of a picture that works: only 403/404/410 count as missing.
        return !await _avatars.IsStillServedAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Says whether an image path sits in the folder the server keeps this channel's own images in.
    /// </summary>
    /// <remarks>
    /// <c>LiveTvChannel.GetInternalMetadataPath()</c> is <c>&lt;internalMetadata&gt;/livetv/&lt;id&gt;/metadata</c>, which
    /// is where <c>ImageSaver</c> put a picture the server fetched on demand - as opposed to this plugin's folder,
    /// which <see cref="AvatarAssets.IsOwnPicture"/> already recognises. Asking for this channel's own path rather
    /// than for any folder under the metadata root keeps the comparison exact and needs no knowledge of how the
    /// server names those directories.
    /// </remarks>
    private static bool IsChannelOwnImageFolder(BaseItem item, string path)
    {
        if (AvatarAssets.IsRemoteAddress(path))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return !string.IsNullOrEmpty(directory)
               && string.Equals(directory, item.GetInternalMetadataPath(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Says whether an address is one this plugin would have handed out as a streamer's picture.
    /// </summary>
    /// <remarks>
    /// Twitch serves every account picture from its own CDN (<c>static-cdn.jtvnw.net</c>, and the older hosts under
    /// the same domain), which no other tuner on this server has any reason to point at. Matching on the host
    /// rather than on the whole address is what makes an avatar change visible: the path carries a new identifier
    /// every time, so comparing addresses would only ever say "different", and asking whether the old one still
    /// resolves says nothing about whether it is <em>current</em>.
    /// </remarks>
    private static bool IsTwitchAvatarAddress(string path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;

        return host.EndsWith("jtvnw.net", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("twitchcdn.net", StringComparison.OrdinalIgnoreCase);
    }
}
