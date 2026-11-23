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
/// Two of the three calls here need no login at all, which is what keeps this plugin usable without
/// registering anything:
/// <list type="bullet">
///   <item><description>
///   <c>GET /helix/schedule/icalendar</c> is documented as needing neither Client-Id nor Authorization,
///   and it returns the same slots the "Schedule" tab on the channel shows - already expanded into
///   concrete dates for about a week ahead. That is the default path.
/// </description></item>
///   <item><description>
///   <c>GET /helix/channels/followed</c> needs an account token with the scope <c>user:read:follows</c>,
///   so it only runs when the operator asks for it (see
///   <see cref="PluginConfiguration.ImportFollowedChannels"/>).
/// </description></item>
/// </list>
/// Numeric user ids are needed by both; they arrive with the GraphQL status request that has to happen
/// anyway (<see cref="TwitchStreamStatus.UserId"/>), so no extra lookup is involved.
/// </remarks>
public sealed class TwitchHelix
{
    private const string HelixUrl = "https://api.twitch.tv/helix";

    /// <summary>How often the follow list is re-read when it is wanted. Follows change slowly.</summary>
    private static readonly TimeSpan FollowRetryAfterFailure = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TwitchHelix> _logger;

    private DateTime _followsExpireUtc;
    private IReadOnlyList<string>? _cachedFollows;

    private string? _lastFollowError;

    /// <summary>Which token <see cref="_cachedFollows"/> was read for, so a second account cannot be served its list.</summary>
    private string? _cachedFollowToken;

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

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new UpstreamRateLimitedException(RetryAfter(response), "HTTP 429 from " + HelixUrl);
        }

        response.EnsureSuccessStatusCode();

