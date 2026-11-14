using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TwitchLive.Configuration;

/// <summary>
/// Settings for the Twitch Live TV plugin.
/// </summary>
/// <remarks>
/// Everything here is read again on every request, so a change takes effect without restarting the
/// server: edit it in the plugin's configuration page (Dashboard -> Plugins -> Twitch Live TV ->
/// Settings), over the REST API (<c>GET</c>/<c>PUT /Plugins/{acfb8b6e-f2ce-4615-800f-77c40ffd213d}/Configuration</c>)
/// or from Nix with <c>services.jellyfinTwitch.manageConfig</c>. Only the channel numbers and groups in
/// Jellyfin's database follow slower - they are rewritten when the guide is refreshed.
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
    /// Gets or sets how far ahead of now the generated guide reaches, in minutes.
    /// </summary>
    /// <remarks>
    /// Twitch publishes no schedule, so a running stream is simply extended this far and replaced when
    /// its title changes. Long values make the guide look full but are guesses.
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
