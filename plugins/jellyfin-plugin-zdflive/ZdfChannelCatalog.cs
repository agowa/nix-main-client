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
/// SWR/SR common programme). Ignored when <see cref="LogoAsset"/> is set.
/// </param>
/// <param name="LogoAsset">
/// File name of a logo that comes inside this plugin's assembly instead of being fetched from anywhere -
/// used for the channels the template's host does not carry at all (3sat, everything NDR makes, the two
/// smallest ARD members and the Bundestag's own two). Beats the template plus id, but still yields to an
/// explicit URL from the settings page and to switching logos off there. See <see cref="ChannelLogoAssets"/>
/// for where the file ends up on disk and Assets/SOURCES.md for where it came from.
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
    string? LogoAsset = null);

/// <summary>
/// The channel catalogue. Metadata is curated here on purpose: the upstream API's own names are
/// unreliable (it answers "NRD Niedersachsen" for NDR), while its stream URLs rotate and are looked
/// up at runtime by <see cref="ZdfChannelRepository"/>.
/// </summary>
public static class ZdfChannelCatalog
{
    /// <summary>Tuner host id reported to Jellyfin; also the key listings providers enable per tuner.</summary>
    public const string HostId = "zdf";

    // The logos of the channels no logo host carries, shipped inside the assembly (Assets/, sources listed in
    // Assets/SOURCES.md). Named here instead of spelled out per entry so that a channel asking for a file
    // that is not in there cannot quietly end up without a picture.
    private const string Logo3sat = "logo_3sat.png";
    private const string LogoNdr = "logo_ndr.png";
    private const string LogoOne = "logo_one.png";
    private const string LogoRadioBremen = "logo_radio_bremen.png";
    private const string LogoSR = "logo_sr.png";
    private const string LogoBundestag = "logo_bundestag.png";

    private const string ZdfGroup = "ZDF Live";
    private const string ArdGroup = "ARD Live";

    /// <summary>Where the channels that are neither ZDF's nor an ARD Landesrundfunkanstalt's end up.</summary>
    private const string PlatformGroup = "ARD/ZDF Live";

