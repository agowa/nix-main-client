using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// One configured channel, with everything Twitch currently says about it.
/// </summary>
/// <param name="Login">The normalised login.</param>
/// <param name="DisplayName">What to call the channel: Twitch's own display name when it has one.</param>
/// <param name="Status">The last answer Twitch gave for this login.</param>
/// <param name="LiveSinceUtc">When this stream was first seen running, if it is running.</param>
/// <param name="TitleSinceUtc">When the current title was first seen, if it is live.</param>
public sealed record TwitchChannel(
    string Login,
    string DisplayName,
    TwitchStreamStatus Status,
    DateTime? LiveSinceUtc,
    DateTime? TitleSinceUtc)
{
    /// <summary>Where this login came from: the settings list, the follow list, or both.</summary>
    public bool FromFollows { get; init; }
}

/// <summary>
/// Keeps the configured channels and what Twitch says about them in one place.
/// </summary>
/// <remarks>
/// Three things happen here on top of calling <see cref="TwitchApi"/>:
/// <list type="bullet">
///   <item><description>Answers are cached for <see cref="PluginConfiguration.StatusCacheSeconds"/>, because a guide refresh asks this plugin many questions in a row.</description></item>
///   <item><description>A failed lookup keeps the previous answers instead of declaring every channel offline, and is retried no sooner than five minutes later - see the note on <see cref="ShouldFetch"/>.</description></item>
///   <item><description>The moment a stream started is remembered locally. Twitch's public player API does not report one, so "live since" can only come from watching it.</description></item>
/// </list>
/// The channel list itself is re-read from the plugin configuration on every call, which is what makes
/// editing it at runtime work without a restart.
/// </remarks>
public sealed class TwitchChannelRepository
{
    /// <summary>How long to wait before retrying after a failed lookup.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>How long a fetched schedule is reused. The feed itself asks for six hours.</summary>
    private static readonly TimeSpan ScheduleCacheLifetime = TimeSpan.FromHours(6);

    private readonly TwitchApi _api;
    private readonly TwitchHelix _helix;
    private readonly ILogger<TwitchChannelRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Last known state per login, kept across failures so channels do not vanish.</summary>
    private Dictionary<string, TrackedChannel> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Order of the configured logins at the time of the last refresh.</summary>
    private List<string> _logins = [];

    /// <summary>How many playback tokens are kept. One tune-in costs one, and an entry is worth seconds.</summary>
    private const int MaxCachedTokens = 512;

    /// <summary>How many published schedules are kept before the oldest expired ones go.</summary>
    private const int MaxCachedSchedules = 512;

    /// <summary>How many rejected logins are remembered, so a settings list full of typos cannot grow it forever.</summary>
    private const int MaxWarnedLogins = 2048;

    /// <summary>
    /// Playback tokens per login, kept just long enough to serve one tune-in.
    /// </summary>
    /// <remarks>
    /// A concurrent dictionary rather than a plain one: unlike the channel state these are written by whoever
    /// happens to be tuning in, which is not the thread holding <see cref="_gate"/> - and two viewers starting
    /// a stream at the same moment used to be able to corrupt the internal arrays of a shared Dictionary.
    /// </remarks>
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Published schedules per login. They change on the order of days, not minutes.</summary>
    /// <remarks>Concurrent for the same reason as <see cref="_tokens"/>: the guide asks per channel.</remarks>
    private readonly ConcurrentDictionary<string, CachedSchedule> _schedules = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _expiresUtc;
    private string? _lastError;

    /// <summary>
    /// Logins already complained about, used as a set.
    /// </summary>
    /// <remarks>
    /// Concurrent because it is consulted from two places that do not share a lock: the settings list is checked
    /// while building every channel list, and the follow list on its way in - neither of which holds
    /// <see cref="_gate"/>, which only covers the refresh itself.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _warnedAboutLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="TwitchChannelRepository"/> class.</summary>
    public TwitchChannelRepository(TwitchApi api, TwitchHelix helix, ILogger<TwitchChannelRepository> logger)
    {
        _api = api;
        _helix = helix;
        _logger = logger;
    }

