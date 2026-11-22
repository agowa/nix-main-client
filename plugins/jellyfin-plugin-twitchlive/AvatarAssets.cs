using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// This plugin's own copy of every followed streamer's picture.
/// </summary>
/// <remarks>
/// A Twitch account's picture is an address into someone else's CDN, and it moves: uploading a new avatar hands
/// out a new URL and stops serving the old one. That would be harmless if the server re-read a channel's artwork,
/// but it does not - <c>GuideManager.UpdateChannel()</c> only touches the image of a channel that has none - so
/// handing out an address is a permanent commitment. Once the streamer changes their picture the stored address
/// answers 404, and because <c>ImageController.GetImage()</c> converts a remote image to local with
/// <c>removeOnFailure: true</c>, the first person to open the guide gets an error response for that tile instead
/// of the letter tile Jellyfin would have drawn with no image at all.
/// <para>
/// So this class takes the same route the ZDF plugin takes for the logos no logo host carries (see its
/// <c>ChannelLogoAssets</c>): keep the file ourselves and hand the server a path, which leaves nothing to fetch
/// when somebody looks at the tile. The difference is that these pictures are not shipped inside the assembly -
/// there is one per streamer and they change on their own schedule - so they are downloaded once into
/// <c>&lt;dataDir&gt;/TwitchLive/avatars/&lt;login&gt;.png</c>.
/// </para>
/// <para>
/// The file name is derived from the login alone, never from the address it came from. That is what makes an
/// avatar change cost nothing: every channel already points at this path, so replacing the bytes updates the tile
/// and no database row has to be touched. It also means a download that fails leaves the previous picture in
/// place rather than a hole - keeping the old avatar is always better than showing nothing.
/// </para>
/// <para>
/// What that costs: which address belongs to which login is only remembered for the life of the process, so the
/// first channel list after a server start fetches one picture per followed channel (tens of kilobytes each) and
/// later lists fetch none unless an avatar actually changed. Recording which address each file came from would save
/// those requests, at the price of a second copy of the truth that can disagree with the folder; the downloads are
/// capped and never hold up a channel list for longer than <see cref="BatchBudget"/>, so what a server start costs
/// is one request per followed channel. Until a picture lands the channel is offered with Twitch's own address,
/// exactly as before, and the next pass moves it onto the file (see <c>ChannelArtwork</c>).
/// </para>
/// </remarks>
public sealed class AvatarAssets
{
    /// <summary>How long a channel list waits for outstanding downloads before going out without them.</summary>
    private static readonly TimeSpan BatchBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long one picture may take before it is counted as failed.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Pictures fetched at once. These are small files on the CDN that already serves the player.</summary>
    private const int ParallelDownloads = 4;

    /// <summary>Largest file accepted as an avatar, so a wrong address cannot be asked to buffer the sea.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Folder under the server's data directory these pictures live in.</summary>
    private static readonly string[] FolderParts = ["TwitchLive", "avatars"];

    /// <summary>
    /// How many logins the remembered source addresses cover. Followed channels are capped at 2000 and a
    /// hand-written list is rarely longer, so this only ever trips on an operator who churns through follows;
    /// it exists because the dictionary would otherwise outlive every channel it was written for.
    /// </summary>
    private const int MaxRememberedLogins = 4096;

    /// <summary>How many refused addresses are remembered, so one broken host cannot grow a set forever.</summary>
    private const int MaxReportedAddresses = 512;

    /// <summary>Extensions accepted from an address; anything else is stored as PNG, which is what Twitch sends.</summary>
    private static readonly string[] KnownExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AvatarAssets> _logger;
    private readonly object _gate = new();

    /// <summary>Address each picture was taken from this run, so an unchanged avatar is fetched once.</summary>
    private readonly Dictionary<string, string> _fetchedFrom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Addresses already complained about, one line per address rather than per channel list.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Login to file name on disk; rebuilt when the folder holds something other than expected.</summary>
    private Dictionary<string, string>? _onDisk;

