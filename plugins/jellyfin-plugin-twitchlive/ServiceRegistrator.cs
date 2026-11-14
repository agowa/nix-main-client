using Jellyfin.Plugin.TwitchLive.Listings;
using Jellyfin.Plugin.TwitchLive.TunerHosts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Puts the tuner host and the programme guide into the server's own service collection.
/// </summary>
/// <remarks>
/// <c>TunerHostManager</c> and <c>ListingsManager</c> are built from
/// <c>IEnumerable&lt;ITunerHost&gt;</c> and <c>IEnumerable&lt;IListingsProvider&gt;</c>, and
/// <c>ApplicationHost.Init()</c> calls <c>PluginManager.RegisterServices()</c> into the very same
/// <see cref="IServiceCollection"/> before it is built - so this plugin can both offer channels and
/// describe them in the guide without any playlist or EPG file involved.
/// </remarks>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TwitchApi>();
        serviceCollection.AddSingleton<TwitchChannelRepository>();
        serviceCollection.AddSingleton<ITunerHost, TwitchTunerHost>();

        // There is no XMLTV for "who is streaming right now", so the guide comes from this plugin too.
        serviceCollection.AddSingleton<IListingsProvider, TwitchGuideProvider>();
    }
}
