# Twitch Live TV for Jellyfin - the channels in services.jellyfinTwitch.channels become Live TV
# channels, played straight from Twitch's own CDN.
#
# Like modules/jellyfin-zdf.nix this is a real tuner plugin rather than an M3U playlist:
# plugins/jellyfin-plugin-twitchlive registers an ITunerHost into the server's own service collection,
# so "Twitch Live TV" appears as a tuner type next to HDHomeRun and IPTV, and Jellyfin hands Twitch's
# HLS URLs straight to jellyfin-ffmpeg. No threadfin/xTeVe, no playlist files, no hardware.
#
# Two things work differently from the ZDF plugin, and both are deliberate:
#
#   * There is no XMLTV for "who is streaming right now", so the plugin ships its own guide provider as
#     well: live channels get their stream title and category in the guide, offline ones get an explicit
#     "offline" entry. Both are added from the same web UI page - tuner source *and* programme guide -
#     and neither needs a URL.
#
#   * The channel list is meant to be edited at runtime, in the plugin's own configuration page or over
#     the REST API; the plugin re-reads it on every request, so no restart is involved. That is why
#     manageConfig below defaults to false: a file written from Nix would silently undo whatever was
#     typed into that page on the next switch. Set it to true only if this configuration really is the
#     single source of truth for the list.
#
# Playback needs nothing beyond outbound HTTPS. Twitch does not geo-fence ordinary streams, and the
# playback token that unlocks a stream is bound to the requesting IP address - which is the same machine
# Jellyfin runs ffmpeg on, so it works without any header tricks (verified: usher.ttvnw.net answers 200
# for a bare request with a valid token). Sub-only streams and locally blocked streams stay unwatchable,
# as they are in the browser too.
{ config, lib, pkgs, ... }:

let
  cfg = config.services.jellyfinTwitch;

  # Folder name under <dataDir>/plugins. Jellyfin scans that directory one level deep and takes the
  # plugin's real name from meta.json, so this is only a label - but it has to be stable, because a
  # second folder claiming the same plugin name gets deleted by the server's version cleanup.
  pluginFolder = "TwitchLive";

  # Where Jellyfin keeps compiled plugins. Note: <dataDir>/plugins, *not* <configDir>/plugins -
  # PluginsPath is ProgramDataPath/plugins (Emby.Server.Implementations/AppBase/BaseApplicationPaths.cs),
  # and with the defaults here that is /var/lib/jellyfin/plugins.
  pluginPath = "${config.services.jellyfin.dataDir}/plugins/${pluginFolder}";

  # The plugin's own settings, in exactly the format Jellyfin writes them itself
  # (<dataDir>/plugins/configurations/<assembly name>.xml).
  pluginConfig = pkgs.writeText "jellyfin-plugin-twitch-live.xml" ''
    <?xml version="1.0" encoding="utf-8"?>
    <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
      <Channels>
    ${lib.concatMapStrings (c: "    <string>${c}</string>\n") cfg.channels}  </Channels>
      <IncludeOfflineChannels>${lib.boolToString cfg.includeOfflineChannels}</IncludeOfflineChannels>
      <ClientId>${cfg.clientId}</ClientId>
      <StatusCacheSeconds>${toString cfg.statusCacheSeconds}</StatusCacheSeconds>
      <TokenCacheSeconds>${toString cfg.tokenCacheSeconds}</TokenCacheSeconds>
      <GuideAheadMinutes>${toString cfg.guideAheadMinutes}</GuideAheadMinutes>
      <AllowSourceQuality>${lib.boolToString cfg.allowSourceQuality}</AllowSourceQuality>
      <UserAgent>${cfg.userAgent}</UserAgent>
    </PluginConfiguration>
  '';

  # <dataDir>/plugins/configurations - where Jellyfin keeps plugin settings.
  dirOfConfigFile = "${config.services.jellyfin.dataDir}/plugins/configurations";

  # Twitch's own login rules. A typo here would otherwise only show up as a channel that never goes
  # live, which is a slow way to discover a stray space.
  isValidLogin = login: builtins.match "^[a-zA-Z0-9_]{3,25}$" login != null;