    /// <summary>Initializes a new instance of the <see cref="AvatarAssets"/> class.</summary>
    /// <param name="applicationPaths">The server's well known paths.</param>
    /// <param name="httpClientFactory">The server's HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AvatarAssets(IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory, ILogger<AvatarAssets> logger)
    {
        Folder = Path.Combine(applicationPaths.DataPath, Path.Combine(FolderParts));
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the directory this plugin keeps streamer pictures in.</summary>
    public string Folder { get; }

    /// <summary>
    /// Gets the picture this plugin holds for a streamer, if it holds one.
    /// </summary>
    /// <remarks>
    /// Reading the folder rather than remembering what was written is deliberate: files here outlive updates but
    /// not a cleaned data directory, and an operator who clears that folder while the server runs should get new
    /// pictures rather than paths to nothing. A login with more than one file (an address that changed extension)
    /// resolves to the newest, which is the one <see cref="FetchAsync"/> last wrote.
    /// </remarks>
    /// <param name="login">The streamer's login.</param>
    /// <returns>The absolute path, or <c>null</c> while nothing has been stored for that login.</returns>
    public string? PathFor(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        var onDisk = EnsureScanned();

        if (!onDisk.TryGetValue(login, out var fileName))
        {
            return null;
        }

        var path = Path.Combine(Folder, fileName);

        if (File.Exists(path))
        {
            return path;
        }

        // Gone underneath us: forget the whole listing so the next call reads the folder again, and let this one
        // answer "nothing" - which offers the channel with Twitch's address until a download replaces it.
        lock (_gate)
        {
            _onDisk = null;
        }

        return null;
    }

    /// <summary>
    /// Says whether an image reference points into this plugin's own picture folder.
    /// </summary>
    /// <remarks>
    /// Used by <c>ChannelArtwork</c> to tell a picture this plugin handed out - which it may therefore replace -
    /// from artwork somebody else put on the channel, which it may not.
    /// </remarks>
    /// <param name="path">The stored image path.</param>
    /// <returns><c>true</c> when that file sits in <see cref="Folder"/>.</returns>
    public bool IsOwnPicture(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !IsRemoteAddress(path!)
           && string.Equals(
               Path.GetDirectoryName(path!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
               Folder,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads every picture this plugin does not have yet, or does not have in the shape Twitch now offers.
    /// </summary>
    /// <remarks>
    /// Called from the channel list, so it cannot be allowed to decide how long that takes: everything outstanding
    /// when <see cref="BatchBudget"/> has passed keeps downloading in the background and records its verdict for
    /// the next list. That is also why a slow CDN costs a channel nothing but its tile staying remote for a while.
    /// </remarks>
    /// <param name="avatarUrlsByLogin">The address Twitch reports per login, or <c>null</c> where it reports none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the batch's share of the channel list's time has been used.</returns>
    public async Task FetchAsync(IReadOnlyDictionary<string, string?> avatarUrlsByLogin, CancellationToken cancellationToken)
    {
        if (avatarUrlsByLogin.Count == 0)
        {
            return;
        }

        var missing = new List<KeyValuePair<string, string>>();

        foreach (var pair in avatarUrlsByLogin)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var url = pair.Value.Trim();

            lock (_gate)
            {
                var known = _fetchedFrom.TryGetValue(pair.Key, out var from) && string.Equals(from, url, StringComparison.Ordinal);
                var onDisk = PathForCore(pair.Key);

                if (!known || onDisk is null)
                {
                    missing.Add(new KeyValuePair<string, string>(pair.Key, url));
                }
            }
        }

        // The batch is the only moment this class learns which logins still matter, so it is also where the
        // remembered addresses lose the ones that stopped being followed. Without this an account that follows
        // and unfollows over months keeps one entry per login it ever saw.
        PruneRemembered(avatarUrlsByLogin);

        if (missing.Count == 0)
        {
            return;
        }

        EnsureFolder();

        var slots = new SemaphoreSlim(ParallelDownloads, ParallelDownloads);
        var downloads = missing
            .Select(pair => DownloadAsync(pair.Key, pair.Value, slots, cancellationToken))
            .ToList();

        // Nothing else will look at these tasks, so make sure a late failure cannot go unobserved; each of them
        // already handles its own exceptions, this is only the belt.
        foreach (var download in downloads)
        {
            _ = download.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var all = Task.WhenAll(downloads);
        var winner = await Task.WhenAny(all, Task.Delay(BatchBudget, CancellationToken.None)).ConfigureAwait(false);

        if (winner != all)
        {
            _logger.LogDebug(
                "Twitch Live TV: only some of the {Total} streamer picture(s) arrived within {Seconds}s; the rest keep downloading",
                missing.Count,
                BatchBudget.TotalSeconds);
        }
    }

    /// <summary>
    /// Says whether an address is still served, for deciding that something already stored is genuinely gone.
    /// </summary>
    /// <remarks>
    /// The same bias the ZDF plugin's logo check has, and for the same reason: only a definite refusal counts as
    /// missing. A CDN that is merely unreachable right now must not talk this plugin out of replacing a picture
    /// that works - see <c>ZdfChannelRepository.VerifyLogosAsync</c> for why that direction is the expensive one
    /// to be wrong in.
    /// </remarks>
    /// <param name="url">The address to ask.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> only when the address answered 403, 404 or 410.</returns>
    public async Task<bool> IsStillServedAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsFetchableAddress(url))
        {
            return true;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            return (int)response.StatusCode is not (403 or 404 or 410);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Twitch Live TV: could not ask whether {Url} is still served; treating it as served", url);
            return true;
        }
    }

    /// <summary>Says whether a path still has to be fetched before it can be shown.</summary>
    internal static bool IsRemoteAddress(string path)
        => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file name an address would be stored under for this login, or <c>null</c> when the login may not
    /// name a file at all.
    /// </summary>
    /// <remarks>
    /// The repository only ever asks about logins that pass <see cref="TwitchApi.IsPlausibleLogin"/>, so this
    /// check is not what keeps the folder sane today - it is what keeps it sane if a later change feeds this
    /// class something else. The file name ends up inside a path the server reads back as channel artwork, and
    /// a login containing a separator would put bytes outside <see cref="Folder"/>: worth refusing twice.
    /// </remarks>
    internal static string? FileNameFor(string login, string url)
    {
        if (!TwitchApi.IsPlausibleLogin(login))
        {
            return null;
        }
        var extension = ".png";

        try
        {
            var lastSegment = new Uri(url).Segments.LastOrDefault();
            var dot = lastSegment?.LastIndexOf('.') ?? -1;

            if (lastSegment is not null && dot > 0)
            {
                var candidate = lastSegment[dot..].ToLowerInvariant();
                if (KnownExtensions.Contains(candidate, StringComparer.Ordinal))
                {
                    extension = candidate == ".jpeg" ? ".jpg" : candidate;
                }
            }
        }
        catch (Exception ex)
        {
            // Not a URL worth arguing about - Twitch's own answers always end in .png anyway.
            _ = ex;
        }

        return login + extension;
    }

    /// <summary>
    /// Says whether an address may be fetched at all.
    /// </summary>
    /// <remarks>
    /// Twitch supplies these addresses rather than the operator, so they are treated as input: a scheme other
    /// than http(s) - or no host in front of it - is not something to hand to the HTTP client, however it got
    /// into an answer. This also refuses the relative and file-style values that would otherwise be resolved
    /// against the process's own working directory.
    /// </remarks>
    /// <param name="url">The address to judge.</param>
    /// <returns><c>true</c> when this is an absolute http(s) address with a host.</returns>
    internal static bool IsFetchableAddress(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(uri.Host);

    /// <summary>A temporary name for one write, unique per attempt rather than shared by every writer.</summary>
    private static string TemporaryName(string target)
        => target + "." + Path.GetRandomFileName() + ".tmp";

    /// <summary>Removes a leftover temporary file, which is never worth an exception over.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A file we could not remove is a few kilobytes in a data directory, nothing more.
        }
    }

    /// <summary>
    /// Forgets remembered addresses and complaints about logins this batch no longer covers, and keeps the set
    /// of complained-about addresses bounded by dropping it once it stops being useful as a memory.
    /// </summary>
    private void PruneRemembered(IReadOnlyDictionary<string, string?> avatarUrlsByLogin)
    {
        lock (_gate)
        {
            if (_fetchedFrom.Count > MaxRememberedLogins)
            {
                var current = new HashSet<string>(avatarUrlsByLogin.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var login in _fetchedFrom.Keys.Where(k => !current.Contains(k)).ToList())
                {
                    _fetchedFrom.Remove(login);
                }
            }

            // One line per refused address is the point of this set; past a few hundred it has stopped being a
            // memory and become a ledger, so start over rather than grow without end.
            if (_reported.Count > MaxReportedAddresses)
            {
                _reported.Clear();
            }
        }
    }

    private Dictionary<string, string> EnsureScanned()
    {
        lock (_gate)
        {
            if (_onDisk is not null)
            {
                return _onDisk;
            }
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(Folder))
            {
                // Newest wins where a login has more than one file: see PathFor.
                foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                             .OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    var login = Path.GetFileNameWithoutExtension(file.Name);

                    if (!string.IsNullOrWhiteSpace(login) && !found.ContainsKey(login))
                    {
                        found[login] = file.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitch Live TV: could not read the streamer picture folder {Folder}", Folder);
        }

        lock (_gate)
        {
            _onDisk ??= found;
            return _onDisk;
        }
    }

    private string? PathForCore(string login)
        => EnsureScanned().TryGetValue(login, out var fileName) ? Path.Combine(Folder, fileName) : null;

    private void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitch Live TV: could not create the streamer picture folder {Folder}; channels keep Twitch's own addresses", Folder);
        }
    }