#if NET9_0_OR_GREATER
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif

        var items = TwitchICalendar.Parse(content, out var truncated);

        if (truncated)
        {
            // Better a warning than a guide that quietly stops short of what was published.
            _logger.LogWarning(
                "Twitch Live TV: the schedule feed for user {UserId} was longer than this plugin reads; showing the first {Count} slots",
                userId,
                items.Count);
        }

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

    /// <summary>
    /// Gets the logins this token's account follows, newest follow first.
    /// </summary>
    /// <param name="token">The account's OAuth access token.</param>
    /// <param name="clientId">The client id the token was issued for; Twitch requires them to match.</param>
    /// <param name="maxCount">Upper bound on how many logins to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="refresh">
    /// Ask Twitch even when a list is cached, and let a failure surface instead of answering with the last
    /// good list. The guide import wants resilience; somebody pressing a button on the settings page wants
    /// an answer about right now - see <see cref="TwitchAccountController"/>.
    /// </param>
    /// <returns>The followed logins, normalised. An empty list means either nobody or no permission.</returns>
    public async Task<IReadOnlyList<string>> GetFollowedLoginsAsync(
        string token,
        string clientId,
        int maxCount,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        // Cached per token rather than per server: the settings page can ask about an account that is not
        // the one the guide import filled this cache with, and answering from that cache would look like a
        // working button reporting the wrong account's follows.
        if (!refresh && _cachedFollows is not null && IsCachedFor(token) && DateTime.UtcNow < _followsExpireUtc)
        {
            return _cachedFollows;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var limit = Math.Clamp(maxCount, 1, 2000);

        try
        {
            // The token's own identity first: /channels/followed insists on the caller's user id and
            // will not accept "me". Validating also tells us about a missing scope before the follow
            // request fails with a message that does not name it.
            var (userId, login, scopes) = await ValidateTokenAsync(token, clientId, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(userId))
            {
                throw new InvalidOperationException("Twitch did not tell us which account this token belongs to");
            }

            if (scopes.Count > 0 && !scopes.Contains("user:read:follows", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "the token is missing the scope user:read:follows, so Twitch will not share the follow list (issued for '"
                    + login + "')");
            }

            var logins = new List<string>();

            // Twitch pages through the list and a login can appear on two pages; asking a List whether it has
            // seen one already is quadratic at exactly the size this endpoint allows (2000 follows).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? cursor = null;

            do
            {
                var url = HelixUrl + "/channels/followed?user_id=" + Uri.EscapeDataString(userId) + "&first=100";
                if (!string.IsNullOrEmpty(cursor))
                {
                    url += "&after=" + Uri.EscapeDataString(cursor);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuth(request, token, clientId);

                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

                // The follow list is paginated a hundred at a time, so it is the call most likely to be told
                // to slow down; the header says when to come back and the caller keeps that verdict.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new UpstreamRateLimitedException(RetryAfter(response), "HTTP 429 from " + HelixUrl);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // 403 with a valid token is Twitch's answer to a missing scope.
                    throw new InvalidOperationException(
                        "Twitch refused the follow list (HTTP 403) - the token needs the scope user:read:follows");
                }

                response.EnsureSuccessStatusCode();

#if NET9_0_OR_GREATER
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in data.EnumerateArray())
                    {
                        var name = entry.TryGetProperty("broadcaster_login", out var l) ? l.GetString() : null;
                        var normalized = TwitchChannelCatalog.Normalize(name);

                        // Only a login that could be a real one goes any further: this list becomes channel ids
                        // and, downstream, the file names of the streamer pictures (see AvatarAssets).
                        if (normalized is not null
                            && TwitchApi.IsPlausibleLogin(normalized)
                            && seen.Add(normalized))
                        {
                            logins.Add(normalized);
                        }

                        if (logins.Count >= limit)
                        {
                            break;
                        }
                    }
                }

                cursor = document.RootElement.TryGetProperty("pagination", out var page)
                         && page.TryGetProperty("cursor", out var c) ? c.GetString() : null;
            }
            while (!string.IsNullOrEmpty(cursor) && logins.Count < limit);

            _cachedFollows = logins;
            _cachedFollowToken = token;
            _followsExpireUtc = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, config.FollowCacheMinutes));

            if (_lastFollowError is not null)
            {
                _logger.LogInformation("Twitch Live TV: follow list works again after {Error}", _lastFollowError);
                _lastFollowError = null;
            }

            _logger.LogDebug("Twitch Live TV: read {Count} followed channels for '{Login}'", logins.Count, login);
            return logins;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep serving the previous list if there was one: a revoked or expired token should not
            // empty the channel list, it should only stop it from changing.
            _lastFollowError = ex.Message;

            // A limit is a schedule rather than a failure: wait as long as was asked and not longer, which is
            // the difference between one slow refresh and a follow list that stays stale for half a day.
            _followsExpireUtc = DateTime.UtcNow
                                + (ex is UpstreamRateLimitedException limited ? limited.Delay : FollowRetryAfterFailure);
            // Asked directly, answered honestly: a button that shows the previous list after Twitch said
            // no is how this feature would lie. The backoff above still applies to the guide import.
            if (refresh)
            {
                throw;
            }

            _logger.LogWarning(
                "Twitch Live TV: could not read the follow list ({Error}); {Cached} channels from the last successful read are kept for {Minutes} minutes",
                ex.Message,
                IsCachedFor(token) ? _cachedFollows?.Count ?? 0 : 0,
                FollowRetryAfterFailure.TotalMinutes);

            return IsCachedFor(token) ? _cachedFollows! : Array.Empty<string>();
        }
    }

    /// <summary>Whether the cached follow list belongs to this token.</summary>
    private bool IsCachedFor(string token)
        => string.Equals(_cachedFollowToken, token, StringComparison.Ordinal);

    /// <summary>
    /// Asks id.twitch.tv whose token this is and what it may read.
    /// </summary>
    /// <remarks>
    /// Also what the settings page asks through <see cref="TwitchAccountController"/>, so that the answer -
    /// not the credential - is what travels back to the browser. Throws with a message written for whoever
    /// typed the token, which that endpoint passes straight through.
    /// </remarks>
    public async Task<(string? UserId, string Login, HashSet<string> Scopes)> ValidateTokenAsync(
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");

        // The validation endpoint is the one place Twitch documents the older "OAuth" scheme.
        request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token.Trim());
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Twitch says this token is not valid (HTTP {(int)response.StatusCode}) - issue a fresh one at https://dev.twitch.tv/console or https://twitchtokengenerator.com");
        }

#if NET9_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        string? userId = root.TryGetProperty("user_id", out var u) ? u.GetString() : null;
        var login = root.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;

        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var scope in s.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.String && scope.GetString() is { Length: > 0 } name)
                {
                    scopes.Add(name);
                }
            }
        }

        return (userId, login, scopes);
    }

    /// <summary>How long a <c>Retry-After</c> asks to be left alone, clamped to something this plugin obeys.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;

        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TwitchApi.MaxRateLimitWait ? TwitchApi.MaxRateLimitWait : delta;
        }

        if (retry?.Date is { } date && date > DateTimeOffset.UtcNow)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TwitchApi.MaxRateLimitWait ? TwitchApi.MaxRateLimitWait : wait;
        }

        return FollowRetryAfterFailure;
    }

    private static void AddAuth(HttpRequestMessage request, string token, string clientId)
    {
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}
