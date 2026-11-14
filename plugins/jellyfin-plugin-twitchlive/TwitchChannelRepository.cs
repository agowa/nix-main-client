using System;
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
    DateTime? TitleSinceUtc);

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

    private readonly TwitchApi _api;
    private readonly ILogger<TwitchChannelRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Last known state per login, kept across failures so channels do not vanish.</summary>
    private Dictionary<string, TrackedChannel> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Order of the configured logins at the time of the last refresh.</summary>
    private List<string> _logins = [];

    /// <summary>Playback tokens per login, kept just long enough to serve one tune-in.</summary>
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _expiresUtc;
    private string? _lastError;
    private readonly HashSet<string> _warnedAboutLogin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="TwitchChannelRepository"/> class.</summary>
    public TwitchChannelRepository(TwitchApi api, ILogger<TwitchChannelRepository> logger)
    {
        _api = api;
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
        var logins = ReadConfiguredLogins(config);

        await EnsureStatusesAsync(forceRefresh, config, logins, cancellationToken).ConfigureAwait(false);

        var result = new List<TwitchChannel>(logins.Count);

        foreach (var login in logins)
        {
            if (_byLogin.TryGetValue(login, out var tracked))
            {
                result.Add(tracked.ToChannel());
            }
            else
            {
                // Never answered about - either nothing was fetched yet, or this login is not worth
                // asking Twitch about at all (see EnsureStatusesAsync). Either way it is not live.
                result.Add(new TwitchChannel(login, login, new TwitchStreamStatus(login, login, false, false, null, null, null, null), null, null));
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

        // The first pass tries the id as it arrived, so the common case costs one comparison.
        var stripped = false;

        while (!string.IsNullOrEmpty(id))
        {
            var match = channels.FirstOrDefault(c => string.Equals(c.Login, id, StringComparison.OrdinalIgnoreCase));
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
        }

        return TwitchApi.BuildStreamUrl(login, cached.Token);
    }

    /// <summary>The configured logins, normalised and de-duplicated, in configuration order.</summary>
    private static List<string> ReadConfiguredLogins(PluginConfiguration config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logins = new List<string>();

        foreach (var entry in config.Channels ?? [])
        {
            var login = TwitchChannelCatalog.Normalize(entry);
            if (login is not null && seen.Add(login))
            {
                logins.Add(login);
            }
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
        var configurationChanged = !_logins.SequenceEqual(logins, StringComparer.OrdinalIgnoreCase);

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
            var tracked = new Dictionary<string, TrackedChannel>(StringComparer.OrdinalIgnoreCase);
            foreach (var login in logins)
            {
                if (_byLogin.TryGetValue(login, out var existing))
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
                else if (_warnedAboutLogin.Add(login))
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

            _byLogin = tracked;
            _logins = logins;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>State for one login between two refreshes.</summary>
    private sealed record TrackedChannel(TwitchStreamStatus Status, DateTime? LiveSinceUtc, DateTime? TitleSinceUtc)
    {
        public TwitchChannel ToChannel()
            => new(Status.Login, Status.DisplayName, Status, LiveSinceUtc, TitleSinceUtc);
    }

    /// <summary>A playback token and the moment it stops being reused.</summary>
    private sealed record CachedToken(TwitchPlaybackToken Token, DateTime ExpiresUtc);
}
