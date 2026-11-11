using System;
using Jellyfin.Plugin.ZdfLive.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// Live TV support for the channels carried by ZDF.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
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
}
