using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TwitchLive.Configuration;

/// <summary>
/// Settings for the Twitch Live TV plugin.
/// </summary>
/// <remarks>
/// Everything here is read again on every request, so a change takes effect without restarting the
/// server: edit it in the plugin's configuration page (Dashboard -> Plugins -> Twitch Live TV ->
/// Settings), over the REST API (<c>GET</c>/<c>POST /Plugins/{acfb8b6e-f2ce-4615-800f-77c40ffd213d}/Configuration</c>
/// - note that Jellyfin only accepts POST there, not PUT) or from Nix with
/// <c>services.jellyfinTwitch.manageConfig</c>. Only the channel numbers and groups in Jellyfin's
/// database follow slower - they are rewritten when the guide is refreshed.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class.</summary>
    public PluginConfiguration()
    {
        // One channel that streams around the clock, so an untouched installation shows something.
        Channels = ["lofigirl"];
        IncludeOfflineChannels = true;

        // The client id of twitchweb itself. Twitch's GraphQL endpoint only answers requests that
        // present a client id, and this is the public one that the embedded player ships with - it is
        // what every third-party tool uses and needs no registration. Replace it with your own
        // (https://dev.twitch.tv/console) if you ever want to stop sharing it.
        ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

        StatusCacheSeconds = 60;
        TokenCacheSeconds = 120;
        GuideAheadMinutes = 240;
        AllowSourceQuality = true;

        // Schedule and artwork are public and need no login, so they are on from the start.
        UseTwitchSchedule = true;
        ScheduleAheadDays = 7;
        ShowChannelLogos = true;

        // Optional: an OAuth token of your own account. Empty means "anonymous", which is enough for
        // everything except importing follows (and ad-free playback with a subscription or Turbo).
        AuthToken = string.Empty;
        UseTokenForPlayback = true;
        ImportFollowedChannels = false;
        MaxFollowedChannels = 200;
        FollowCacheMinutes = 360;

        // Verified against the CDN: usher.ttvnw.net, playlist.ttvnw.net and the segment hosts all
        // answer 200 for a bare request, because the playback token is already bound to the client IP.
        // So no User-Agent trickery is needed the way the ZDF CDNs want it; this stays empty unless a
        // CDN starts disagreeing.
        UserAgent = string.Empty;
    }

    /// <summary>
    /// Gets or sets the Twitch logins to offer as channels, in guide order.
    /// </summary>
    /// <remarks>
    /// This is the list meant to be edited at runtime. Entries are compared case-insensitively and
    /// surrounding whitespace is ignored; a login that does not exist on Twitch simply never shows up
    /// as live, which is how a typo reveals itself.
    /// </remarks>
    public string[] Channels { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether channels that are not streaming are still listed.
    /// </summary>
    /// <remarks>
    /// On, they are listed in their own group and the guide says that they are offline - which also
    /// keeps a channel's number stable while its streamer sleeps. Off, only live channels are offered
    /// at all, and the channel list grows and shrinks with the guide refreshes.
    /// </remarks>
    public bool IncludeOfflineChannels { get; set; }

    /// <summary>Gets or sets the Twitch client id sent to the GraphQL endpoint.</summary>
    public string ClientId { get; set; }

    /// <summary>
    /// Gets or sets an OAuth access token of a Twitch account, without the word "Bearer".
    /// </summary>
    /// <remarks>
    /// Everything this plugin does by default is public and needs no token. A token is what unlocks
    /// three extras: the ad-free rendition that a channel subscription or Twitch Turbo entitles that
    /// account to (see <see cref="UseTokenForPlayback"/>), the broadcaster's own schedule through the
    /// documented Helix endpoint rather than the public iCalendar feed, and
    /// <see cref="ImportFollowedChannels"/> - which additionally needs the scope
    /// <c>user:read:follows</c>.
    /// <para>
    /// Get one at https://twitchtokengenerator.com or from your own app registration
    /// (https://dev.twitch.tv/console, implicit grant flow); it is stored in this plugin's XML file
    /// under the data directory and is sent only to api.twitch.tv, id.twitch.tv, gql.twitch.tv and
    /// usher.ttvnw.net. Treat that file like a password: it can be revoked at
    /// https://www.twitch.tv/settings/connections.
    /// </para>
    /// </remarks>
    public string AuthToken { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account token is also sent with playback requests.
    /// </summary>
    /// <remarks>
    /// On, Twitch sees the subscribing/Turbo account when it hands out the playlist and can leave the
    /// ad markers out - which is the whole point of paying for either. Off, only the guide-related
    /// calls use the token and playback stays anonymous (ad-supported). Harmless when no token is set.
    /// </remarks>
    public bool UseTokenForPlayback { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the accounts this login follows are added as channels.
    /// </summary>
    /// <remarks>
    /// Needs <see cref="AuthToken"/> with the scope <c>user:read:follows</c>. Follows are merged with
    /// <see cref="Channels"/> rather than replacing it, so hand-picked logins survive an import and
    /// unfollowing someone only removes them at the next refresh.
    /// </remarks>
    public bool ImportFollowedChannels { get; set; }

    /// <summary>
    /// Gets or sets how many followed accounts to add at most, newest follow first.
    /// </summary>
    /// <remarks>
    /// Every channel costs one GraphQL alias per status request and one schedule request per guide
    /// refresh, so a few hundred follows is a lot of guide work. Twitch pages at 100 per request.
    /// </remarks>
    public int MaxFollowedChannels { get; set; }

    /// <summary>Gets or sets how long the follow list is reused before it is fetched again.</summary>
    public int FollowCacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each channel's published broadcast schedule is used.
    /// </summary>
    /// <remarks>
    /// This is the data behind the "Schedule" tab on a Twitch channel. It turns the guide from "what
    /// is on right now, guessed" into actual appointments for channels that publish one; channels
    /// without a schedule keep the generated entries. Fetched from Twitch's own endpoint and needs no
    /// login unless <see cref="AuthToken"/> is set, in which case the richer JSON API is used.
    /// </remarks>
    public bool UseTwitchSchedule { get; set; }

    /// <summary>Gets or sets how far ahead the published schedule is fetched, in days.</summary>
    /// <remarks>
    /// Twitch publishes about a week of recurring slots regardless of what is asked for, so values
    /// above 7 mostly cost requests. Jellyfin's own guide window (Live TV settings) still decides how
    /// much of it is stored.
    /// </remarks>
    public int ScheduleAheadDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether channel logos are offered to the library.
    /// </summary>
    /// <remarks>
    /// The logo is Twitch's own profile picture for the account, so this costs no extra request - it
    /// arrives with the live/offline answer that has to be fetched anyway.
    /// </remarks>
    public bool ShowChannelLogos { get; set; }

    /// <summary>
    /// Gets or sets how long a fetched live/offline answer is reused, in seconds.
    /// </summary>
    /// <remarks>
    /// One request answers the state of every configured channel, so this can be low without
    /// hammering Twitch; it only decides how quickly going on- and offline becomes visible.
    /// </remarks>
    public int StatusCacheSeconds { get; set; }

    /// <summary>
    /// Gets or sets how long a fetched playback token is reused, in seconds.
    /// </summary>
    /// <remarks>
    /// Tuning to a channel asks for a media source and then for the stream itself, so without a short
    /// cache every tune-in costs two tokens. Tokens are only needed to fetch the master playlist -
    /// Jellyfin keeps playing through the signed segment URLs inside it - so this does not limit how
    /// long a stream can run.
    /// </remarks>
    public int TokenCacheSeconds { get; set; }

    /// <summary>
    /// Gets or sets how far ahead of now a running stream is extended in the guide, in minutes.
    /// </summary>
    /// <remarks>
    /// A live stream has no published end, so it reaches this far and is replaced when its title
    /// changes. Where the channel's own schedule says what comes next (see
    /// <see cref="UseTwitchSchedule"/>), that ends the live entry early instead and this value only
    /// covers channels without a schedule. Long values make the guide look full but are guesses.
    /// </remarks>
    public int GuideAheadMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Twitch's untranscoded source rendition is offered.
    /// </summary>
    /// <remarks>
    /// Maps to usher's <c>allow_source</c>. On, the playlist can contain the broadcaster's original
    /// stream (often better than the 1080p60 transcode); off, only the regular renditions.
    /// </remarks>
    public bool AllowSourceQuality { get; set; }

    /// <summary>Gets or sets an optional User-Agent for the Twitch CDNs. Empty sends none.</summary>
    public string UserAgent { get; set; }
}