    /// <summary>
    /// Every channel this plugin names itself, grouped and ordered the way the settings page shows them.
    /// </summary>
    /// <remarks>
    /// The first seven are what https://www.zdf.de/live-tv offers; the rest arrive through the same API.
    /// A channel missing from here is not lost - the stream API's own list is what the settings page can
    /// offer at all (see <see cref="ZdfChannelRepository.GetKnownChannelsAsync"/>) - but an unlisted one
    /// has no guide id, no artwork and whatever name the API happens to send, which for NDR is
    /// "NRD Niedersachsen" and for the parliamentary channels contains soft hyphens.
    /// </remarks>
    public static readonly IReadOnlyList<ZdfChannelDefinition> All =
    [
        new("zdf", "ZDF", "ZDF.de",
            "https://zdf-hls-15.akamaized.net/hls/live/2016498/de/high/master.m3u8", true),
        new("zdf_neo", "ZDFneo", "ZDFneo.de",
            "https://zdf-hls-16.akamaized.net/hls/live/2016499/de/high/master.m3u8", true),
        new("zdf_info", "ZDFinfo", "ZDFinfo.de",
            "https://zdf-hls-17.akamaized.net/hls/live/2016500/de/high/master.m3u8", true),
        // 3sat is one of the channels the default logo host has no image for - probed once: 404 for 3sat.de,
        // NDR.de, ONE.de, SR.de and RB.de among others - so its picture comes from inside the plugin.
        new("dreisat", "3sat", "3sat.de",
            "https://zdf-hls-18.akamaized.net/hls/live/2016501/dach/high/master.m3u8", true,
            LogoAsset: Logo3sat),
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
        new("one", "ONE", "ONE.de",
            "https://mcdn-one.ard.de/ardone/hls/master.m3u8", false, ArdGroup,
            LogoAsset: LogoOne),
        new("wdr", "WDR", "WDR.de",
            "https://wdr-live.ard-mcdn.de/wdr/live/hls/de/master.m3u8", true, ArdGroup),
        new("mdr_sachsen", "MDR Sachsen", "MDR.de",
            "https://mdrtvsnhls.akamaized.net/hls/live/2016928/mdrtvsn/master.m3u8", true, ArdGroup),
        new("mdr_sachsen_anhalt", "MDR Sachsen-Anhalt", string.Empty,
            "https://mdrtvsahls.akamaized.net/hls/live/2016879/mdrtvsa/master.m3u8", false, ArdGroup, LogoId: "MDR.de"),
        new("mdr_thueringen", "MDR Thüringen", string.Empty,
            "https://mdrtvthhls.akamaized.net/hls/live/2016880/mdrtvth/master.m3u8", false, ArdGroup, LogoId: "MDR.de"),
        new("br_nord", "BR Nord", "BR.de",
            "https://mcdn.br.de/br/fs/bfs_nord/hls/de/master.m3u8", true, ArdGroup),
        new("br_sued", "BR Süd", string.Empty,
            "https://mcdn.br.de/br/fs/bfs_sued/hls/de/master.m3u8", false, ArdGroup, LogoId: "BR.de"),
        new("hr", "HR", "HR.de",
            "https://hr-live.ard-mcdn.de/hr/live/hls/de/master.m3u8", true, ArdGroup),
        new("rbb_berlin", "rbb Berlin", "RBB.de",
            "https://rbb-hls-berlin.akamaized.net/hls/live/2017824/rbb_berlin/index.m3u8", true, ArdGroup),
        new("rbb_brandenburg", "rbb Brandenburg", string.Empty,
            "https://rbb-hls-brandenburg.akamaized.net/hls/live/2017825/rbb_brandenburg/index.m3u8", false, ArdGroup, LogoId: "RBB.de"),
        new("ndr_nds", "NDR Niedersachsen", "NDR.de",
            "https://mcdn.ndr.de/ndr/hls/ndr_fs/ndr_nds/master.m3u8", true, ArdGroup,
            LogoAsset: LogoNdr),
        new("ndr_hh", "NDR Hamburg", string.Empty,
            "https://mcdn.ndr.de/ndr/hls/ndr_fs/ndr_hh/master.m3u8", false, ArdGroup,
            LogoAsset: LogoNdr),
        new("ndr_mv", "NDR Mecklenburg-Vorpommern", string.Empty,
            "https://mcdn.ndr.de/ndr/hls/ndr_fs/ndr_mv/master.m3u8", false, ArdGroup,
            LogoAsset: LogoNdr),
        new("ndr_sh", "NDR Schleswig-Holstein", string.Empty,
            "https://mcdn.ndr.de/ndr/hls/ndr_fs/ndr_sh/master.m3u8", false, ArdGroup,
            LogoAsset: LogoNdr),

        // The two smallest members of the ARD, and the only ones whose whole national feed is this window.
        new("rb", "Radio Bremen", string.Empty,
            "https://rbhlslive.akamaized.net/hls/live/2020435/rbfs/master.m3u8", false, ArdGroup,
            LogoAsset: LogoRadioBremen),
        new("sr", "SR", string.Empty,
            "https://srfs.akamaized.net/hls/live/689649/srfsgeo/master.m3u8", false, ArdGroup,
            LogoAsset: LogoSR),
        new("swr_bw", "SWR Baden-Württemberg", "SWR/SR.de",
            "https://swrbwd-hls.akamaized.net/hls/live/2018672/swrbwd/master.m3u8", true, ArdGroup, LogoId: "SWRSR.de"),
        new("swr_rp", "SWR Rheinland-Pfalz", string.Empty,
            "https://swrrpd-hls.akamaized.net/hls/live/2018676/swrrpd/master.m3u8", false, ArdGroup, LogoId: "SWRSR.de"),

        // Not a broadcaster but on the same platform: the two channels of the German Bundestag. The API's
        // names for these carry soft hyphens ("Parlaments­fern­sehen"), which is not what to show in a guide.
        // Neither has an artwork of its own anywhere - not on the logo host, not on Wikimedia - so both carry
        // the Bundestag's own eagle, which is as close to a logo for them as anything gets.
        new("parlamentsfernsehen_1", "Parlamentsfernsehen Kanal 1", string.Empty,
            "https://bttv-live-z.r53.cdn.tv1.eu/13014bundestag-hk1/_definst_/live/video/hk1_de.smil/playlist.m3u8",
            false, PlatformGroup, IsHd: false, LogoAsset: LogoBundestag),
        new("parlamentsfernsehen_2", "Parlamentsfernsehen Kanal 2", string.Empty,
            "https://bttv-live-z.r53.cdn.tv1.eu/13014bundestag-hk2/_definst_/live/video/hk2_de.smil/playlist.m3u8",
            false, PlatformGroup, IsHd: false, LogoAsset: LogoBundestag)
    ];

