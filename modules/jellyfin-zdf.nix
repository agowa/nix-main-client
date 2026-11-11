# ZDF Live TV for Jellyfin - live channels of the public broadcasters carried by ZDF:
# ZDF, ZDFneo, ZDFinfo, 3sat, ARTE, KiKA and phoenix (the offering on https://www.zdf.de/live-tv).
#
# This is a real tuner plugin, not an M3U playlist: plugins/jellyfin-plugin-zdflive registers an
# ITunerHost into the server's own service collection, so "ZDF Live TV" shows up as a tuner type next
# to HDHomeRun and IPTV, and Jellyfin hands the broadcasters' HLS URLs straight to jellyfin-ffmpeg.
# No proxy (threadfin/xTeVe), no playlist files, no hardware DVB stick - the stream never touches this
# configuration except as a URL string.
#
# Two things are worth knowing before enabling it:
#
#   * The streams themselves come from ZDF's/ARD's own CDNs and are geo-fenced to Germany. From
#     outside Germany every request answers HTTP 403, so playback needs PC-001's normal internet
#     uplink - nothing here has to be allowlisted beyond outbound HTTPS. (Verified: the CDN answers
#     403 for all hosts from a non-German IP while KiKA on the same CDN works, so it is geo policy.)
#   * Channel URLs are resolved at runtime from https://api.zapp.mediathekview.de (no key needed),
#     which is why nothing here pins an akamaized path that would rot. If that API is unreachable the
#     plugin falls back to the last known paths baked into its catalogue and logs one warning.
#
# Installing the plugin is not quite the same as switching Live TV on: Jellyfin only offers Live TV
# once at least one tuner source exists, and it stores tuner sources plus guide providers in
# ${configDir}/livetv.xml together with everything else the operator configures by hand. Writing that
# file from here would silently delete any other tuner or EPG added through the web UI, so the two
# one-time steps below stay in the UI on purpose:
#
#   1. Guide + Channels -> Live TV -> add tuner source -> "ZDF Live TV" (its own entry; the URL field
#      is irrelevant and may be left as offered by "Detect").
#   2. Same page -> add programme guide -> XMLTV, path
#      https://epgshare01.online/epgshare01/epg_ripper_DE1.xml.gz (German guide, gzipped - Jellyfin
#      unpacks .gz URLs itself). Channels are matched by their guide id automatically, so no manual
#      channel mapping is needed.
{ config, lib, pkgs, ... }:

let
  cfg = config.services.jellyfinZdfLive;

  # Folder name under <dataDir>/plugins. Jellyfin scans that directory one level deep and takes the
  # plugin's real name from meta.json, so this is only a label - but it has to be stable, because a
  # second folder claiming the same plugin name gets deleted by the server's version cleanup.
  pluginFolder = "ZdfLive";

  # Where Jellyfin keeps compiled plugins. Note: <dataDir>/plugins, *not* <configDir>/plugins -
  # PluginsPath is ProgramDataPath/plugins (Emby.Server.Implementations/AppBase/BaseApplicationPaths.cs),
  # and with the defaults here that is /var/lib/jellyfin/plugins.
  pluginPath = "${config.services.jellyfin.dataDir}/plugins/${pluginFolder}";

  # The plugin's own settings, in exactly the format Jellyfin writes them itself
  # (<dataDir>/plugins/configurations/<assembly name>.xml). Managing it here is what makes "also give
  # me ZDFneo in SD" a system configuration change instead of a click in a web form; the trade-off is
  # that a nixos-rebuild rewrites the file, so changes made through Jellyfin's REST API do not survive.
  pluginConfig = pkgs.writeText "jellyfin-plugin-zdf-live.xml" ''
    <?xml version="1.0" encoding="utf-8"?>
    <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
      <EnabledChannels>
    ${lib.concatMapStrings (c: "    <string>${c}</string>\n") cfg.enabledChannels}  </EnabledChannels>
      <CacheMinutes>${toString cfg.cacheMinutes}</CacheMinutes>
      <ApiBaseUrl>${cfg.apiBaseUrl}</ApiBaseUrl>
      <SendRefererHeader>${lib.boolToString cfg.sendRefererHeader}</SendRefererHeader>
    </PluginConfiguration>
  '';

  # Keys known to the plugin's catalogue; a typo here would otherwise just make a channel vanish.
  # Keep in sync with ZdfChannelCatalog.cs (the catalogue also carries each channel's guide id).
  catalogKeys = [
    "zdf" "zdf_neo" "zdf_info" "dreisat" "phoenix" "arte" "kika"
    "das_erste" "tagesschau24" "ard_alpha" "wdr" "mdr_sachsen" "br_nord"
    "hr" "rbb_berlin" "ndr_nds" "swr_bw"
  ];