    /// <summary>Gets the reason the last lookup failed, or <c>null</c> when it worked.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Gets every configured channel in configuration order.
    /// </summary>
    /// <param name="forceRefresh">Ignore the cached answers and ask Twitch again.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<TwitchChannel>> GetChannelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var configured = ReadConfiguredLogins(config);
        var followed = await GetFollowedLoginsAsync(config, cancellationToken).ConfigureAwait(false);

        // Membership is asked per login below and the follow list can be two thousand long, so it is a set:
        // asking a List whether one of those contains a login used to make building one channel list quadratic.
        var followedSet = new HashSet<string>(followed, StringComparer.OrdinalIgnoreCase);

        // Hand-picked logins first and in their configured order: they decide the channel numbers.
        // Follows are appended after them, newest follow first, so importing never renumbers anything.
        var logins = new List<string>(configured.Count + followed.Count);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var login in configured)
        {
            if (added.Add(login))
            {
                logins.Add(login);
            }
        }

        foreach (var login in followed)
        {
            if (added.Add(login))
            {
                logins.Add(login);
            }
        }

        await EnsureStatusesAsync(forceRefresh, config, logins, cancellationToken).ConfigureAwait(false);

        var tracked2 = Volatile.Read(ref _byLogin);
        var result = new List<TwitchChannel>(logins.Count);

        foreach (var login in logins)
        {
            if (tracked2.TryGetValue(login, out var tracked))
            {
                result.Add(tracked.ToChannel(followedSet.Contains(login)));
            }
            else
            {
                // Never answered about - either nothing was fetched yet, or this login is not worth
                // asking Twitch about at all (see EnsureStatusesAsync). Either way it is not live.
                result.Add(new TwitchChannel(login, login, new TwitchStreamStatus(login, login, false, false, null, null, null, null), null, null)
                {
                    FromFollows = followedSet.Contains(login)
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Finds one configured channel by the id a tuner host was handed.
    /// </summary>
    /// <remarks>
    /// Jellyfin does not always hand back the bare id this plugin returned from <c>GetChannels</c>: ids
    /// travelling through the Live TV service arrive with a prefix in front of them, and that prefix is
    /// the tuner source's own identifier rather than this plugin's type. So instead of guessing the
    /// prefix, every suffix after an underscore is tried against the configured logins - which also
    /// copes with logins that contain underscores themselves ("esl_sc2").
    /// </remarks>
    public async Task<TwitchChannel?> FindAsync(string? channelId, bool forceRefresh, CancellationToken cancellationToken)
    {
        var channels = await GetChannelsAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        if (channels.Count == 0)
        {
            return null;
        }

        var id = TwitchChannelCatalog.Normalize(channelId);
        if (id is null)
        {
            return null;
        }

        // One pass over the list to index it, then every suffix below is a lookup. With two thousand followed
        // channels the previous shape - scanning the whole list once per underscore in the id - was the most
        // expensive thing a tune-in did.
        var byLogin = new Dictionary<string, TwitchChannel>(channels.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            byLogin[channel.Login] = channel;
        }

        // The first pass tries the id as it arrived, so the common case costs one comparison.
        var stripped = false;

        while (!string.IsNullOrEmpty(id))
        {
            byLogin.TryGetValue(id, out var match);
            if (match is not null)
            {
                if (stripped)
                {
                    _logger.LogDebug("Twitch Live TV: resolved channel id '{ChannelId}' to '{Login}'", channelId, match.Login);
                }

                return match;
            }

            stripped = true;

            var separator = id.IndexOf('_', StringComparison.Ordinal);
            if (separator == -1 || separator == id.Length - 1)
            {
                // Nothing left to strip: this id belongs to another tuner.
                break;
            }

            id = id[(separator + 1)..];
        }

        _logger.LogDebug("Twitch Live TV: '{ChannelId}' is not one of the {Count} configured channels", channelId, channels.Count);
        return null;
    }

    /// <summary>
    /// Gets a master playlist URL for a channel, or <c>null</c> when it cannot be played.
    /// </summary>
    /// <remarks>
    /// Offline channels are refused here rather than handed to ffmpeg: usher answers such a request with
    /// <c>404 {"error":"Can not find channel"}</c>, so the viewer would only see an encoder failure for
    /// what is really "this streamer is not streaming".
    /// </remarks>
    public async Task<string?> GetStreamUrlAsync(string login, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!TwitchApi.IsPlausibleLogin(login))
        {
            return null;
        }

        if (!_tokens.TryGetValue(login, out var cached) || DateTime.UtcNow >= cached.ExpiresUtc)
        {
            // Tokens are bound to the requesting IP and only authorise fetching the playlist, so they
            // are fetched at tune-in time and kept briefly - just long enough that the media source
            // request and the stream request that follow a click on Play do not each cost one.
            var token = await _api.GetPlaybackTokenAsync(login, cancellationToken).ConfigureAwait(false);
            cached = new CachedToken(token, DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0, config.TokenCacheSeconds)));
            _tokens[login] = cached;

            // Nothing else retires these: a channel watched once an hour for a year would otherwise leave one
            // entry per login that was ever played, each holding a token nobody will ask about again.
            if (_tokens.Count > MaxCachedTokens)
            {
                var nowUtc = DateTime.UtcNow;
                foreach (var stale in _tokens.Where(p => p.Value.ExpiresUtc <= nowUtc).Select(p => p.Key).ToList())
                {
                    _tokens.TryRemove(stale, out _);
                }
            }
        }

        return TwitchApi.BuildStreamUrl(login, cached.Token);
    }

    /// <summary>
    /// The configured logins, normalised, de-duplicated and checked, in configuration order.
    /// </summary>
    /// <remarks>
    /// A login that is not a syntactically possible Twitch login is dropped here rather than carried along: it
    /// can never be live, so as a channel it is only a tile that never lights up - and further down this plugin
    /// the login doubles as a file name (one picture per streamer under <c>avatars/&lt;login&gt;.png</c>), which is
    /// not a job for a string that arrived from a settings box. The one-time warning is what tells the operator
    /// where their channel went, since the guide no longer shows it at all.
    /// </remarks>
    private List<string> ReadConfiguredLogins(PluginConfiguration config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logins = new List<string>();

        foreach (var entry in config.Channels ?? [])
        {
            var login = TwitchChannelCatalog.Normalize(entry);
            if (login is null || !seen.Add(login))
            {
                continue;
            }

            if (!TwitchApi.IsPlausibleLogin(login))
            {
                // Said once per login rather than once per channel list, because this runs on every guide
                // question - and said at all because the channel simply does not appear otherwise.
                if (_warnedAboutLogin.TryAdd(login, 0))
                {
                    _logger.LogWarning(
                        "Twitch Live TV: '{Login}' is not a valid Twitch login (3-25 letters, digits and underscores), so no channel is offered for it - fix it in the plugin settings",
                        login);
                }

                if (_warnedAboutLogin.Count > MaxWarnedLogins)
                {
                    // Past a point this is not a memory of what was said but a ledger of everything that could be.
                    _warnedAboutLogin.Clear();
                }

                continue;
            }

            logins.Add(login);
        }

        return logins;
    }

    /// <summary>
    /// Whether Twitch should be asked now. A failed lookup is honoured even by callers that ask for a
    /// forced refresh: one guide refresh asks this host several times, and without the backoff an
    /// unreachable endpoint would be retried - and complained about - once per question instead of once
    /// per <see cref="RetryAfterFailure"/>.
    /// </summary>
    private bool ShouldFetch(bool forceRefresh)
    {
        if (_lastError is not null)
        {
            return DateTime.UtcNow >= _expiresUtc;
        }

        return forceRefresh || DateTime.UtcNow >= _expiresUtc;
    }

    private async Task EnsureStatusesAsync(bool forceRefresh, PluginConfiguration config, List<string> logins, CancellationToken cancellationToken)
    {
        // A changed channel list must be fetched at once: the new entries have no cached state, and
        // waiting for the previous list's expiry would make a runtime edit look like it did nothing.
        var configurationChanged = !Volatile.Read(ref _logins).SequenceEqual(logins, StringComparer.OrdinalIgnoreCase);

        if (!configurationChanged && !ShouldFetch(forceRefresh))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!configurationChanged && !ShouldFetch(forceRefresh))
            {
                return;
            }

            // Forget channels that were removed, but keep the rest - including their "live since" times.
            var known = Volatile.Read(ref _byLogin);
            var tracked = new Dictionary<string, TrackedChannel>(StringComparer.OrdinalIgnoreCase);
            foreach (var login in logins)
            {
                if (known.TryGetValue(login, out var existing))
                {
                    tracked[login] = existing;
                }
            }

            // A typo is not worth a request per refresh; it is reported once instead.
            var askable = new List<string>(logins.Count);
            foreach (var login in logins)
            {
                if (TwitchApi.IsPlausibleLogin(login))
                {
                    askable.Add(login);
                }
                else if (_warnedAboutLogin.TryAdd(login, 0))
                {
                    _logger.LogWarning("Twitch Live TV: '{Login}' is not a valid Twitch login (3-25 letters, digits and underscores) - fix it in the plugin settings", login);
                }
            }

            try
            {
                var statuses = await _api.GetStatusesAsync(askable, cancellationToken).ConfigureAwait(false);
                var nowUtc = DateTime.UtcNow;

                foreach (var pair in statuses)
                {
                    if (!tracked.TryGetValue(pair.Key, out var previous))
                    {
                        // First answer about this channel: "since now" is the only honest start time.
                        tracked[pair.Key] = new TrackedChannel(
                            pair.Value,
                            pair.Value.IsLive ? nowUtc : null,
                            pair.Value.IsLive ? nowUtc : null);
                        continue;
                    }

                    // "Live since" and "title since" only move when the state actually changes, so a
                    // refresh cannot reset a stream that has been running for hours.
                    DateTime? liveSince = null;
                    DateTime? titleSince = null;

                    if (pair.Value.IsLive)
                    {
                        liveSince = previous.LiveSinceUtc ?? nowUtc;

                        var sameTitle = string.Equals(previous.Status.Title, pair.Value.Title, StringComparison.Ordinal);
                        titleSince = sameTitle ? previous.TitleSinceUtc ?? nowUtc : nowUtc;
                    }

                    tracked[pair.Key] = new TrackedChannel(pair.Value, liveSince, titleSince);
                }

                _expiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, config.StatusCacheSeconds));

                if (_lastError is not null)
                {
                    _logger.LogInformation("Twitch Live TV: channel lookup works again after {Error}", _lastError);
                    _lastError = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UpstreamRateLimitedException ex)
            {
                // Twitch named the wait, so that is the backoff: asking again sooner is how a limit becomes a
                // block, and one guide refresh asks this host several times.
                _lastError = ex.Message;
                _expiresUtc = DateTime.UtcNow + ex.Delay;
                _logger.LogWarning(
                    "Twitch Live TV: Twitch rate limited this server ({Error}); not asking again for {Seconds} seconds, keeping {Cached} known states meanwhile",
                    ex.Message,
                    (int)ex.Delay.TotalSeconds,
                    tracked.Count);
            }
            catch (Exception ex)
            {
                // Not fatal: the previous answers stay in place, so channels keep their group and guide
                // entry instead of all flipping to offline because a request failed.
                _lastError = ex.Message;
                _expiresUtc = DateTime.UtcNow + RetryAfterFailure;
                _logger.LogWarning(
                    "Twitch Live TV: could not refresh channel states ({Error}); keeping {Cached} known states and retrying in {Minutes} minutes",
                    ex.Message,
                    tracked.Count,
                    RetryAfterFailure.TotalMinutes);
            }

            // A schedule is only worth keeping while its channel is.
            foreach (var stale in _schedules.Keys.Where(k => !tracked.ContainsKey(k)).ToList())
            {
                _schedules.TryRemove(stale, out _);
            }

            Volatile.Write(ref _byLogin, tracked);
            Volatile.Write(ref _logins, logins);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets the logins of the accounts the configured token follows, when that was asked for.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetFollowedLoginsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.ImportFollowedChannels || string.IsNullOrWhiteSpace(config.AuthToken))
        {
            return Array.Empty<string>();
        }

        return await _helix.GetFollowedLoginsAsync(
            config.AuthToken,
            config.ClientId,
            config.MaxFollowedChannels,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the published broadcast slots of one channel, from its "Schedule" tab.
    /// </summary>
    /// <remarks>
    /// Cached per channel for <see cref="ScheduleCacheLifetime"/>, which is what the feed's own
    /// X-PUBLISHED-TTL asks for. A guide refresh walks every channel, so without this a channel list of
    /// fifty would mean fifty HTTP requests per refresh for data that moves weekly. Channels whose
    /// account Twitch never resolved (no numeric id) simply have no schedule.
    /// </remarks>
    /// <param name="login">The normalised login.</param>
    /// <param name="userId">Twitch's numeric account id, from the status answer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<TwitchScheduledItem>> GetScheduleAsync(string login, string? userId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!config.UseTwitchSchedule || string.IsNullOrWhiteSpace(userId))
        {
            return Array.Empty<TwitchScheduledItem>();
        }

        if (_schedules.TryGetValue(login, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
        {
            return cached.Items;
        }

        try
        {
            var items = await _helix.GetScheduleAsync(userId, config.ScheduleAheadDays, cancellationToken).ConfigureAwait(false);
            _schedules[login] = new CachedSchedule(items, DateTime.UtcNow + ScheduleCacheLifetime);
            PruneSchedules();
            return items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A channel whose schedule cannot be read keeps its generated guide entry instead of going
            // blank, and is not asked again until the cache would have expired anyway.
            _logger.LogWarning(ex, "Twitch Live TV: could not read the schedule of '{Login}'; using the generated guide entry", login);
            _schedules[login] = new CachedSchedule(Array.Empty<TwitchScheduledItem>(), DateTime.UtcNow + TimeSpan.FromMinutes(30));
            PruneSchedules();
            return Array.Empty<TwitchScheduledItem>();
        }
    }

    /// <summary>
    /// Drops schedules whose channel is gone or whose answer has expired, once the cache outgrew its purpose.
    /// </summary>
    /// <remarks>
    /// A guide refresh walks every channel, so this dictionary is written from a loop rather than one at a
    /// time - and channels that stop being followed leave their schedule behind. Six hours of reuse is what
    /// keeps it small in practice; the cap is what keeps it small when an operator rotates through follows.
    /// </remarks>
    private void PruneSchedules()
    {
        if (_schedules.Count <= MaxCachedSchedules)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var stale in _schedules.Where(p => p.Value.ExpiresUtc <= nowUtc).Select(p => p.Key).ToList())
        {
            _schedules.TryRemove(stale, out _);
        }
    }

    /// <summary>State for one login between two refreshes.</summary>
    private sealed record TrackedChannel(TwitchStreamStatus Status, DateTime? LiveSinceUtc, DateTime? TitleSinceUtc)
    {
        public TwitchChannel ToChannel(bool fromFollows)
            => new(Status.Login, Status.DisplayName, Status, LiveSinceUtc, TitleSinceUtc) { FromFollows = fromFollows };
    }

    /// <summary>A fetched schedule and the moment it is fetched again.</summary>
    private sealed record CachedSchedule(IReadOnlyList<TwitchScheduledItem> Items, DateTime ExpiresUtc);

    /// <summary>A playback token and the moment it stops being reused.</summary>
    private sealed record CachedToken(TwitchPlaybackToken Token, DateTime ExpiresUtc);
}
