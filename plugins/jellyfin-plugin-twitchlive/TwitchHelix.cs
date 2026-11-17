using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// The documented Twitch HTTP API (Helix), used for the two things the player endpoint cannot do:
/// read a channel's published schedule and read whose channels an account follows.
/// </summary>
/// <remarks>
/// The one call here needs no login at all, which is what keeps this plugin usable without
/// registering anything:
/// <list type="bullet">
///   <item><description>
///   <c>GET /helix/schedule/icalendar</c> is documented as needing neither Client-Id nor Authorization,
///   and it returns the same slots the "Schedule" tab on the channel shows - already expanded into
///   concrete dates for about a week ahead. That is the default path.
/// </description></item>
/// </list>
/// A numeric user id is needed; it arrives with the GraphQL status request that has to happen
/// anyway (<see cref="TwitchStreamStatus.UserId"/>), so no extra lookup is involved.
/// </remarks>
public sealed class TwitchHelix
{
    private const string HelixUrl = "https://api.twitch.tv/helix";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TwitchHelix> _logger;


    /// <summary>Initializes a new instance of the <see cref="TwitchHelix"/> class.</summary>
    public TwitchHelix(IHttpClientFactory httpClientFactory, ILogger<TwitchHelix> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the published broadcast slots of one channel for the coming days.
    /// </summary>
    /// <param name="userId">The channel's numeric id.</param>
    /// <param name="aheadDays">How far ahead to ask for; Twitch publishes about a week either way.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The slots in chronological order, or an empty list when the channel publishes none.</returns>
    public async Task<IReadOnlyList<TwitchScheduledItem>> GetScheduleAsync(
        string userId,
        int aheadDays,
        CancellationToken cancellationToken)
    {
        var url = HelixUrl + "/schedule/icalendar?broadcaster_id=" + Uri.EscapeDataString(userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Deliberately no Client-Id and no Authorization: the iCalendar endpoint is documented as
        // needing neither, so this works for an operator who never registers a Twitch application.
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Documented meaning: the channel has no schedule, or it holds no slots right now. That is
            // normal for most channels and not worth a warning per refresh.
            return Array.Empty<TwitchScheduledItem>();
        }

        response.EnsureSuccessStatusCode();

#if NET9_0_OR_GREATER
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif

        var items = TwitchICalendar.Parse(content);

        // The feed covers a rolling window; anything outside what was asked for would only make the
        // guide claim to know further ahead than it does.
        var fromUtc = DateTime.UtcNow.AddHours(-1);
        var untilUtc = DateTime.UtcNow.AddDays(Math.Max(1, aheadDays));

        var result = items.Where(i => i.EndUtc > fromUtc && i.StartUtc < untilUtc)
            .OrderBy(i => i.StartUtc)
            .ToList();

        _logger.LogDebug(
            "Twitch Live TV: {Count} scheduled slots for user {UserId} between now and {Until}",
            result.Count,
            userId,
            untilUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}
