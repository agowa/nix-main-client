using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ZdfLive.Configuration;

/// <summary>
/// Settings for the ZDF Live TV plugin.
/// </summary>
/// <remarks>
/// Everything here is re-read on every request, so a change takes effect without restarting the server:
/// use the plugin's configuration page (Dashboard -> Plugins -> ZDF Live TV -> Settings), which lists
/// every channel it knows including ones only the upstream API knows about, or
/// <c>GET</c>/<c>POST /Plugins/f0a3242b-6358-494a-ad7a-8d5c50c90a20/Configuration</c> - Jellyfin only
/// accepts POST there, not PUT. On a NixOS host the same values can be pinned with
/// <c>services.jellyfinZdfLive.manageConfig</c>. Every default is complete, so the plugin works untouched.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class.</summary>
    public PluginConfiguration()
    {
        EnabledChannels = ZdfChannelCatalog.DefaultKeys;
        CacheMinutes = 60;
        ApiBaseUrl = "https://api.zapp.mediathekview.de/v1/channelInfoList";

        // The German XMLTV guide this plugin's channels are matched against carries no icons, so the
        // logo has to come from elsewhere: epg.best publishes one per guide id under a predictable URL.
        ShowChannelLogos = true;
        LogoTemplate = "https://static.epg.best/de/{id}.png";
        LogoUrls = [];
        // The broadcasters' CDNs are fronted by Akamai and answer a non-browser user agent with
        // 403 in some regions, so the built-in M3U tuner does the same thing: pretend to be Chrome.
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
        SendRefererHeader = true;
    }

    /// <summary>Gets or sets the catalogue keys to expose as channels.</summary>
    public string[] EnabledChannels { get; set; }

    /// <summary>Gets or sets how long resolved stream URLs are reused before the API is queried again.</summary>
    public int CacheMinutes { get; set; }

    /// <summary>Gets or sets the JSON endpoint that maps catalogue keys to current master playlists.</summary>
    public string ApiBaseUrl { get; set; }

    /// <summary>Gets or sets the User-Agent sent with every request for a stream.</summary>
    public string UserAgent { get; set; }

    /// <summary>Gets or sets whether a Referer is sent for the ZDF-operated CDNs.</summary>
    public bool SendRefererHeader { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether channels are offered with a logo.
    /// </summary>
    /// <remarks>
    /// Jellyfin stores this as the channel's primary image and downloads it the first time something
    /// asks for it, so nothing is fetched until the guide or a client wants to show it.
    /// </remarks>
    public bool ShowChannelLogos { get; set; }

    /// <summary>
    /// Gets or sets the logo URL template, with <c>{id}</c> where the channel's logo id goes.
    /// </summary>
    /// <remarks>
    /// Empty means no logos even when <see cref="ShowChannelLogos"/> is on - which is how this stays
    /// one setting rather than two when someone wants to point it at their own image host. The id used
    /// is the channel's guide id unless the catalogue records a different spelling for that host.
    /// </remarks>
    public string LogoTemplate { get; set; }

    /// <summary>
    /// Gets or sets per-channel logo URLs, by catalogue key, overriding the template.
    /// </summary>
    /// <remarks>
    /// A handful of channels have no logo at the default host (3sat and the NDR state feeds among
    /// them); rather than ship a URL that may rot, the settings page lets an operator paste any image
    /// address per channel. Name is the catalogue key, Value the URL.
    /// </remarks>
    public NameValuePair[] LogoUrls { get; set; }

    /// <summary>Looks up the explicit logo URL configured for one catalogue key.</summary>
    /// <param name="key">The catalogue key.</param>
    /// <returns>The configured URL, or <c>null</c> when this channel has no override.</returns>
    public string? LogoUrlFor(string key)
    {
        var entry = LogoUrls?
            .FirstOrDefault(u => string.Equals(u?.Name, key, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(entry?.Value) ? null : entry!.Value;
    }
}