    /// <summary>Keys of the channels that are on unless the user configured otherwise.</summary>
    public static string[] DefaultKeys
        => All.Where(c => c.EnabledByDefault).Select(c => c.Key).ToArray();

    /// <summary>
    /// Builds the logo for one channel: an address to fetch, or a file this plugin ships itself.
    /// </summary>
    /// <remarks>
    /// The German XMLTV guide this plugin's channels are matched against carries no icons at all, so
    /// artwork has to come from somewhere else; epg.best publishes a logo per guide id and is what the
    /// default template points at. It does not carry every channel, so an entry may name a file that comes
    /// inside the plugin instead (<see cref="ZdfChannelDefinition.LogoAsset"/>). Both the template and a
    /// per-channel URL can be changed in the settings page, because a host that disappears should not cost
    /// more than one setting - and a shipped file still yields to either of them.
    /// <para>
    /// A channel with its own file does not fall back to the template when that file cannot be used: the
    /// reason it has one is that the host behind the template answers 404 for it, so falling back would
    /// trade no picture for a broken one.
    /// </para>
    /// </remarks>
    /// <param name="definition">The channel.</param>
    /// <param name="template">A URL containing <c>{id}</c>; empty switches artwork off entirely.</param>
    /// <param name="overrideUrl">An explicit URL for this channel, winning over everything else.</param>
    /// <param name="bundledLogos">Resolves a shipped file name to the path it was unpacked to, or <c>null</c>
    /// when that did not work; without it a channel naming a <c>LogoAsset</c> gets no artwork.</param>
    /// <returns>What to offer as the channel's logo, or <c>null</c> when it should have none.</returns>
    public static string? LogoFor(
        ZdfChannelDefinition definition,
        string template,
        string? overrideUrl,
        Func<string, string?>? bundledLogos = null)
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

        if (!string.IsNullOrWhiteSpace(definition.LogoAsset))
        {
            // The host behind the template does not carry this channel, so its picture came along in the
            // assembly. Deliberately no fallback to the template below: for these channels that address is
            // known to answer 404 (see VerifyLogosAsync for why that is worth avoiding).
            return bundledLogos?.Invoke(definition.LogoAsset);
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
    /// Says whether an offered logo still has to be fetched before it can be shown.
    /// </summary>
    /// <remarks>
    /// A logo is either an address - which the server downloads when something first asks for the tile, see
    /// <see cref="ChannelArtwork"/> for what that costs when it fails - or a file this plugin unpacked from
    /// its own assembly (<see cref="ChannelLogoAssets"/>). The distinction is the same one the server makes
    /// with <c>ItemImageInfo.IsLocalFile</c>, and it decides which of <c>ChannelInfo.ImagePath</c> and
    /// <c>ChannelInfo.ImageUrl</c> a logo belongs in.
    /// </remarks>
    /// <param name="logo">What <see cref="LogoFor"/> handed out, or <c>null</c> for no artwork.</param>
    /// <returns><c>true</c> when the logo is an http(s) address rather than a file on this machine.</returns>
    public static bool IsRemote(string? logo)
        => logo is not null
           && (logo.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

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
