using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// What Twitch says about one channel right now.
/// </summary>
/// <param name="Login">The normalised login the answer belongs to.</param>
/// <param name="DisplayName">The display name Twitch reports, or the login if it has none.</param>
/// <param name="Exists">Whether Twitch knows this login at all; false means the configuration has a typo in it.</param>
/// <param name="IsLive">Whether a stream is running. Anything playable implies this.</param>
/// <param name="StreamType"><c>"live"</c> for a normal broadcast, <c>"playlist"</c> for an automated one.</param>
/// <param name="Title">The stream title, if live.</param>
/// <param name="Game">The category the stream is listed under, if live.</param>
/// <param name="Viewers">Viewer count, if Twitch reports one.</param>
public sealed record TwitchStreamStatus(
    string Login,
    string DisplayName,
    bool Exists,
    bool IsLive,
    string? StreamType,
    string? Title,
    string? Game,
    int? Viewers);

/// <summary>A playback token for one channel.</summary>
/// <param name="Value">The token itself; it is bound to the requesting IP address and expires.</param>
/// <param name="Signature">Its signature, which usher checks against it.</param>
public sealed record TwitchPlaybackToken(string Value, string Signature);

/// <summary>
/// The two Twitch endpoints this plugin needs.
/// </summary>
/// <remarks>
/// Both are the ones the embedded player on twitch.tv uses, so no app registration and no user login is
/// involved - only the public client id from the configuration. Everything is asked over POST to a
/// single GraphQL endpoint:
/// <list type="bullet">
///   <item>
///     <description>
///     Live state of every configured channel in one request, as an array of documents that each alias
///     up to <see cref="TwitchChannelCatalog.AliasesPerQuery"/> logins. Aliases are named after the
///     login they ask about, so answers cannot be mixed up regardless of the order they come back in.
///     </description>
///   </item>
///   <item>
///     <description>
///     A playback token per channel at tune-in time, which together with its signature authorises one
///     fetch of that channel's master playlist.
///     </description>
///   </item>
/// </list>
/// Logins are always passed as GraphQL variables and are additionally checked against Twitch's own
/// login alphabet first, so nothing configured by hand can end up inside the query text.
/// </remarks>
public sealed class TwitchApi
{
    private const string GraphQlUrl = "https://gql.twitch.tv/gql";

    /// <summary>Twitch logins: 3 to 25 characters of letters, digits and underscores.</summary>
    private static readonly System.Text.RegularExpressions.Regex LoginPattern =
        new("^[a-z0-9_]{3,25}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TwitchApi> _logger;

    /// <summary>Initializes a new instance of the <see cref="TwitchApi"/> class.</summary>
    public TwitchApi(IHttpClientFactory httpClientFactory, ILogger<TwitchApi> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Whether a string is a syntactically valid Twitch login (after normalisation).</summary>
    public static bool IsPlausibleLogin(string? normalizedLogin)
        => normalizedLogin is not null && LoginPattern.IsMatch(normalizedLogin);

    /// <summary>
    /// Asks Twitch which of these channels are streaming.
    /// </summary>
    /// <param name="logins">Normalised, de-duplicated logins.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One entry per login that was asked about; a login Twitch does not know comes back with <see cref="TwitchStreamStatus.Exists"/> set to false.</returns>
    public async Task<Dictionary<string, TwitchStreamStatus>> GetStatusesAsync(IReadOnlyList<string> logins, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TwitchStreamStatus>(StringComparer.OrdinalIgnoreCase);
        if (logins.Count == 0)
        {
            return result;
        }

        // One HTTP request even for a long list: the endpoint takes an array of queries.
        var documents = new List<string>();
        foreach (var chunk in Chunk(logins, TwitchChannelCatalog.AliasesPerQuery))
        {
            documents.Add(BuildStatusDocument(chunk, documents.Count));
        }

        using var response = await PostAsync(documents.Count == 1 ? documents[0] : "[" + string.Join(",", documents) + "]", cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            ReadStatusRoot(document.RootElement, result);
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                ReadStatusRoot(element, result);
            }
        }

        // Anything the answer did not cover has to be reported as unknown rather than left out: a
        // missing entry would look like "offline" further up and hide a mistake.
        foreach (var login in logins)
        {
            if (!result.ContainsKey(login))
            {
                _logger.LogWarning("Twitch Live TV: no answer for channel '{Login}'", login);
            }
        }

        return result;
    }

    /// <summary>Fetches a playback token for one channel.</summary>
    public async Task<TwitchPlaybackToken> GetPlaybackTokenAsync(string login, CancellationToken cancellationToken)
    {
        // Serialised rather than assembled from a template: an earlier version pasted the login into a
        // hand-written JSON document and produced `"channel":""lofigirl""`, which Twitch rejected. Letting
        // the serializer write the whole body makes that class of mistake impossible, and the login stays
        // a variable either way.
        var body = JsonSerializer.Serialize(new TokenRequest
        {
            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["channel"] = login }
        });

        using var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
#if NET9_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("streamPlaybackAccessToken", out var token)
            || token.ValueKind != JsonValueKind.Object
            || !token.TryGetProperty("value", out var value)
            || !token.TryGetProperty("signature", out var signature))
        {
            throw new InvalidOperationException($"Twitch did not issue a playback token for '{login}': {Truncate(document.RootElement.GetRawText())}");
        }

