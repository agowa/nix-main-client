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

1. **Guide + Channels → Live TV → add tuner source → "ZDF Live TV"** (or press *Detect*). The URL field is
   ignored - channel URLs come from the plugin, it only has to look like a URL for the form's validation.
   This step is required at all because Jellyfin only offers Live TV once ≥1 tuner source exists.
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
Everything is a NixOS option in `modules/jellyfin-zdf.nix`:

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

* The file is rewritten on every switch, so settings changed inside Jellyfin do not survive a rebuild.
  Set `manageConfig = false` to hand ownership back to the server.
* *Removing* a channel takes two guide refreshes to disappear from the list: the first refresh adds what
  is new and leaves the dropped channel as an unplayable leftover, the second one prunes it.

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

ZDF/ZDFneo/ZDFinfo/3sat/phoenix playback could **not** be tested from the network available here, because
that egress is not in Germany - see the geo section. Everything up to and including handing the URL to the
encoder was verified; only the CDN's regional answer is untested until PC-001 tries it.

## Building / developing

```sh
nix build .#jellyfin-plugin-zdflive          # dll + meta.json in lib/jellyfin/plugins/ZdfLive
nix develop .#plugin-dev                     # dotnet SDK 9 for iterating on the C#
dotnet build Jellyfin.Plugin.ZdfLive.csproj -c Release
```

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
