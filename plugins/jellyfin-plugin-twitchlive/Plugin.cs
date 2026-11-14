using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Live TV support for Twitch channels.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable identity of this plugin; must match meta.json.</summary>
    public static readonly Guid PluginId = Guid.Parse("acfb8b6e-f2ce-4615-800f-77c40ffd213d");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Twitch Live TV";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>Gets the current instance, or <c>null</c> before the plugin host has created it.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the pages that make up the configuration UI.
    /// </summary>
    /// <remarks>
    /// Jellyfin 10.11 does not run inline script in a plugin page any more: the view container only
    /// executes an ES module, and it loads the one named by the page's <c>data-controller</c> attribute
    /// from /web/ConfigurationPage?name=&lt;controller&gt;. So the HTML and its controller are registered
    /// as two separate embedded resources, and the controller has to be listed second - when a plugin
    /// offers more than one page the web client picks the first one for the "Settings" button.
    /// </remarks>
    /// <returns>The configuration page and its controller.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "twitchLiveConfig",
                DisplayName = "Twitch Live TV",
                EmbeddedResourcePath = typeof(Plugin).Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "twitchLiveConfigController",
                DisplayName = "Twitch Live TV settings",
                EmbeddedResourcePath = typeof(Plugin).Namespace + ".Configuration.configController.js"
            }
        ];
    }
}
