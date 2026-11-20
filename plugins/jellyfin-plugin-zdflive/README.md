# Jellyfin ZDF Live TV

A Jellyfin Live TV **tuner plugin** for the live streams that ZDF publishes at
<https://www.zdf.de/live-tv>: **ZDF, ZDFneo, ZDFinfo, 3sat, ARTE, KiKA, phoenix**.

Installed declaratively by [`modules/jellyfin-zdf.nix`](../../modules/jellyfin-zdf.nix); see there for the
NixOS options. This file documents the plugin itself.

## Why a tuner host and not an M3U playlist

Jellyfin can already read an IPTV playlist, so a plugin needs a reason to exist. The reason here is that
the plugin resolves stream URLs **at runtime** instead of freezing them into a file:

* `ZdfChannelRepository` asks the MediathekView Zapp API (`api.zapp.mediathekview.de`, no key required)
  which master playlist each channel currently uses, caches it for `CacheMinutes` and falls back to the
  last known paths in `ZdfChannelCatalog.cs` if that API is down - with one warning in the log.
* Nothing in this repository pins an `*.akamaized.net` path that would silently rot.

It is a real tuner type, not a disguised playlist: `ServiceRegistrator` puts an `ITunerHost` into the
server's own DI collection, which works because `TunerHostManager` is constructed from
`IEnumerable<ITunerHost>` and `ApplicationHost.Init()` calls `PluginManager.RegisterServices()` into that
same `IServiceCollection` before it is built. In the web UI the result is a tuner source called
"ZDF Live TV" next to HDHomeRun and IPTV, auto-discoverable via "Detect".

Playback does not proxy anything: `GetChannelStreamMediaSources()` returns a media source with
`Protocol = Http` and the playlist URL in `Path`, and because `ZdfLiveStream` deliberately does **not**
implement `IDirectStreamProvider`, Jellyfin hands that URL straight to jellyfin-ffmpeg - the same route
the built-in M3U tuner takes for HLS. No threadfin/xTeVe, no DVB stick, no segment buffering in the plugin.

## Two one-time steps in the web UI

Installing the plugin is not quite "Live TV on", and that is deliberate: Jellyfin stores tuner sources and
guide providers in `<configDir>/livetv.xml` together with everything else configured by hand, so writing
that file from Nix would delete any other tuner or EPG. Both steps survive a rebuild:

1. **Guide + Channels → Live TV → add tuner source**, then either of the two - they are alternatives, not
   steps in a row:
   * press **Detect My Devices** and pick "ZDF Live TV" from the results. The list is `GET
     /LiveTv/Tuners/Discover`, which asks every installed tuner type at once and ignores whatever the
     dropdown says, so with both plugins of this flake installed you will be offered both rows however
     hard you tried to select one. Pick yours; a source already added is not offered again. This path also
     stores the name and device id the plugin reports, which is why it shows up as "ZDF Live TV".
   * or pick **ZDF Live TV** in the dropdown and continue - nothing needs detecting here, so pressing
     Detect first only adds a step. That form posts no name unless the field is filled in, which used to
     leave the row titled "Unknown": the web client titles a device by its friendly name and otherwise
     falls back to a label taken from a switch over the four tuner types shipped with the server, so any
     other type reads as "Unknown" whatever it is. The plugin names the device while the server is still
     saving it - `Validate()` is handed the very entry that then goes into `livetv.xml` - so both paths read
     "ZDF Live TV" as soon as the page redraws. A row an older version left unnamed is named on the next
     channel list.

   Either way the URL field is ignored - channel URLs come from the plugin, it only has to look like a URL
   for the form's validation. This step is required at all because Jellyfin only offers Live TV once ≥1
   tuner source exists.
2. **Same page → add programme guide → XMLTV**, path
   `https://epgshare01.online/epgshare01/epg_ripper_DE1.xml.gz`. Jellyfin unpacks `.gz` URLs itself.
   No manual channel mapping is needed: every channel carries its guide id in
   `ChannelInfo.TunerChannelId` (`ZDF.de`, `ZDFneo.de`, `PHOENIX.de`, ...), which is the second thing
   Jellyfin tries when matching programme data.

