using System;
using System.Collections.Generic;
using System.Linq;

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
public sealed record ZdfChannelDefinition(
    string Key,
    string Name,
    string EpgId,
    string FallbackUrl,
    bool EnabledByDefault,
    string Group = "ZDF Live",
    bool IsHd = true);

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
            "https://zdf-hls-19.akamaized.net/hls/live/2016502/de/high/master.m3u8", true),
        new("arte", "ARTE", "ARTE.de",
            "https://artesimulcast.akamaized.net/hls/live/2030993/artelive_de/index.m3u8", true),
        new("kika", "KiKA", "KIKA.de",
            "https://kikageohls.akamaized.net/hls/live/2022693/livetvkika_de/master.m3u8", true),

        // Not part of the ZDF offering, but delivered by the same API and therefore nearly free.
        new("das_erste", "Das Erste", "Das.Erste.de",
            "https://daserste-live.ard-mcdn.de/daserste/live/hls/de/master.m3u8", true, ArdGroup),
        new("tagesschau24", "tagesschau24", "tagesschau24.de",
            "https://tagesschau.akamaized.net/hls/live/2020117/tagesschau/tagesschau_3/master.m3u8", true, ArdGroup),
        new("ard_alpha", "ARD-alpha", "ARD.alpha.de",
            "https://mcdn.br.de/br/fs/ard_alpha/hls/de/master.m3u8", true, ArdGroup),
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
            "https://swrbwd-hls.akamaized.net/hls/live/2018672/swrbwd/master.m3u8", true, ArdGroup)
    ];

    /// <summary>Keys of the channels that are on unless the user configured otherwise.</summary>
    public static string[] DefaultKeys
        => All.Where(c => c.EnabledByDefault).Select(c => c.Key).ToArray();

    /// <summary>Looks a definition up by key, ignoring case and surrounding whitespace.</summary>
    public static ZdfChannelDefinition? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
