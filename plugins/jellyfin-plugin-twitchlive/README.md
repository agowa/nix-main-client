# Jellyfin Twitch Live TV

A Jellyfin Live TV **tuner plugin** for [Twitch](https://www.twitch.tv) channels. Installed declaratively
by [`modules/jellyfin-twitch.nix`](../../modules/jellyfin-twitch.nix); this file documents the plugin.

Two things set it apart from the ZDF plugin next to it, and both come from Twitch being different: the
channel list is **edited while the server runs**, and because there is no XMLTV for "who is live", the
plugin **brings its own programme guide** - which is also what tells you whether a channel is streaming.

## Why a tuner host and not an M3U playlist

Jellyfin can already read an IPTV playlist, so a plugin needs a reason to exist. Here there are three:

* **A stream address cannot be written into a file.** Playing a Twitch channel needs a playback token
  that is signed for the requesting IP address and expires within minutes. `TwitchApi` fetches one at
  tune-in time and builds the usher URL from it, so nothing has to be refreshed on a schedule - and no
  threadfin/xTeVe proxy is involved.
* **Live state is worth modelling.** A tuner host knows which channels are actually streaming and can say
  so in the channel list, the guide and at the moment someone presses Play (see below).
* **It is a real tuner type.** `ServiceRegistrator` puts an `ITunerHost` into the server's own DI
  collection - possible because `TunerHostManager` is constructed from `IEnumerable<ITunerHost>` and
  `ApplicationHost.Init()` calls `PluginManager.RegisterServices()` into that same `IServiceCollection`
  before it is built. In the web UI the result is a tuner source called "Twitch Live TV" next to
  HDHomeRun and IPTV, auto-discoverable via *Detect*.

Playback does not proxy anything: `GetChannelStreamMediaSources()` returns a media source with
`Protocol = Http` and the playlist URL in `Path`, and because `TwitchLiveStream` deliberately does **not**
implement `IDirectStreamProvider`, Jellyfin hands that URL straight to jellyfin-ffmpeg - the same route
the built-in M3U tuner takes for HLS. Letting ffmpeg read the adaptive playlist is also what picks and
switches renditions; the plugin never chooses a quality.

## The channel list is a runtime setting

`TwitchChannelRepository` reads `PluginConfiguration.Channels` again on every request, so editing it needs
neither a restart nor a rebuild:

* **Web UI** - Dashboard → Plugins → *Twitch Live TV* → Settings. One login per line (commas are accepted
  too), plus the cache sizes and the offline-channel switch.
* **REST API** - `GET`/`PUT /Plugins/acfb8b6e-f2ce-4615-800f-77c40ffd213d/Configuration`. Send the whole
  object back (the server replaces it, so a partial body would blank the fields you left out).
* **Nix** - `services.jellyfinTwitch.channels`, which *seeds* the file on a fresh installation and leaves
  it alone afterwards; set `manageConfig = true` only if this configuration should win every switch.

After an edit, channel numbers and live/offline groups in Jellyfin's database follow on the next guide
refresh - run the *Refresh Guide* task to see it immediately. The stream itself reacts at once: tuning is
resolved against the current list, not the last refresh.

The configuration page needs two embedded resources. Jellyfin 10.11 no longer executes `<script>` inside a
plugin page (`data-require` and its script loader are gone from jellyfin-web); the view container reads a
`data-controller` attribute off the page element, imports the module registered under that name from
`/web/ConfigurationPage?name=<controller>`, and constructs it with the page. Hence `configPage.html` plus
`configController.js`, registered as two pages in `Plugin.GetPages()` - and the HTML one first, because
with several pages the web client uses the first for its "Settings" button.

## Live and offline are kept apart

Three places say it, so you cannot miss it:

| | live | offline |
| --- | --- | --- |
| tag on the channel | `live` | `offline` |
| guide entry | the stream title, category, viewer count and "live since" | *Offline*, or *No such Twitch account* for a login that does not exist |
| pressing Play | usher playlist handed to ffmpeg | no media source at all, with one fresh check first |

The tag is what actually reaches the library: `GuideManager` copies `ChannelInfo.Tags` onto the channel
item (and re-indexes it when they change), but never reads `ChannelGroup`, so `Twitch - Live` /
`Twitch - Offline` are only visible while Jellyfin lists the tuner's channels during guide setup. Tags
survive into the library, show up in the channel list and can be filtered on, which is why both carry the
state rather than relying on the group alone.

Offline channels are **listed** rather than hidden by default (`IncludeOfflineChannels`): a channel that
vanished every time its streamer slept would also lose its guide position, and Jellyfin only notices
removals on the second refresh after it. Turn the option off to offer live channels only.

Refusing an offline tune-in is not cosmetic: Twitch still issues a playback token for a channel that is
not streaming, but usher then answers `404 {"error":"Can not find channel"}`, so the viewer would get an
opaque encoder failure for what is really "nobody is live". Because a cached answer can be up to
`StatusCacheSeconds` old, an offline tune-in re-asks Twitch once before giving up - one request for every
channel at once, only in that case.

## The guide comes from the plugin too

`TwitchGuideProvider` implements `IListingsProvider`: a running stream becomes one programme carrying its
title and category, starting when this plugin first saw that title (Twitch's public player API reports no
start time, so "live since" can only come from watching) and reaching `GuideAheadMinutes` ahead. Programme
ids change with the title, which is how Jellyfin learns a stream moved on. There is no schedule to show -
everything past *now* is a guess, which is why the horizon is short and configurable.

## Two one-time steps in the web UI

Installing the plugin is not quite "Live TV on", deliberately: Jellyfin stores tuner sources and guide
providers in `<configDir>/livetv.xml` together with everything else configured by hand, so writing that
file from Nix would delete any other tuner or EPG. Both steps survive a rebuild, and unlike the ZDF plugin
**neither needs a URL**:

1. **Guide + Channels → Live TV → add tuner source → "Twitch Live TV"** (or press *Detect*). The URL field
   is ignored; channels come from the plugin configuration. Required at all because Jellyfin only offers
   Live TV once ≥1 tuner source exists.
2. **Same page → add programme guide → "Twitch Live TV"**. No login, no path. Channels match automatically:
   both sides key on the login (`ChannelInfo.TunerChannelId`).

## What was verified against Twitch (November 2026)

* `POST https://gql.twitch.tv/gql` with the public web client id answers without any registration or user
  login. Live state of many channels in **one** request: an array of documents, each aliasing logins as
  `c_<login>`; aliases are capped at **15 per document**, so longer lists become several documents in the
  same POST. A live channel answers `stream.type`, an existing but offline one answers `user` with
  `stream: null`, and a login that does not exist answers `user: null` - which is how a typo is told apart
  from a streamer who is asleep.
* Playback chain works end to end from this machine, **no geo-fencing** (unlike the ZDF CDNs): token →
  master playlist (`200`, 8.6 kB) → media playlist (`200`) → an actual `1.5 MB` MPEG-TS segment.
* Through Jellyfin as well, not just with curl: tuning a channel yields one `Protocol = Http` media source,
  the server probes it (`hls`, h264 `1920x1080` + AAC stereo) and starts jellyfin-ffmpeg against the usher
  URL; a 45 second pull delivered `24 MB` of MPEG-TS (first byte `0x47`). An offline channel instead logs
  that there is nothing to tune to and offers no media source.
* Runtime editing works on a running server: `PUT /Plugins/<id>/Configuration` with a different channel
  list, then the *Refresh Guide* task, replaced the channels in the library - two removed, two added, no
  restart and no rebuild - and `IncludeOfflineChannels = false` left only the live ones.
* The CDNs want **no special headers**: usher answered `200` with no headers at all, with only a User-Agent,
  and with only Origin/Referer alike - the token already carries the client IP. So unlike ZDF nothing here
  pretends to be Chrome; `UserAgent` is empty unless someone needs it.
* A token outlives its usefulness but not your stream: it authorises only the master playlist, while the
  media playlists and segments inside are separately signed - so a stream keeps running after the token
  that started it has expired.

```sh
# Live state of two channels in one request (the shape TwitchApi builds):
curl -s https://gql.twitch.tv/gql -H 'Client-Id: kimne78kx3ncx6brgo4mv6wki5h1ko' \
     -H 'Content-Type: application/json' \
     -d '[{"operationName":"q0","query":"query q0($a: String!, $b: String!){ c_lofigirl: user(login: $a){displayName stream{type title}} c_esl_sc2: user(login: $b){displayName stream{type title}} }","variables":{"a":"lofigirl","b":"esl_sc2"}}]'
```

## Limits worth knowing

* **This is the player API, not an official one.** It uses the same endpoint and client id as the embedded
  twitch.tv player; Twitch documents `dev.twitch.tv` APIs with their own credentials instead. The
  configuration takes a `ClientId`, so switching to a registered one is a setting, not a code change. If
  Twitch changes this surface, this plugin is the thing that breaks - which is also why every answer is
  cached and every failure degrades to "keep what we knew" instead of turning all channels offline.
* **Streams that need an account stay unwatchable** - subscribers-only channels, and streams blocked in your
  region or by Twitch's own rules. The same ones are black in the browser.
* **No VODs, no clips.** Live only; past broadcasts are a different API and a different feature.
* **Recording is as good as the guess above.** Jellyfin records what the guide says will air, so a recording
  of a Twitch channel stops when `GuideAheadMinutes` runs out unless a refresh extends it.
* Channel logos are not set: `ChannelInfo.ImageUrl` would point at Twitch's avatar CDN and let the server
  fetch them, which is deliberately left for another day rather than added untested.
