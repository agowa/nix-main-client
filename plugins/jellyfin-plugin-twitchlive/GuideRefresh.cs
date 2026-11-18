using System;
using System.Linq;
using MediaBrowser.Common;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Asks the server to rebuild what Live TV currently holds, so a settings change shows up without the
/// operator having to go looking for a task to run.
/// </summary>
/// <remarks>
/// The channels this plugin offers are items the Live TV service stores, and only one code path re-reads
/// them - <c>GuideManager.RefreshGuide()</c>, which starts with <c>TunerHostManager.ScanForTunerDeviceChanges()</c>.
/// Adding a tuner device therefore applies on its own (<c>TunerHostManager.SaveTunerHost()</c> queues this
/// very task) while saving these settings did not: typing another login, turning follows on or changing the
/// schedule window left the library untouched until somebody happened to run the guide refresh. That also
/// covers the live/offline grouping, which is decided during the same pass.
/// <para>
/// Going through <see cref="ITaskManager"/> rather than calling <c>IGuideManager.RefreshGuide()</c> directly
/// keeps the server's own rules: one run at a time, the task visible as running on the tasks page, and its
/// result logged like any other run. Cancelling a refresh that started before this save is what the server
/// does in the same spot for tuner devices.
/// </para>
/// </remarks>
internal static class GuideRefresh
{
    /// <summary><c>RefreshGuideScheduledTask.Key</c>; the task's name is localised, this is not.</summary>
    private const string RefreshGuideTaskKey = "RefreshGuide";

    /// <summary>
    /// Queues a guide refresh, never throwing and never blocking: saving settings must not fail because a
    /// background convenience could not be arranged.
    /// </summary>
    /// <param name="applicationHost">Used to reach the task manager once the server is up.</param>
    /// <param name="logger">Where to say what happened.</param>
    public static void Queue(IApplicationHost applicationHost, ILogger logger)
    {
        try
        {
            if (applicationHost.ServiceProvider?.GetService(typeof(ITaskManager)) is not ITaskManager taskManager)
            {
                // Only possible before the container is built, i.e. before anything could save settings.
                logger.LogInformation("Twitch Live TV: saved, but the task manager was not available to queue a guide refresh");
                return;
            }

            var worker = taskManager.ScheduledTasks.FirstOrDefault(
                w => string.Equals(w.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.OrdinalIgnoreCase));

            if (worker is null)
            {
                // Live TV off entirely would look like this; the channels are not in the library either then.
                logger.LogInformation("Twitch Live TV: saved, but no '{Key}' task exists to queue", RefreshGuideTaskKey);
                return;
            }

            if (worker.State == TaskState.Running)
            {
                // It started before this save, so it is working off the previous settings.
                taskManager.Cancel(worker);
            }

            taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());

            logger.LogInformation("Twitch Live TV: saved, queued a guide refresh so the channels update now");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Twitch Live TV: saved, but could not queue a guide refresh; run it by hand to see the change");
        }
    }
}