## Geo restriction

The streams are geo-fenced to Germany. From a non-German IP every ZDF CDN request answers **HTTP 403** -
verified by comparing hosts: `zdf-hls-15/17/19` all returned 403 while KiKA on the very same
`akamaized.net` edge returned 200, so it is a geo policy and not a stale URL or a header check (no
combination of browser User-Agent and Referer changed anything). Playback therefore needs PC-001's normal
German uplink; outbound HTTPS is all that has to be permitted. A quick check from the machine itself:

```sh
curl -so /dev/null -w '%{http_code}\n' https://zdf-hls-15.akamaized.net/hls/live/2016498/de/high/master.m3u8
# 200 = fine, 403 = this IP is outside the allowed region
```

## Changing settings

Defaults are complete (the seven channels above, 60 min URL cache), so no configuration is required.

The normal way to change them is the plugin's own **Settings page** (Dashboard → Plugins → ZDF Live TV →
Settings): every channel the catalogue knows plus anything else the stream API is serving right now, listed with
its guide id and group and ticked from what is configured, with a filter and select-all/none shortcuts. Saving
takes effect by itself - no restart, no rebuild and nothing to press elsewhere: saving queues the same
rebuild that adding a tuner device queues (`GuideRefresh.cs`), which matters more here than it sounds because
this plugin ships no programme guide at all, so "run the guide task" was advice about somebody else's feature.
Channels that are configured but currently
unresolvable stay in the list, or saving would quietly delete them; should the API start carrying a channel this
catalogue does not name, it can be ticked as well - with no guide id and no artwork until it is added to
`ZdfChannelCatalog.cs`. Logos, the logo host and a
per-channel logo URL are on the same page.

Everything is also a NixOS option in `modules/jellyfin-zdf.nix`, for when this configuration really is the source
of truth:

```nix
services.jellyfinZdfLive = {
  enable = true;
  enabledChannels = [ "zdf" "zdf_neo" "arte" "kika" ]; # keys from ZdfChannelCatalog.cs
  cacheMinutes = 30;
};
```

`enabledChannels` is a `types.enum`, so a typo fails at evaluation instead of making a channel vanish.
The generated plugin configuration can be inspected without deploying:

```sh
nix build .#nixosConfigurations.PC-001.config.services.jellyfinZdfLive.configFile -L && result
```

Two behaviours worth knowing, both measured rather than assumed:

* `manageConfig` defaults to **false**: with it on, the module rewrites the plugin's XML on every switch and
  anything set in the settings page since is gone - which would undo the point of having a settings page. Turn it
  on only if Nix is the single source of truth for this plugin.
* *Removing* a channel takes two guide refreshes to disappear from the list: the first refresh adds what
  is new and leaves the dropped channel as an unplayable leftover, the second one prunes it.

## Channel artwork

The German XMLTV guide these channels are matched against publishes no icons, so without help a channel has no
picture anywhere. `ChannelInfo.ImageUrl` is set from a logo host instead - by default
`https://static.epg.best/de/{id}.png`, with the id being the guide id except where that host spells it
differently (`ARD.de` for Das Erste, `SWRSR.de` for the SWR/SR programme, `Phoenix.de`).

Measured against that host: it carries thirteen of the thirty channels under their own id and none of the ARD
state windows, so those are looked up under their parent broadcaster (`BR.de` for BR Süd, `MDR.de` for both extra
MDR windows, `RBB.de` for rbb Brandenburg, `SWRSR.de` for SWR Rheinland-Pfalz) - that host keeps one image per ARD
member. **3sat, everything NDR makes, ONE, Radio Bremen and SR are 404** there under every spelling tried, and the
two parliamentary channels have nothing to point at anywhere - so those ten channels carry their logo inside the
plugin instead (six files): `Assets/*.png`, embedded in the assembly by the csproj and unpacked once into
`<dataDir>/data/ZdfLive/logos` by `ChannelLogoAssets`, which is what `ChannelInfo.ImagePath` then points at.
`Assets/SOURCES.md` lists each source and its licence - all six are Public domain word marks rendered from
Wikimedia Commons SVGs at 256 px, 40 kB together.

