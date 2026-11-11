using Jellyfin.Plugin.ZdfLive.TunerHosts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// Puts the tuner host into the server's own service collection.
/// </summary>
/// <remarks>
/// <c>TunerHostManager</c> is built from <c>IEnumerable&lt;ITunerHost&gt;</c>, and
/// <c>ApplicationHost.Init()</c> calls <c>PluginManager.RegisterServices()</c> into the very same
/// <see cref="IServiceCollection"/> before it is built - so a plugin that registers an ITunerHost here
/// shows up as a tuner type next to HDHomeRun and IPTV, without any playlist files involved.
/// </remarks>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ZdfChannelRepository>();
        serviceCollection.AddSingleton<ITunerHost, ZdfTunerHost>();
    }
}
