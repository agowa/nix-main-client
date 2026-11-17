using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// A live channel carried by the ZDF live TV platform (https://www.zdf.de/live-tv).
/// </summary>
/// <param name="Key">Channel key in the MediathekView Zapp API catalogue.</param>
/// <param name="Name">Display name shown in Jellyfin.</param>
/// <param name="EpgId">Channel id in the German XMLTV guide, used to attach programme data.</param>
/// <param name="FallbackUrl">Last known master playlist, used only if the API cannot be reached.</param>
/// <param name="EnabledByDefault">Whether the channel is on without any configuration.</param>
/// <param name="Group">Jellyfin channel group.</param>
/// <param name="IsHd">Recorded as the channel's HD flag.</param>
/// <param name="LogoId">
/// The id the logo host knows this channel under. Usually its guide id, but not always: that host keys
/// on its own catalogue and spells a few of them differently ("ARD.de" for Das Erste, "SWRSR.de" for the
/// SWR/SR common programme). Ignored when <see cref="LogoUrl"/> is set.
/// </param>
/// <param name="LogoUrl">
/// A complete logo URL for a channel the template's host does not carry (3sat and NDR have no image at
/// epg.best). Beats the template plus id, but still yields to an explicit URL from the settings and to
/// switching logos off there.
/// </param>
public sealed record ZdfChannelDefinition(
    string Key,
    string Name,
    string EpgId,
    string FallbackUrl,
    bool EnabledByDefault,
    string Group = "ZDF Live",
    bool IsHd = true,
    string? LogoId = null,
    string? LogoUrl = null);

/// <summary>
/// The channel catalogue. Metadata is curated here on purpose: the upstream API's own names are
/// unreliable (it answers "NRD Niedersachsen" for NDR), while its stream URLs rotate and are looked
/// up at runtime by <see cref="ZdfChannelRepository"/>.
/// </summary>
public static class ZdfChannelCatalog
{
    /// <summary>Tuner host id reported to Jellyfin; also the key listings providers enable per tuner.</summary>
    public const string HostId = "zdf";

    private const string ZdfGroup = "ZDF Live";
    private const string ArdGroup = "ARD Live";

    /// <summary>The seven channels that https://www.zdf.de/live-tv offers, in the order the page lists them.</summary>
    public static readonly IReadOnlyList<ZdfChannelDefinition> All =
    [
        new("zdf", "ZDF", "ZDF.de",
            "https://zdf-hls-15.akamaized.net/hls/live/2016498/de/high/master.m3u8", true),
        new("zdf_neo", "ZDFneo", "ZDFneo.de",
            "https://zdf-hls-16.akamaized.net/hls/live/2016499/de/high/master.m3u8", true),
        new("zdf_info", "ZDFinfo", "ZDFinfo.de",
            "https://zdf-hls-17.akamaized.net/hls/live/2016500/de/high/master.m3u8", true),
        new("dreisat", "3sat", "3sat.de",
            "https://zdf-hls-18.akamaized.net/hls/live/2016501/dach/high/master.m3u8", true),
        new("phoenix", "phoenix", "PHOENIX.de",
            "https://zdf-hls-19.akamaized.net/hls/live/2016502/de/high/master.m3u8", true, LogoId: "Phoenix.de"),
        new("arte", "ARTE", "ARTE.de",
            "https://artesimulcast.akamaized.net/hls/live/2030993/artelive_de/index.m3u8", true),
        new("kika", "KiKA", "KIKA.de",
            "https://kikageohls.akamaized.net/hls/live/2022693/livetvkika_de/master.m3u8", true),

        // Not part of the ZDF offering, but delivered by the same API and therefore nearly free.
        new("das_erste", "Das Erste", "Das.Erste.de",
            "https://daserste-live.ard-mcdn.de/daserste/live/hls/de/master.m3u8", true, ArdGroup, LogoId: "ARD.de"),
        new("tagesschau24", "tagesschau24", "tagesschau24.de",
            "https://tagesschau.akamaized.net/hls/live/2020117/tagesschau/tagesschau_3/master.m3u8", true, ArdGroup, LogoId: "Tagesschau24.de"),
        new("ard_alpha", "ARD-alpha", "ARD.alpha.de",
            "https://mcdn.br.de/br/fs/ard_alpha/hls/de/master.m3u8", true, ArdGroup, LogoId: "ARDalpha.de"),
        new("wdr", "WDR", "WDR.de",
            "https://wdr-live.ard-mcdn.de/wdr/live/hls/de/master.m3u8", true, ArdGroup),
        new("mdr_sachsen", "MDR Sachsen", "MDR.de",
            "https://mdrtvsnhls.akamaized.net/hls/live/2016928/mdrtvsn/master.m3u8", true, ArdGroup),
        new("br_nord", "BR Nord", "BR.de",
            "https://mcdn.br.de/br/fs/bfs_nord/hls/de/master.m3u8", true, ArdGroup),
        new("hr", "HR", "HR.de",
            "https://hr-live.ard-mcdn.de/hr/live/hls/de/master.m3u8", true, ArdGroup),
        new("rbb_berlin", "rbb Berlin", "RBB.de",
            "https://rbb-hls-berlin.akamaized.net/hls/live/2017824/rbb_berlin/index.m3u8", true, ArdGroup),
        new("ndr_nds", "NDR Niedersachsen", "NDR.de",
            "https://mcdn.ndr.de/ndr/hls/ndr_fs/ndr_nds/master.m3u8", true, ArdGroup),
        new("swr_bw", "SWR Baden-Württemberg", "SWR/SR.de",
            "https://swrbwd-hls.akamaized.net/hls/live/2018672/swrbwd/master.m3u8", true, ArdGroup, LogoId: "SWRSR.de")
    ];