That is not tidiness but the failure mode: a channel's remote logo is fetched only when something asks for the tile
the first time (`ImageController` → `LibraryManager.ConvertImageToLocal(..., removeOnFailure: true)`), and one
failed fetch answers that request with an error *and* deletes the stored image reference. The tile then shows
nothing usable until artwork gets written again - which for a channel means waiting for
`GuideManager.UpdateChannel`, and that only writes to a channel holding no image at all. A file already on disk
cannot fail that way: serving one measured 15-30 ms against the ~1.3 s of the first Wikimedia fetch, with no
outbound request either.

Precedence is per-channel URL from the settings page, then no artwork at all (`ShowChannelLogos` off or an empty
template), then the shipped file, then the template plus id. A channel with a shipped file does not fall back to
the template: for those entries that address is known to 404, so falling back would trade no picture for a broken
one.

Every address is HEAD-checked before it is handed out (`File.Exists` for a shipped file), because Jellyfin stores a
channel's image only while the item has none (`GuideManager.UpdateChannel` tests `!item.HasImage(Primary)`) - an
address that turns out not to exist would stay on that channel permanently. The same rule works against a later fix
though: what is stored is never replaced either, so 3sat and NDR went on showing a broken tile long after being
moved to working addresses, and a client shows that broken picture rather than falling back to the letter tile it
uses when an item has no image at all. `ChannelArtwork.cs` covers both ends of it - it compares what each channel
holds against what is offered now and steps in when what it holds cannot be loaded (a URL answering 403/404/410, or
a file that has gone), and since the shipped files also replaces an address for artwork this plugin now carries
itself, without waiting for it to break. Artwork the plugin never offered is left alone, including the copy
Jellyfin makes into the channel's own metadata folder on first view.

One limit worth stating because it is often assumed otherwise: a tuner host can hand over exactly **one** image
per channel (`ChannelInfo` has only `ImagePath`, `ImageUrl`, `HasImage`) - there is no banner field in the
contract. Wider artwork belongs to programmes (`ProgramInfo.ImageUrl/ThumbImageUrl/BackdropImageUrl`), which for
these channels comes from the XMLTV guide rather than from this plugin.

## Verified state (2026-11-11, against jellyfin 10.11.11 - the version this flake pins)

Built with `nix build` from a clean tree and run in a real server instance:

* plugin loads (`Loaded plugin: "ZDF Live TV" "1.0.0"`), tuner type appears:
  `GET /LiveTv/TunerHosts/Types` → HD Homerun, M3U Tuner, **ZDF Live TV**
* `DiscoverDevices()` is picked up: `Discovered tuner device "ZDF Live TV" at "https://www.zdf.de/live-tv"`
* all 7 default channels listed after a guide refresh, resolved through the live API
* EPG matched per channel: `Found epg channel in "XmlTV" null "ZDF" "ZDF.de"` (likewise ZDFneo, ZDFinfo, KiKA)
* playback plumbing works end to end on KiKA (the one channel not geo-blocked from the test network):
  ffprobe returned real media info (`Container: hls`, h264 640x360@50, AAC stereo "Deutsch"), Jellyfin
  served a master playlist and then actual `.ts` segments through it
* declarative `enabledChannels` respected (7 → 2 channels and back)

Added since, on a running server (2026-11-18):

* saving applies without being asked twice: POSTing a configuration that dropped `ndr_nds` removed *NDR
  Niedersachsen* from the library on its own (`ZDF Live TV: saved, queued a guide refresh`, then
  `TaskManager: Refresh Guide Completed after 0 minute(s) and 8 seconds`); putting it back restored the channel.
  Two saves produced two runs, so the hook does not feed itself