in
{
  options.services.jellyfinZdfLive = {
    enable = lib.mkEnableOption ''Jellyfin plugin for the ZDF live TV channels'';

    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../plugins/jellyfin-plugin-zdflive/package.nix { };
      defaultText = lib.literalExpression "pkgs.callPackage ../plugins/jellyfin-plugin-zdflive/package.nix { }";
      description = "The ZDF Live TV Jellyfin plugin to install.";
    };

    enabledChannels = lib.mkOption {
      type = with lib.types; listOf (enum catalogKeys);
      default = [ "zdf" "zdf_neo" "zdf_info" "dreisat" "phoenix" "arte" "kika" ];
      defaultText = lib.literalExpression ''[ "zdf" "zdf_neo" "zdf_info" "dreisat" "phoenix" "arte" "kika" ]'';
      example = [ "zdf" "zdf_neo" "das_erste" ];
      description = ''
        Catalogue keys to offer as channels. The default is the set that ZDF itself streams on
        https://www.zdf.de/live-tv; the ARD channels are reachable through the same API and can be
        added on demand. Order decides the channel numbers in the guide.
      '';
    };

    cacheMinutes = lib.mkOption {
      type = with lib.types; ints.between 1 1440;
      default = 60;
      description = ''
        How long resolved stream URLs are reused before the MediathekView API is queried again.
        Only affects how quickly a moved CDN path is picked up.
      '';
    };

    apiBaseUrl = lib.mkOption {
      type = lib.types.str;
      default = "https://api.zapp.mediathekview.de/v1/channelInfoList";
      description = ''
        JSON endpoint mapping catalogue keys to current master playlists. Set to an unreachable
        address to force the plugin onto its built-in fallback URLs.
      '';
    };

    sendRefererHeader = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Send a https://www.zdf.de/ Referer for the ZDF-operated CDNs, next to the browser User-Agent
        that Jellyfin's own IPTV tuner also sends. Harmless if the CDN ignores it.
      '';
    };

    manageConfig = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Write the plugin's configuration file from this module. Turn off to keep settings that were
        made inside Jellyfin instead of having them rewritten on every switch.
      '';
    };
 
    # Not a knob - just the generated file, so `nix build .#nixosConfigurations.PC-001.config.
    # services.jellyfinZdfLive.configFile` shows exactly what will be dropped into Jellyfin.
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
        message = "services.jellyfinZdfLive requires services.jellyfin.enable.";
      }
    ];

    # Plugins are only read while the server boots, so this runs in preStart rather than as an
    # activation script: switching always restarts jellyfin (the unit text changes with the store
    # path below) and the plugin directory is then exactly what this configuration says. Files are
    # copied instead of symlinked because Jellyfin rewrites meta.json when it disables a plugin, which
    # fails on a read-only nix store target.
    services.jellyfinZdfLive.configFile = pluginConfig;

    systemd.services.jellyfin.preStart = lib.mkAfter (''
      install -d -m 0755 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${pluginPath}'
      install -m 0644 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${cfg.package}/lib/jellyfin/plugins/ZdfLive/'* '${pluginPath}/'
    '' + lib.optionalString cfg.manageConfig ''
      install -d -m 0755 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${config.services.jellyfin.dataDir}/plugins/configurations'
      install -m 0644 -o ${config.services.jellyfin.user} -g ${config.services.jellyfin.group} '${pluginConfig}' '${config.services.jellyfin.dataDir}/plugins/configurations/Jellyfin.Plugin.ZdfLive.xml'
    '');
  };
}