        return new TwitchPlaybackToken(value.GetString() ?? string.Empty, signature.GetString() ?? string.Empty);
    }

    /// <summary>
    /// Builds the master playlist URL for a channel from its token.
    /// </summary>
    /// <remarks>
    /// This is the address twitchweb itself tunes to. The token expires after a while, but only this
    /// first request needs it: the media playlists and segments referenced inside are separately signed,
    /// which is why a stream keeps running long after the token that started it has become invalid.
    /// </remarks>
    public static string BuildStreamUrl(string login, TwitchPlaybackToken token)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var url = new StringBuilder("https://usher.ttvnw.net/api/channel/hls/")
            .Append(Uri.EscapeDataString(login))
            .Append(".m3u8?allow_source=")
            .Append(config.AllowSourceQuality ? "true" : "false")
            .Append("&allow_spectator_views=true&fast_bread=true&player_backend=mediaplayer&platform=web")
            .Append("&sig=").Append(Uri.EscapeDataString(token.Signature))
            .Append("&token=").Append(Uri.EscapeDataString(token.Value));

        return url.ToString();
    }

    private static void ReadStatusRoot(JsonElement root, Dictionary<string, TwitchStreamStatus> into)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            // Refuse the whole answer rather than treating the channels it did not name as offline: the
            // caller keeps its previous state and retries later, which is the safe direction to fail in.
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText())
                .Where(m => !string.IsNullOrWhiteSpace(m));
            throw new InvalidOperationException("Twitch rejected the channel query: " + string.Join("; ", messages));
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in data.EnumerateObject())
        {
            var login = TwitchChannelCatalog.LoginFromAlias(property.Name);
            if (login is null)
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                // user(login: ...) resolved to null: no such account.
                into[login] = new TwitchStreamStatus(login, login, false, false, null, null, null, null);
                continue;
            }

            var displayName = ReadString(property.Value, "displayName") ?? login;

            if (!property.Value.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
            {
                into[login] = new TwitchStreamStatus(login, displayName, true, false, null, null, null, null);
                continue;
            }

            into[login] = new TwitchStreamStatus(
                login,
                displayName,
                true,
                true,
                ReadString(stream, "type"),
                ReadString(stream, "title"),
                stream.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Object ? ReadString(game, "displayName") : null,
                stream.TryGetProperty("viewersCount", out var viewers) && viewers.TryGetInt32(out var count) ? count : null);
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string text)
        => text.Length <= 300 ? text : text[..300] + "...";

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }

    /// <summary>
    /// Builds one GraphQL document asking after a chunk of channels.
    /// </summary>
    /// <remarks>
    /// Logins go in as variables, so the generated query text only ever contains names this file made up.
    /// The operation name has to be repeated in "operationName" - Twitch answers with
    /// "operation with name 'x' not found" otherwise - and it differs per document because a batch may
    /// not contain two operations of the same name.
    /// </remarks>
    private static string BuildStatusDocument(IReadOnlyList<string> logins, int index)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var selections = new List<string>(logins.Count);
        var declarations = new List<string>(logins.Count);

        for (var i = 0; i < logins.Count; i++)
        {
            var name = "v" + i.ToString(CultureInfo.InvariantCulture);
            declarations.Add("$" + name + ": String!");
            selections.Add(TwitchChannelCatalog.AliasFor(logins[i]) + ": user(login: $" + name + ") { displayName stream { type title game { displayName } viewersCount } }");
            variables[name] = logins[i];
        }

        var query = "query TwitchLiveStatus" + index.ToString(CultureInfo.InvariantCulture) + "(" + string.Join(", ", declarations) + ") { " + string.Join(" ", selections) + " }";

        return JsonSerializer.Serialize(new StatusDocument
        {
            OperationName = "TwitchLiveStatus" + index.ToString(CultureInfo.InvariantCulture),
            Query = query,
            Variables = variables
        });
    }

    private async Task<HttpResponseMessage> PostAsync(string body, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("Client-Id", config.ClientId);

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The body says what Twitch objected to, which is the difference between a useful log line
            // and "response status code indicates failure".
            var detail = string.Empty;
            try
            {
                detail = Truncate(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Twitch Live TV: could not read the error body");
            }

            response.Dispose();
            throw new InvalidOperationException($"Twitch answered {(int)response.StatusCode} for '{GraphQlUrl}'{(string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail)}");
        }

        return response;
    }

    /// <summary>The playback-token request; the login travels as a GraphQL variable.</summary>
    private sealed class TokenRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("operationName")]
        public string OperationName { get; set; } = "TwitchLiveToken";

        [System.Text.Json.Serialization.JsonPropertyName("query")]
        public string Query { get; set; } = """
            query TwitchLiveToken($channel: String!) { streamPlaybackAccessToken(channelName: $channel, params: { platform: "web", playerBackend: "mediaplayer", playerType: "channel" }) { value signature } }
            """.Trim();

        [System.Text.Json.Serialization.JsonPropertyName("variables")]
        public Dictionary<string, string> Variables { get; set; } = new();
    }

    /// <summary>One document of the channel-state request.</summary>
    private sealed class StatusDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("operationName")]
        public string OperationName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("variables")]
        public Dictionary<string, string> Variables { get; set; } = new();
    }
}