* `/Items/{id}/Images/Primary` now returns real artwork for the two channels epg.best lacks: 3sat 26523 B and
  NDR 19438 B, byte-for-byte what Wikimedia Commons serves for those files
* a channel holding an image that cannot be loaded has it replaced on the next refresh: pointed at a file that
  was not there, *3sat* logged `was still pointing at ..., which is not there; it now uses ...` and its stored
  path became the logo offered then - and stayed that way, because nothing else writes it again
* all thirty channels of the stream API are offered once ticked (seventeen named before, thirteen catalogued
  now); every one of them ends up with artwork except the two parliamentary channels, which have none anywhere
* a source added without a name gets one: after deleting both `<FriendlyName>` elements from `livetv.xml`, the
  next refresh logged `ZDF Live TV: named the 'zdf' tuner device ZDF Live TV` and the file held them again

Added since, on a running server (2026-11-19):

* artwork that ships with the plugin is served from disk: after a guide refresh *3sat*, *ONE*, the four NDR feeds,
  *Radio Bremen*, *SR* and both parliamentary channels hold a path under `<dataDir>/data/ZdfLive/logos/`, and
  `/Items/{id}/Images/Primary` answers `200 image/png` from it in 15-30 ms (the first Wikimedia fetch had taken
  1.3 s, and any failed fetch used to leave a broken tile behind - see the artwork section)
* both parliamentary channels have a picture now; they share the Bundestag's own word mark, which is the only
  artwork either of them has anywhere
* an install that already held remote logo addresses is moved onto the shipped files without anything breaking
  first: one pass logged `'NDR Niedersachsen' was still pointing at https://commons.wikimedia.org/...; this plugin
  carries that picture itself now, so it uses ...` for six channels and rewrote their stored paths
* a device added the way the dropdown adds one - `POST /LiveTv/TunerHosts` with `"FriendlyName": null` - is
  returned *and* written to `livetv.xml` as `"ZDF Live TV"` in that same request, so the tuner list never shows
  "Unknown" for it; a name typed by an operator (`"Mein Sender"`) is kept as given

ZDF/ZDFneo/ZDFinfo/3sat/phoenix playback could **not** be tested from the network available here, because
that egress is not in Germany - see the geo section. Everything up to and including handing the URL to the
encoder was verified; only the CDN's regional answer is untested until PC-001 tries it.

## Building / developing

```sh
nix build .#jellyfin-plugin-zdf-live         # dll + meta.json in lib/jellyfin/plugins/ZdfLive
nix develop .#plugin-dev                     # dotnet SDK 9 for iterating on the C#
dotnet build Jellyfin.Plugin.ZdfLive.csproj -c Release
```

The result stays two files: the bundled logos are embedded in the dll (`Assets\*.png`), so nothing about
installing or updating the plugin changes when artwork is added.

Targets `net9.0` and pins `Jellyfin.{Common,Controller,Model}` to **10.11.11**, exactly the server version
in the pinned nixpkgs; `ExcludeAssets=runtime` keeps those assemblies out of the output so the plugin uses
the ones already loaded by the server. After changing a dependency version, refresh the lockfile with

```sh
nix build --no-link --print-out-paths .#jellyfin-plugin-zdflive.passthru.fetch-deps | xargs -I{} {}
```

(the script writes `nuget-deps.json` in this directory by default - pass a path only if you want it elsewhere).

### Gotchas found while wiring this up

* Plugins are read from **`<dataDir>/plugins/<dir>/`**, not `<configDir>/plugins/`: `PluginsPath` is
  `ProgramDataPath/plugins`. With the defaults on PC-001 that is `/var/lib/jellyfin/plugins/ZdfLive`.
* The plugin folder needs no version suffix and no dot in its name; keep it stable, because a second
  folder claiming the same plugin *name* gets deleted by Jellyfin's version cleanup.
* Files are copied rather than symlinked into place: Jellyfin rewrites `meta.json` when it marks a plugin
  malfunctioning, which fails against a read-only nix store target.
