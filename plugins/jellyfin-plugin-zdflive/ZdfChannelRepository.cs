using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ZdfLive.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>A channel the upstream API knows about, whether or not this plugin's catalogue does.</summary>
/// <param name="Key">The API key, which doubles as the channel id suffix.</param>
/// <param name="Name">What the API calls it. Known to be unreliable for a few channels ("NRD Niedersachsen").</param>
/// <param name="Catalogued">Whether the curated catalogue has an entry with guide id and logo for it.</param>
public sealed record DiscoveredChannel(string Key, string Name, bool Catalogued);

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

    /// <summary>Deadline for one logo address.</summary>
    private static readonly TimeSpan LogoCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Deadline for a whole batch of them, so the channel list is never held up for long.</summary>
    private static readonly TimeSpan LogoBatchBudget = TimeSpan.FromSeconds(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZdfChannelRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, string>? _urlByKey;

    /// <summary>How many logo verdicts are remembered before the expired ones are dropped.</summary>
    private const int MaxCachedLogos = 512;

    /// <summary>Largest answer this plugin will read from the stream API. The real one is a few hundred kilobytes.</summary>
    private const long MaxApiBodyBytes = 8 * 1024 * 1024;

    /// <summary>Longest wait a <c>Retry-After</c> is allowed to impose before it is treated as a mistake.</summary>
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Channel names per key, as the API reports them. Also what makes an unlisted channel offerable.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale rather than edited in place, exactly like <see cref="_urlByKey"/>: it is written while
    /// holding <see cref="_gate"/> but read by the settings page and by <see cref="Synthesise"/> without it, and
    /// a Dictionary being grown underneath a reader is how "collection modified" and half-built buckets happen.
    /// </remarks>
    private Dictionary<string, string> _nameByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current name table, read the way every unlocked reader should read it.</summary>
    private Dictionary<string, string> Names => Volatile.Read(ref _nameByKey);

    /// <summary>
    /// Whether each logo URL was last found to answer. A channel's image is written into its database
    /// item once and never replaced afterwards (GuideManager only sets it while the item has none), so a
    /// URL that 404s would leave that channel showing a broken image for good - checking first turns a
    /// permanent mistake into nothing at all.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedLogo> _logos = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _expiresUtc;
    private string? _lastError;

    /// <summary>Initializes a new instance of the <see cref="ZdfChannelRepository"/> class.</summary>
    public ZdfChannelRepository(IHttpClientFactory httpClientFactory, ILogger<ZdfChannelRepository> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Once per process: the settings page is built from a static list, and this is how a channel
        // added to the catalogue but not to that list gets noticed.
        ZdfChannelCatalog.VerifyEmbeddedList(_logger);
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

        // The configured keys decide both the set and the order, because that is what the channel
        // numbers come from. A key the catalogue does not know is still offerable as long as the API
        // has a stream for it - which is how a channel the broadcasters add later becomes usable without
        // waiting for this plugin to be updated. Such a channel simply has no guide id, so Jellyfin's
        // automatic matching falls back to comparing names.
        foreach (var key in RequestedKeys(config.EnabledChannels))
        {
            var definition = ZdfChannelCatalog.Find(key);

            // Prefer the URL from the API; fall back to the one baked into the catalogue.
            var url = urls is not null && urls.TryGetValue(key, out var resolved)
                ? resolved
                : definition?.FallbackUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                if (definition is null)
                {
                    _logger.LogWarning(
                        "ZDF Live TV: '{Key}' is enabled but the stream API does not know it either, so no channel is offered for it",
                        key);
                }

                continue;
            }

            result.Add(new ResolvedChannel(definition ?? Synthesise(key), url));
        }

        return result;
    }

    /// <summary>
    /// Checks which of these logo URLs are actually there, once per URL, remembering the answers.
    /// </summary>
    /// <remarks>
    /// Why this exists at all: a channel's picture is written into its database item once and never
    /// replaced after that (GuideManager only sets it while the item has none), so handing out one address
    /// that turns out not to exist leaves that channel showing a broken image for good, while handing out
    /// nothing costs a picture until the next refresh. Only therefore is a definite "not here" worth
    /// acting on - and only that:
    /// <list type="bullet">
    ///   <item><description>Answered 2xx: offer it.</description></item>
    ///   <item><description>Answered 403/404/410: do not, and say so once in the log.</description></item>
    ///   <item><description>Timeout or transport error: offer it anyway. A logo host that is merely slow -
    /// first connection through a proxy, IPv6 falling back to IPv4, a cold CDN edge - must not end up
    /// switching every picture off, because the address was probably fine after all.</description></item>
    /// </list>
    /// <para>
    /// The whole batch also has one shared deadline, so the slowest possible answer costs that rather than
    /// the HTTP client's timeout per channel: this runs inside the channel list, which both the guide
    /// refresh and the tuner page wait for.
    /// </para>
    /// </remarks>
    /// <param name="urls">What the catalogue produced per channel - an address or a bundled file; nulls and
    /// duplicates are ignored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subset that should be offered.</returns>
    public async Task<HashSet<string>> VerifyLogosAsync(IReadOnlyCollection<string?> urls, CancellationToken cancellationToken)
    {
        // A logo that ships inside the plugin is not something to ask a host about: it was either unpacked
        // onto disk or it was not (see ChannelLogoAssets), which is a question File.Exists answers better.
        var bundled = urls
            .Where(url => !string.IsNullOrWhiteSpace(url) && !ZdfChannelCatalog.IsRemote(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wanted = urls
            .Where(url => !string.IsNullOrWhiteSpace(url) && ZdfChannelCatalog.IsRemote(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknown = new List<string>();
        foreach (var url in wanted)
        {
            if (!_logos.TryGetValue(url, out var cached) || DateTime.UtcNow >= cached.ExpiresUtc)
            {
                unknown.Add(url);
            }
        }

        // A logo host that gets pointed at a new address per channel - or an operator who keeps pasting new
        // ones into the settings page - would otherwise leave one entry here forever.
        if (_logos.Count > MaxCachedLogos)
        {
            var nowUtc = DateTime.UtcNow;
            foreach (var stale in _logos.Where(p => p.Value.ExpiresUtc <= nowUtc).Select(p => p.Key).ToList())
            {
                _logos.TryRemove(stale, out _);
            }
        }

        if (unknown.Count > 0)
        {
            var checks = unknown
                .Select(url => CheckLogoAsync(url, cancellationToken))
                .ToList();

            // Wait for them together, but not longer than the batch's share of the channel list's time.
            // Whatever is still outstanding when that passes keeps answering in the background and records
            // its verdict for the next refresh; this pass treats it as usable (see below).
            foreach (var check in checks)
            {
                // The batch may return before these do, and nothing else will ever look at them; each one
                // already handles its own failures, this is only so a late exception cannot go unobserved.
                _ = check.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            var all = Task.WhenAll(checks);
            var winner = await Task.WhenAny(all, Task.Delay(LogoBatchBudget, CancellationToken.None)).ConfigureAwait(false);

            if (winner != all)
            {
                _logger.LogDebug(
                    "ZDF Live TV: only some of the {Total} logo addresses answered within {Seconds}s; the rest are offered unchecked",
                    unknown.Count,
                    LogoBatchBudget.TotalSeconds);
            }
        }

        var usable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in bundled)
        {
            if (File.Exists(file))
            {
                usable.Add(file);
            }
            else
            {
                _logger.LogWarning("ZDF Live TV: the bundled logo {File} is not on disk; the channel is offered without one", file);
            }
        }

        foreach (var url in wanted)
        {
            // No verdict yet means the batch ran out of time: offer it rather than lose the picture.
            if (!_logos.TryGetValue(url, out var verdict) || verdict.Exists)
            {
                usable.Add(url);
            }
        }

        return usable;
    }

    /// <summary>
    /// Says whether one logo address is there, on the remembered answer where there is a fresh one.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="VerifyLogosAsync"/> this waits for a verdict instead of assuming one: callers
    /// use it to decide that something already stored is genuinely gone, and guessing in either
    /// direction there is expensive. The bias stays the same though - only 403/404/410 say "missing".
    /// </remarks>
    /// <param name="url">The address to ask.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> unless the address is known not to be served.</returns>
    public async Task<bool> LogoExistsAsync(string url, CancellationToken cancellationToken)
    {
        if (_logos.TryGetValue(url, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
        {
            return cached.Exists;
        }

        return await CheckLogoAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks one logo URL whether it exists, and records the verdict.</summary>
    private async Task<bool> CheckLogoAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LogoCheckTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Remember(url, true);
                return true;
            }

            var code = (int)response.StatusCode;
            if (code is 403 or 404 or 410)
            {
                // Definite: nothing will be served at this address.
                Remember(url, false);
                _logger.LogInformation(
                    "ZDF Live TV: no logo at {Url}; the channel is offered without one - its own URL in the plugin settings would fix that",
                    url);
                return false;
            }

            // Anything else (5xx, a redirect loop, a host that dislikes HEAD) says nothing about whether
            // the picture exists.
            Remember(url, true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeout or transport failure: assume the address is fine. See VerifyLogosAsync for why this
            // direction is the cheap one to be wrong in.
            Remember(url, true);
            _logger.LogDebug(ex, "ZDF Live TV: could not check the logo at {Url}; offering it unchecked", url);
            return true;
        }
    }

    /// <summary>Records a verdict, re-asking about missing logos sooner than working ones.</summary>
    private void Remember(string url, bool exists)
        => _logos[url] = new CachedLogo(exists, DateTime.UtcNow + (exists ? TimeSpan.FromDays(1) : TimeSpan.FromHours(6)));

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

    /// <summary>
    /// The keys to offer, in the order they were configured.
    /// </summary>
    /// <remarks>
    /// Only a list that is not there at all means "the defaults": that is what the constructor puts in
    /// place, and what a configuration file written before this option existed deserializes to. An empty
    /// one is somebody's deliberate choice - the settings page says so before saving it - and offering
    /// the whole default set back at them would make "deselect everything" mean its opposite.
    /// </remarks>
    private static List<string> RequestedKeys(string[]? enabledKeys)
    {
        if (enabledKeys is null)
        {
            return ZdfChannelCatalog.DefaultKeys.ToList();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>(enabledKeys.Length);

        foreach (var entry in enabledKeys)
        {
            var key = entry?.Trim();
            if (!string.IsNullOrEmpty(key) && seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>
    /// Makes a catalogue entry out of a key only the upstream API knows. No guide id, so programme data
    /// can still attach - just by name rather than by id - and no logo until one is configured.
    /// </summary>
    private ZdfChannelDefinition Synthesise(string key)
    {
        var names = Names;
        var name = names.TryGetValue(key, out var discovered) && !string.IsNullOrWhiteSpace(discovered)
            ? discovered
            : key;

        return new ZdfChannelDefinition(key, name, string.Empty, string.Empty, false, "ARD/ZDF Live");
    }

    /// <summary>
    /// Gets every channel that could be enabled: the curated catalogue plus whatever the stream API is
    /// currently serving. The configuration page lists this, which is why a new channel of the
    /// broadcasters appears there without a rebuild.
    /// </summary>
    /// <param name="forceRefresh">Ignore the cached answer and query the API again.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<DiscoveredChannel>> GetKnownChannelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        await EnsureUrlsAsync(forceRefresh, config, cancellationToken).ConfigureAwait(false);

        var knownNames = Names;
        var result = new List<DiscoveredChannel>(ZdfChannelCatalog.All.Count + knownNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Catalogue first and in its order: those entries carry a guide id and a logo. The API's own
        // names are deliberately not used for them - it answers "NRD Niedersachsen" for NDR.
        foreach (var definition in ZdfChannelCatalog.All)
        {
            if (seen.Add(definition.Key))
            {
                result.Add(new DiscoveredChannel(definition.Key, definition.Name, true));
            }
        }

        foreach (var pair in knownNames.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(pair.Key))
            {
                result.Add(new DiscoveredChannel(pair.Key, pair.Value, false));
            }
        }

        return result;
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
            catch (UpstreamRateLimitedException ex)
            {
                // The answer said how long to wait, so that is the backoff rather than the built-in five minutes:
                // asking again sooner is what turns one limit into a permanently blocked client.
                _lastError = ex.Message;
                _expiresUtc = DateTime.UtcNow + ex.Delay;
                _logger.LogWarning(
                    "ZDF Live TV: the stream API rate limited this server ({Error}); not asking again for {Seconds} seconds, using {Cached} cached and built-in URLs meanwhile",
                    ex.Message,
                    (int)ex.Delay.TotalSeconds,
                    _urlByKey?.Count ?? 0);
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

    /// <summary>
    /// Asks the stream API which master playlist each channel currently has.
    /// </summary>
    /// <remarks>
    /// Two things this endpoint's answer cannot be trusted about, both handled here rather than left to luck:
    /// <list type="bullet">
    ///   <item><description>The address comes from the settings page, so it is checked before anything is asked of
    /// it. An operator pointing it at their own proxy is expected; one typing <c>file://</c>, or having an option
    /// fed to them by something else, is not - and this plugin runs as the Jellyfin user, which can read the
    /// server's own configuration.</description></item>
    ///   <item><description>The answer is deserialised in full, so it is capped: a host that returns something
    /// enormous - by mistake or on purpose - should cost this process a few megabytes and a warning, not every
    /// byte of memory the server has.</description></item>
    /// </list>
    /// Names are merged into a copy of the current table and published in one assignment (see
    /// <see cref="_nameByKey"/>), because readers exist that never take <see cref="_gate"/>.
    /// </remarks>
    private async Task<Dictionary<string, string>> FetchUrlsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!IsAllowedEndpoint(config.ApiBaseUrl))
        {
            throw new InvalidOperationException(
                $"'{config.ApiBaseUrl}' is not an http(s) address with a host, so the stream API was not asked - set it in the plugin settings");
        }

        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.GetAsync(config.ApiBaseUrl, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new UpstreamRateLimitedException(RetryAfter(response), Truncate(response.ReasonPhrase ?? string.Empty));
        }

        response.EnsureSuccessStatusCode();

#if NET9_0_OR_GREATER
        await using var streamed = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var streamed = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var body = await ReadCappedAsync(streamed, MaxApiBodyBytes, cancellationToken).ConfigureAwait(false);

        var entries = JsonSerializer.Deserialize<Dictionary<string, ChannelEntry>>(body, JsonOptions);

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entries is null)
        {
            return urls;
        }

        var names = new Dictionary<string, string>(Names, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in entries)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value?.Name))
            {
                names[pair.Key] = pair.Value!.Name!;
            }

            if (!string.IsNullOrWhiteSpace(pair.Value?.StreamUrl))
            {
                urls[pair.Key] = pair.Value!.StreamUrl!;
            }
        }

        Volatile.Write(ref _nameByKey, names);

        _logger.LogDebug("ZDF Live TV: resolved {Count} stream URLs from {Url}", urls.Count, config.ApiBaseUrl);
        return urls;
    }

    /// <summary>
    /// Says whether a configured address may be requested at all.
    /// </summary>
    /// <remarks>
    /// Only the scheme and the presence of a host are judged, not the host itself: pointing this at a proxy or a
    /// mirror of one's own is a documented setting, while <c>file://</c>, <c>gopher://</c> and an address without
    /// a host are never what the option means. Loopback over plain http stays allowed because that is exactly
    /// what a local proxy looks like; everything else has to be https, which is what the real endpoint is.
    /// </remarks>
    internal static bool IsAllowedEndpoint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        // Plain http is only what the option means when something on this very machine is answering: a proxy or
        // a local mirror. Anywhere else, sending it over http - with the channel list in the clear - would be a
        // downgrade this plugin should not quietly agree to.
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(uri.Host, out var host) && IPAddress.IsLoopback(host));
    }

    /// <summary>Reads a stream up to a limit, and says so in the exception when the answer is bigger than that.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException(
                    $"the answer was larger than the {maxBytes / (1024 * 1024)} MB this plugin accepts from the stream API");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>How long a <c>Retry-After</c> asks for, or the ordinary retry delay when it does not say.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;

        // Both forms RFC 9110 allows: a number of seconds, or a date. Either is clamped - an API asking for a
        // day of silence gets waited out for fifteen minutes and asked again, because losing the channel list
        // for a day over one header would be the worse outcome, and this plugin has no way to tell an operator
        // that it is obeying a deadline.
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > MaxRateLimitWait ? MaxRateLimitWait : delta;
        }

        if (retry?.Date is { } date && date > DateTimeOffset.UtcNow)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > MaxRateLimitWait ? MaxRateLimitWait : wait;
        }

        // No header to go by: the ordinary backoff, which is already longer than nothing.
        return RetryAfterFailure;
    }

    private static string Truncate(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= 200 ? text : text[..200] + "...");

    /// <summary>A logo verdict and the moment it is checked again.</summary>
    private sealed record CachedLogo(bool Exists, DateTime ExpiresUtc);

    /// <summary>One entry of the API answer: where to stream it and what they call it.</summary>
    private sealed class ChannelEntry
    {
        [JsonPropertyName("streamUrl")]
        public string? StreamUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