    private async Task DownloadAsync(string login, string url, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        if (!IsFetchableAddress(url))
        {
            Report(login, url, "is not an http(s) address and was not fetched");
            return;
        }

        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Report(login, url, "answered " + (int)response.StatusCode);
                return;
            }

            // Like the server's own image saving: an address that serves something which is not a picture does
            // not become channel artwork just because it downloaded fine.
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                Report(login, url, "is not an image but " + (string.IsNullOrWhiteSpace(contentType) ? "an unknown type" : contentType));
                return;
            }

            var declared = response.Content.Headers.ContentLength ?? -1;
            if (declared > MaxBytes)
            {
                Report(login, url, $"is {declared} bytes, which is not a profile picture");
                return;
            }

            var fileName = FileNameFor(login, url);
            if (fileName is null)
            {
                // Should not happen - the repository only asks about plausible logins - but this is the last
                // place that could still say no before something is written under a name we did not choose.
                Report(login, url, "belongs to a login that may not name a file; nothing was stored");
                return;
            }

            var target = Path.Combine(Folder, fileName);
            var temporary = TemporaryName(target);
            var stored = false;

            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;

                    while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        written += read;

                        if (written > MaxBytes)
                        {
                            Report(login, url, "is larger than the accepted maximum");
                            return;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }

                // Through a temporary name, so an interrupted download cannot leave half a picture where the
                // server is about to look for it - and so a channel already pointing at this path never sees it
                // missing. The name is unique per attempt (see TemporaryName) because two passes over the same
                // channel would otherwise share one temporary file and race over who gets to move it away.
                File.Move(temporary, target, true);
                stored = true;
            }
            finally
            {
                if (!stored)
                {
                    TryDelete(temporary);
                }
            }

            lock (_gate)
            {
                _fetchedFrom[login] = url;
                _onDisk = null;
            }

            _logger.LogDebug("Twitch Live TV: stored the picture of '{Login}' in {File}", login, target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The previous picture - if there was one - is still in place and still offered. Nothing to fix here.
            Report(login, url, "could not be fetched: " + ex.Message);
        }
        finally
        {
            slots.Release();
        }
    }

    private void Report(string login, string url, string what)
    {
        lock (_gate)
        {
            if (!_reported.Add(url))
            {
                return;
            }
        }

        _logger.LogInformation(
            "Twitch Live TV: the picture of '{Login}' {What}; it keeps whatever this plugin has for that channel",
            login,
            what);
    }
}