    /// <summary>Keys of the channels that are on unless the user configured otherwise.</summary>
    public static string[] DefaultKeys
        => All.Where(c => c.EnabledByDefault).Select(c => c.Key).ToArray();

    /// <summary>
    /// Builds the logo URL for one channel.
    /// </summary>
    /// <remarks>
    /// The German XMLTV guide this plugin's channels are matched against carries no icons at all, so
    /// artwork has to come from somewhere else; epg.best publishes a logo per guide id and is what the
    /// default template points at. It does not carry every channel, so an entry may name its own source
    /// (<see cref="ZdfChannelDefinition.LogoUrl"/>). Both the template and a per-channel URL can be
    /// changed in the settings page, because a host that disappears should not cost more than one setting.
    /// </remarks>
    /// <param name="definition">The channel.</param>
    /// <param name="template">A URL containing <c>{id}</c>, or empty for the built-in default.</param>
    /// <param name="overrideUrl">An explicit URL for this channel, winning over everything else.</param>
    /// <returns>The URL to offer as the channel's logo, or <c>null</c> when logos are switched off.</param>
    public static string? LogoFor(ZdfChannelDefinition definition, string template, string? overrideUrl)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.Trim();
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            // An empty template means "no logos"; the settings page offers it as such. It wins over a
            // URL baked into the catalogue below, so one setting really is enough to switch artwork off.
            return null;
        }

        if (!string.IsNullOrWhiteSpace(definition.LogoUrl))
        {
            // The host the template points at does not carry every channel; those entries name their own
            // source instead of being handed a URL that would 404 (see VerifyLogosAsync for why that is
            // worth avoiding: Jellyfin keeps a channel's first image and never replaces it).
            return definition.LogoUrl.Trim();
        }

        if (!template.Contains("{id}", StringComparison.Ordinal))
        {
            return null;
        }

        // A channel that only the stream API knows about has no guide id, and asking the logo host for
        // an empty id would build a URL that cannot exist (".../de/.png").
        var id = definition.LogoId ?? definition.EpgId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return template.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares the embedded channel list that the settings page renders against this catalogue, and
    /// says so when they disagree.
    /// </summary>
    /// <remarks>
    /// The JSON has to be a static embedded resource - /web/ConfigurationPage only serves resources out
    /// of the plugin's own assembly (DashboardController.GetDashboardConfigurationPage), so it cannot be
    /// generated from this list at request time. That leaves two sources for one fact, and this check is
    /// what stops them from drifting apart quietly: a channel added here but not there simply does not
    /// appear in the settings page, which looks like a bug in the page rather than a missing line.
    /// </remarks>
    /// <param name="logger">Where to report a mismatch.</param>
    public static void VerifyEmbeddedList(ILogger logger)
    {
        const string Resource = "Jellyfin.Plugin.ZdfLive.Configuration.zdfCatalog.json";

        using var stream = typeof(ZdfChannelCatalog).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) {
            logger.LogWarning("ZDF Live TV: the embedded channel list '{Resource}' is missing, so the settings page cannot list channels", Resource);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(stream);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in document.RootElement.GetProperty("channels").EnumerateArray())
            {
                var key = entry.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    seen.Add(key!);

                    if (Find(key) is null)
                    {
                        logger.LogWarning("ZDF Live TV: the embedded channel list knows '{Key}' but the catalogue does not", key);
                    }
                }
            }

            foreach (var definition in All)
            {
                if (!seen.Contains(definition.Key))
                {
                    logger.LogWarning("ZDF Live TV: '{Key}' is in the catalogue but missing from the embedded channel list, so it cannot be ticked in the settings page", definition.Key);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ZDF Live TV: could not read the embedded channel list");
        }
    }

    /// <summary>Looks a definition up by key, ignoring case and surrounding whitespace.</summary>
    public static ZdfChannelDefinition? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
