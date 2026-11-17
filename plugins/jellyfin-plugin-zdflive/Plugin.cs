using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ZdfLive.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// Live TV support for the channels carried by ZDF.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable identity of this plugin; must match meta.json.</summary>
    public static readonly Guid PluginId = Guid.Parse("f0a3242b-6358-494a-ad7a-8d5c50c90a20");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "ZDF Live TV";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>Gets the current instance, or <c>null</c> before the plugin host has created it.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the pages that make up the configuration UI.
    /// </summary>
    /// <remarks>
    /// Jellyfin 10.11 does not run inline script in a plugin page: the view container only executes an
    /// ES module, and it loads the one named by the page's <c>data-controller</c> attribute from
    /// /web/ConfigurationPage?name=&lt;controller&gt;. So the HTML, its controller and the channel
    /// catalogue are three embedded resources served through the same endpoint.
    /// <para>
    /// Order matters: when a plugin offers several pages the web client picks the first one for its
    /// "Settings" button (jellyfin-web's findBestConfigurationPage), so the HTML has to come first and
    /// the two data resources after it.
    /// </para>
    /// </remarks>
    /// <returns>The configuration page, its controller and the catalogue.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "zdfLiveConfig",
                DisplayName = "ZDF Live TV",
                EmbeddedResourcePath = typeof(Plugin).Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "zdfLiveConfigController",
                DisplayName = "ZDF Live TV settings",
                EmbeddedResourcePath = typeof(Plugin).Namespace + ".Configuration.configController.js"
            },

            // The channel list the settings page renders. Served as a page because that is the only way
            // a plugin can hand content to its own UI without adding an endpoint - see the note in
            // ZdfChannelCatalog.VerifyEmbeddedList about keeping it in step with the catalogue.
            new PluginPageInfo
            {
                Name = "zdfLiveCatalog",
                DisplayName = "ZDF Live TV channels",
                EmbeddedResourcePath = typeof(Plugin).Namespace + ".Configuration.zdfCatalog.json"
            }
        ];
    }
}
