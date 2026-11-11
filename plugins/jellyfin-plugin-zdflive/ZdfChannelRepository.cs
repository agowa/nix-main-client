using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ZdfLive.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>A catalogued channel together with the stream URL that is current right now.</summary>
public sealed record ResolvedChannel(ZdfChannelDefinition Definition, string Url);

/// <summary>
/// Turns the static catalogue into playable channels by looking up each channel's master playlist in
/// the MediathekView Zapp API. That API is what keeps this plugin working when a CDN path moves: the
/// URLs are read at runtime and the ones baked into <see cref="ZdfChannelCatalog"/> are only a
/// fallback for when the API itself cannot be reached.
/// </summary>
public sealed class ZdfChannelRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>How long to wait before retrying after a failed lookup.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZdfChannelRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, string>? _urlByKey;
    private DateTime _expiresUtc;
    private string? _lastError;

    /// <summary>Initializes a new instance of the <see cref="ZdfChannelRepository"/> class.</summary>
    public ZdfChannelRepository(IHttpClientFactory httpClientFactory, ILogger<ZdfChannelRepository> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the reason the last lookup failed, or <c>null</c> when it worked.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Gets the enabled channels with a current stream URL each.
    /// </summary>
    /// <param name="forceRefresh">Ignore the cached URLs and query the API again.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<ResolvedChannel>> GetChannelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        await EnsureUrlsAsync(forceRefresh, config, cancellationToken).ConfigureAwait(false);

        var urls = _urlByKey;
        var result = new List<ResolvedChannel>();

        foreach (var definition in ZdfChannelCatalog.All)
        {
            if (!IsEnabled(config.EnabledChannels, definition.Key))
            {
                continue;
            }

            // Prefer the URL from the API; fall back to the one baked into the catalogue.
            var url = urls is not null && urls.TryGetValue(definition.Key, out var resolved)
                ? resolved
                : definition.FallbackUrl;

            if (!string.IsNullOrWhiteSpace(url))
            {
                result.Add(new ResolvedChannel(definition, url));
            }
        }

        return result;
    }

    /// <summary>Resolves a single channel by its Jellyfin channel id.</summary>
    public async Task<ResolvedChannel?> ResolveAsync(string? channelId, CancellationToken cancellationToken)
    {
        var key = StripServicePrefix(channelId);
        if (key is null)
        {
            return null;
        }

        var channels = await GetChannelsAsync(false, cancellationToken).ConfigureAwait(false);
        return channels.FirstOrDefault(c => string.Equals(c.Definition.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Jellyfin hands a tuner host the bare channel id it received from <see cref="GetChannelsAsync"/>,
    /// but ids travelling through the Live TV service can carry the service prefix, so accept both.
    /// Keys themselves contain underscores ("zdf_neo"), hence split on the first separator only.
    /// </summary>
    private static string? StripServicePrefix(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        var id = channelId.Trim();
        if (ZdfChannelCatalog.Find(id) is not null)
        {
            return id;
        }

        var separator = id.IndexOf('_', StringComparison.Ordinal);
        return separator == -1 ? null : ZdfChannelCatalog.Find(id[(separator + 1)..])?.Key;
    }

    private static bool IsEnabled(string[]? enabledKeys, string key)
    {
        // A missing list means "the defaults"; an empty one is a deliberate choice by the operator.
        if (enabledKeys is null || enabledKeys.Length == 0)
        {
            enabledKeys = ZdfChannelCatalog.DefaultKeys;
        }

        return enabledKeys.Any(k => string.Equals(k?.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the API should be queried now. A failed lookup is honoured even by callers that ask for
    /// a forced refresh: one guide refresh asks this host several times (channel list, then each media
    /// source), and without the backoff an unreachable API would be retried - and warned about - once
    /// per question instead of once per <see cref="RetryAfterFailure"/>.
    /// </summary>
    private bool ShouldFetch(bool forceRefresh)
    {
        if (_lastError is not null)
        {
            return DateTime.UtcNow >= _expiresUtc;
        }

        return forceRefresh || _urlByKey is null || DateTime.UtcNow >= _expiresUtc;
    }

    private async Task EnsureUrlsAsync(bool forceRefresh, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!ShouldFetch(forceRefresh))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ShouldFetch(forceRefresh))
            {
                return;
            }

            try
            {
                var fetched = await FetchUrlsAsync(config, cancellationToken).ConfigureAwait(false);

                // Keep whatever we had for keys the API did not mention, so a partial answer cannot
                // make channels disappear.
                var merged = _urlByKey is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(_urlByKey, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in fetched)
                {
                    merged[pair.Key] = pair.Value;
                }

                _urlByKey = merged;
                _expiresUtc = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, config.CacheMinutes));
                if (_lastError is not null)
                {
                    _logger.LogInformation("ZDF Live TV: stream lookup works again after {Error}", _lastError);
                    _lastError = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not fatal: the catalogue's own URLs are used until the API answers again.
                _lastError = ex.Message;
                _expiresUtc = DateTime.UtcNow + RetryAfterFailure;
                _logger.LogWarning(
                    "ZDF Live TV: could not refresh stream URLs from {Url} ({Error}); using {Cached} cached and built-in URLs for {Minutes} minutes",
                    config.ApiBaseUrl,
                    ex.Message,
                    _urlByKey?.Count ?? 0,
                    RetryAfterFailure.TotalMinutes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> FetchUrlsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.GetAsync(config.ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

#if NET9_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var entries = await JsonSerializer
            .DeserializeAsync<Dictionary<string, ChannelEntry>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entries is null)
        {
            return urls;
        }

        foreach (var pair in entries)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value?.StreamUrl))
            {
                urls[pair.Key] = pair.Value!.StreamUrl!;
            }
        }

        _logger.LogDebug("ZDF Live TV: resolved {Count} stream URLs from {Url}", urls.Count, config.ApiBaseUrl);
        return urls;
    }

    /// <summary>One entry of the API answer; only the stream URL is of interest.</summary>
    private sealed class ChannelEntry
    {
        [JsonPropertyName("streamUrl")]
        public string? StreamUrl { get; set; }
    }
}