in
{
  options.services.jellyfinTwitch = {
    enable = lib.mkEnableOption ''Jellyfin plugin for Twitch live channels'';

    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../plugins/jellyfin-plugin-twitchlive/package.nix { };
      defaultText = lib.literalExpression "pkgs.callPackage ../plugins/jellyfin-plugin-twitchlive/package.nix { }";
      description = "The Twitch Live TV Jellyfin plugin to install.";
    };

    channels = lib.mkOption {
      type = with lib.types; listOf str;
      default = [ ];
      example = [ "lofigirl" "esl_sc2" ];
      description = ''
        Initial list of Twitch logins to offer as channels, in guide order.

        This is only a starting point unless {option}`manageConfig` is on: the plugin reads its channel
        list from its own configuration at runtime, so streamers are normally added and removed in the
        web UI (Dashboard -> Plugins -> Twitch Live TV -> Settings) or with
        `PUT /Plugins/acfb8b6e-f2ce-4615-800f-77c40ffd213d/Configuration`, and neither a rebuild nor a
        restart is involved. With {option}`manageConfig` on, this list wins and a switch rewrites the
        file - including anything changed in the UI since.
      '';
    };

    includeOfflineChannels = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Whether channels that are not streaming stay in the channel list, in their own "Twitch - Offline"
        group with an "offline" guide entry. Turn it off to offer only live channels, at the cost of
        channels appearing and disappearing between guide refreshes.
      '';
    };

    clientId = lib.mkOption {
      type = lib.types.str;
      default = "kimne78kx3ncx6brgo4mv6wki5h1ko";
      description = ''
        Twitch client id sent to the GraphQL endpoint. The default is the public id of the twitch.tv web
        player, which needs no registration; a private one from https://dev.twitch.tv/console works too.
        No secret and no user login are involved - this plugin only ever reads what the player can see.
      '';
    };

    statusCacheSeconds = lib.mkOption {
      type = with lib.types; ints.between 5 3600;
      default = 60;
      description = ''
        How long a fetched live/offline answer is reused. One request answers every configured channel,
        so this only decides how quickly going on- and offline becomes visible.
      '';
    };

    tokenCacheSeconds = lib.mkOption {
      type = with lib.types; ints.between 0 600;
      default = 120;
      description = ''
        How long a fetched playback token is reused. Tuning to a channel asks for a media source and then
        for the stream, so a short cache keeps that from costing two tokens. It does not limit how long a
        stream can play: only the first playlist request needs the token.
      '';
    };

    guideAheadMinutes = lib.mkOption {
      type = with lib.types; ints.between 15 1440;
      default = 240;
      description = ''
        How far ahead of now a running stream is shown in the guide. Twitch publishes no schedule, so
        everything past "now" is a guess.
      '';
    };

    allowSourceQuality = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Offer Twitch's untranscoded source rendition next to the regular ones (usher's `allow_source`).
      '';
    };

    userAgent = lib.mkOption {
      type = lib.types.str;
      default = "";
      example = "Mozilla/5.0";
      description = ''
        Optional User-Agent for requests to the Twitch CDNs. Empty sends none, which is what they expect:
        unlike the ZDF CDNs these answer a plain request, because the playback token already carries the
        client's IP address.
      '';
    };

    manageConfig = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Rewrite the plugin's configuration file from this module on every switch.

        Off by default, because the channel list is designed to be edited while the server runs and a
        managed file would quietly revert those edits. With it off, {option}`channels` still applies - but
        only as a first-run seed: the file is written when Jellyfin does not have one yet, and from then on
        the running server owns it. Turn this on to make the Nix configuration the single source of truth
        instead, and accept that edits made in the web UI are lost at the next switch.
      '';
    };

    configFileTarget = lib.mkOption {
      type = lib.types.str;
      default = "${dirOfConfigFile}/Jellyfin.Plugin.TwitchLive.xml";
      internal = true;
      description = "Where the plugin keeps its own settings.";
    };

    # Not a knob - just the generated file, so `nix build .#nixosConfigurations.PC-001.config.
    # services.jellyfinTwitch.configFile` shows exactly what would be dropped into Jellyfin.
    configFile = lib.mkOption {
      type = with lib.types; nullOr path;
      default = null;
      internal = true;
      description = "The plugin configuration file generated from the options above.";
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      {
        assertion = config.services.jellyfin.enable;
        message = "services.jellyfinTwitch requires services.jellyfin.enable.";
      }
      {
        # Checked at evaluation time: a rejected login here would otherwise sit in the channel list
        # looking like a streamer who simply never goes live.
        assertion = builtins.all isValidLogin cfg.channels;
        message = "services.jellyfinTwitch.channels must be Twitch logins - 3 to 25 letters, digits and underscores (not display names, no spaces): ${lib.concatStringsSep ", " (builtins.filter (c: !isValidLogin c) cfg.channels)}";
      }
    ];

    # Plugins are only read while the server boots, so this runs in preStart rather than as an
    # activation script: switching always restarts jellyfin (the unit text changes with the store path
    # below) and the plugin directory is then exactly what this configuration says. Files are copied
    # instead of symlinked because Jellyfin rewrites meta.json when it disables a plugin, which fails on
    # a read-only nix store target.
    services.jellyfinTwitch.configFile = pluginConfig;

    systemd.services.jellyfin.preStart = lib.mkAfter (''
      install -d -m 0755 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${pluginPath}'
      install -m 0644 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${cfg.package}/lib/jellyfin/plugins/TwitchLive/'* '${pluginPath}/'
    '' + lib.optionalString cfg.manageConfig (''
      # Managed: this configuration wins, and edits made in the web UI are overwritten.
      install -d -m 0755 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${dirOfConfigFile}'
      install -m 0644 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${pluginConfig}' '${cfg.configFileTarget}'
    '') + lib.optionalString (!cfg.manageConfig && cfg.channels != [ ]) (''
      # Seeded once: a fresh installation starts from the list in this configuration, and whatever the
      # running server makes of it afterwards is left alone. Without the guard every switch would undo
      # each streamer added through the web UI - which is exactly what manageConfig = false promises.
      if [ ! -e '${cfg.configFileTarget}' ]; then
        install -d -m 0755 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${dirOfConfigFile}'
        install -m 0644 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${pluginConfig}' '${cfg.configFileTarget}'
      fi
    ''));
  };
}
