using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ZdfLive.Configuration;

/// <summary>
/// Settings for the ZDF Live TV plugin. There is no configuration page: on a NixOS host these values
/// belong in the system configuration, and they can be changed over the REST API without one - see
/// README.md ("Changing settings"). Every default is complete, so the plugin works untouched.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class.</summary>
    public PluginConfiguration()
    {
        EnabledChannels = ZdfChannelCatalog.DefaultKeys;
        CacheMinutes = 60;
        ApiBaseUrl = "https://api.zapp.mediathekview.de/v1/channelInfoList";
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
}
