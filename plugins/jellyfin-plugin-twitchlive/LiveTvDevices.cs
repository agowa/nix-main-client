using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// The tuner devices the operator has actually added under Guide &amp; Channels -> Live TV.
/// </summary>
/// <remarks>
/// Why a plugin has to ask about this at all: <c>DefaultLiveTvService.GetChannelsAsync()</c>
/// (src/Jellyfin.LiveTv/DefaultLiveTvService.cs) loops over <em>every registered</em>
/// <see cref="MediaBrowser.Controller.LiveTv.ITunerHost"/>, not over the configured devices, and it
/// merges all their channels into one list keyed by channel id alone. A tuner host that answers with
/// its full channel list unconditionally therefore contributes those channels even when nobody added
/// a device for it - which is how two installed plugins end up looking like they share state: adding
/// the second tuner appears to add the first plugin's channels too, because both were already there.
/// <para>
/// The server's own tuners do not have this problem because they derive from
/// <c>Jellyfin.LiveTv.TunerHosts.BaseTunerHost</c>, whose <c>GetChannels()</c> iterates
/// <c>Config.GetLiveTvConfiguration().TunerHosts.Where(t => t.Type == Type)</c>. That class lives in
/// the server's private assembly and is not part of the plugin contract, so this class is the
/// equivalent for a plugin: same filter, same source - <see cref="LiveTvOptions"/> is the object
/// Jellyfin parses out of <c>&lt;configDir&gt;/livetv.xml</c> and reading it costs nothing after the
/// first time. It also means the guide provider can be matched to a device: programme data is only
/// fetched for channels whose <see cref="ChannelInfo.TunerHostId"/> appears in the listings
/// provider's <c>EnabledTuners</c>, so that field has to carry the real device id and not this
/// plugin's type string.
/// </para>
/// </remarks>
public sealed class LiveTvDevices
{
    private readonly IServerConfigurationManager _config;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<LiveTvDevices> _logger;

    /// <summary>Devices this plugin has already given a name, by id, so it does not save them again.</summary>
    private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps the "no device" hint to one line per change instead of one per guide question.</summary>
    private string? _lastLoggedState;

    /// <summary>Initializes a new instance of the <see cref="LiveTvDevices"/> class.</summary>
    public LiveTvDevices(IServerConfigurationManager config, IApplicationHost applicationHost, ILogger<LiveTvDevices> logger)
    {
        _config = config;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Gets the configured devices of this plugin's tuner type, in the order they were added.
    /// </summary>
    /// <returns>The devices, or an empty list while no device has been added in the web UI.</returns>
    public IReadOnlyList<TunerHostInfo> GetOwnDevices()
    {
        LiveTvOptions options;

        try
        {
            // The same call the server's own extension method makes: GetConfiguration<LiveTvOptions>("livetv").
            options = _config.GetConfiguration<LiveTvOptions>("livetv");
        }
        catch (Exception ex)
        {
            // No "livetv" store means no Live TV set up at all, so there is nothing to offer either.
            LogState("unreadable");
            _logger.LogWarning(ex, "Twitch Live TV: could not read the Live TV configuration");
            return Array.Empty<TunerHostInfo>();
        }

        var devices = (options.TunerHosts ?? Array.Empty<TunerHostInfo>())
            .Where(t => t is not null
                        && !string.IsNullOrWhiteSpace(t.Id)
                        && string.Equals(t.Type, TwitchChannelCatalog.HostId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        LogState(devices.Count == 0 ? "none configured" : devices.Count + " configured");

        return devices;
    }

    /// <summary>
    /// Gets the device that this plugin's channels should be attributed to.
    /// </summary>
    /// <remarks>
    /// One channel list per plugin rather than one per device: a second device of the same type would
    /// offer exactly the same channels, and duplicate ids in the merged list are dropped by the server
    /// anyway (BaseTunerHost deduplicates on id for the same reason). The first device is used because
    /// that is what <c>EnabledTuners</c> ends up naming.
    /// </remarks>
    /// <returns>The device, or <c>null</c> when none is configured.</returns>
    public TunerHostInfo? GetPrimaryDevice()
        => GetOwnDevices().FirstOrDefault();

    /// <summary>
    /// Gives every device of this plugin's type the name the web UI should show for it.
    /// </summary>
    /// <remarks>
    /// The tuner list titles each device with its friendly name, and falls back to a label that comes from
    /// a switch over the four tuner types shipped with the server - anything else is titled "Unknown". So a
    /// source added by picking this plugin in the dropdown rather than pressing "Detect My Devices" reads
    /// as "Unknown", because that form sends no name at all (jellyfin-web's tuner form posts FriendlyName
    /// only if the field was filled in). Nothing is broken in that state - everything that decides which
    /// channels appear keys on the type string - but it looks like a plugin that failed to install, and
    /// there is no way to notice from the page that the row is in fact this plugin.
    /// <para>
    /// Naming the device here rather than telling the operator to edit it costs one save. Going through
    /// <see cref="ITunerHostManager"/> rather than writing the Live TV store directly keeps the server's
    /// own handling of the entry, and asking for that service on first use rather than in the constructor
    /// is what avoids a cycle: <c>TunerHostManager</c> implements it and is itself built from every
    /// registered <see cref="ITunerHost"/>, this plugin's included.
    /// </para>
    /// </remarks>
    /// <param name="friendlyName">What to call the device; the tuner host's own name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task EnsureFriendlyNamesAsync(string friendlyName, CancellationToken cancellationToken)
    {
        var unnamed = GetOwnDevices()
            .Where(d => string.IsNullOrWhiteSpace(d.FriendlyName) && !_named.Contains(d.Id))
            .ToList();

        if (unnamed.Count == 0)
        {
            return;
        }

        if (_applicationHost.ServiceProvider?.GetService(typeof(ITunerHostManager)) is not ITunerHostManager tunerHosts)
        {
            _logger.LogInformation("Twitch Live TV: could not reach the tuner host manager, so an unnamed device keeps the label it was given");
            return;
        }

        foreach (var device in unnamed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                device.FriendlyName = friendlyName;

                // dataSourceChanged: false - the source is the same device under a label, so there is
                // nothing for the guide to rebuild, and queueing a refresh from inside one would not end.
                await tunerHosts.SaveTunerHost(device, false).ConfigureAwait(false);

                _named.Add(device.Id);
                _logger.LogInformation(
                    "Twitch Live TV: named the '{Type}' tuner device {Name}; the tuner list had been calling it Unknown",
                    TwitchChannelCatalog.HostId,
                    friendlyName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Twitch Live TV: could not name the '{Type}' tuner device", TwitchChannelCatalog.HostId);
            }
        }
    }

    private void LogState(string state)
    {
        if (string.Equals(_lastLoggedState, state, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedState = state;

        if (state == "none configured")
        {
            _logger.LogInformation(
                "Twitch Live TV: no '{Type}' tuner device is configured, so no channels are offered. Add one under Guide and Channels -> Live TV -> Tuner devices.",
                TwitchChannelCatalog.HostId);
        }
        else
        {
            _logger.LogDebug("Twitch Live TV: tuner state changed to {State}", state);
        }
    }
}
