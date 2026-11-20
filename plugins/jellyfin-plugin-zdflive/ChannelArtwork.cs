using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// Corrects the picture a channel carries when that picture no longer exists.
/// </summary>
/// <remarks>
/// The server writes a channel's primary image once and never looks at it again:
/// <c>GuideManager.UpdateChannel()</c> only touches artwork inside <c>if (!item.HasImage(ImageType.Primary))</c>.
/// Handing out an address that turns out not to exist is therefore permanent, and so is every later fix -
/// a channel whose image was stored while the plugin still pointed 3sat and NDR at logo addresses that
/// answer 404 keeps showing a broken tile however often the guide is refreshed. The web client has no
/// fallback for that case either: an item with an image reference that will not load shows the broken
/// picture rather than the letter tile it would have used with no image at all.
/// <para>
/// So this class does the one thing the server will not, and only that - it compares what a channel is
/// holding against what this plugin offers now, and steps in when what it holds is <em>missing</em> (a URL
/// answering 403/404/410, or a file that is gone) or when it is an address for artwork the plugin has since
/// started shipping itself. Anything else is left alone, including
/// artwork from an older source that still loads (Jellyfin downloads a remote logo into its own metadata
/// folder the first time it is served, so "different from what we offer" is normal and not a defect) and
/// anything an operator set by hand. A channel with no image at all is left to <c>GuideManager</c>, which
/// fills those in itself from <see cref="MediaBrowser.Controller.LiveTv.ChannelInfo"/>.
/// </para>
/// </remarks>
public sealed class ChannelArtwork
{
    /// <summary>How long a pass that found nothing to say about the current offering stays valid.</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(15);

    private readonly ILibraryManager _libraryManager;
    private readonly ZdfChannelRepository _repository;
    private readonly ILogger<ChannelArtwork> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What was offered the last time this ran, and when that was.</summary>
    private string? _lastOffered;

    private DateTime _lastRunUtc = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="ChannelArtwork"/> class.</summary>
    public ChannelArtwork(ILibraryManager libraryManager, ZdfChannelRepository repository, ILogger<ChannelArtwork> logger)
    {
        _libraryManager = libraryManager;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Replaces the artwork of this plugin's channels that points at something which is not there.
    /// </summary>
    /// <param name="logosByChannelId">The logo currently offered per channel id, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once every affected channel has been written back.</returns>
    public async Task RepairAsync(IReadOnlyDictionary<string, string?> logosByChannelId, CancellationToken cancellationToken)
    {
        // Called from the channel list, which the guide refresh and the tuner page both ask for. Two of
        // them at once would only repeat each other's work, and neither may wait for the other.
        if (logosByChannelId.Count == 0 || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // The Live TV service asks for the channel list several times per refresh, and reading every
            // live TV channel in the library is not free, so a pass that would look at exactly the same
            // pictures it looked at a moment ago is skipped. A settings change alters what is offered and
            // therefore gets through straight away.
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
            _logger.LogWarning(ex, "ZDF Live TV: could not check the channels' artwork");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RepairCoreAsync(IReadOnlyDictionary<string, string?> logosByChannelId, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery {
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

            // Nothing stored yet: GuideManager gives such a channel the logo it is being offered right now.
            if (current is null || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (string.Equals(path.Trim(), wanted?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Second trigger, the one that has to act before anything is broken: this plugin used to hand out
            // addresses for channels it now carries a file for. Such an address may load perfectly well today,
            // but every display of it is a request to somebody else's server, and the first failure costs both
            // the tile and the stored reference (see IsMissingAsync) - which is exactly how 3sat and ONE ended
            // up showing nothing usable. A file in our own folder cannot fail that way, so move onto it; a
            // file of ours under a different name is an older version of itself for the same reason. What is
            // left alone is artwork this plugin never offered: a poster Jellyfin downloaded into the channel's
            // metadata folder, or something an operator set by hand.
            var migrating = wanted is not null
                            && !ZdfChannelCatalog.IsRemote(wanted)
                            && (ZdfChannelCatalog.IsRemote(path)
                                || string.Equals(
                                    Path.GetDirectoryName(path),
                                    Path.GetDirectoryName(wanted),
                                    StringComparison.OrdinalIgnoreCase));

            if (!migrating && !await IsMissingAsync(path!, cancellationToken).ConfigureAwait(false))
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

            if (migrating)
            {
                _logger.LogInformation(
                    "ZDF Live TV: '{Channel}' was still pointing at {Old}; this plugin carries that picture itself now, so it uses {New}",
                    item.Name,
                    path,
                    wanted!);
            }
            else
            {
                _logger.LogInformation(
                    "ZDF Live TV: '{Channel}' was still pointing at {Old}, which is not there; it now uses {New}",
                    item.Name,
                    path,
                    string.IsNullOrWhiteSpace(wanted) ? "no picture of its own" : wanted!);
            }
        }

        if (repaired > 0)
        {
            _logger.LogInformation("ZDF Live TV: replaced the artwork of {Count} channel(s)", repaired);
        }
    }

    /// <summary>Says whether an image reference the server is holding cannot be loaded.</summary>
    private async Task<bool> IsMissingAsync(string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
            return !File.Exists(path);
        }

        // The same verdict the channel list itself is built on, so a logo host that is merely unreachable
        // right now does not talk us out of a picture that works: only 403/404/410 count as missing.
        return !await _repository.LogoExistsAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
