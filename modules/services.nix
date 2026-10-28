{ config, lib, pkgs, inputs, ... }:

let
  opts = { system = pkgs.stdenv.hostPlatform.system; config = { allowUnfree = true; }; };
  # nixos-25-05 = import inputs.nixos-25-05 opts; # unused
  # nixos-25-11 = import inputs.nixos-25-11 opts; # unused
  # nixos-26-05 = import inputs.nixos-26-05 opts; # unused
  nixos-unstable = import inputs.nixos-unstable opts;

in {
  # UniFi
  services.unifi = {
    enable = false;
    openFirewall = true;
    initialJavaHeapSize = 4096;
    unifiPackage = nixos-unstable.pkgs.unifi; # nixos-25.11's unifi package is marked as insecure
    jrePackage = pkgs.jdk25_headless;
  };

  # List services that you want to enable:
  services.fwupd.enable = true;
  services.languagetool.enable = true;
  services.openssh = {
    enable = true;
    allowSFTP = true;
    authorizedKeysInHomedir = false; # When false, the only files trusted by default are those in /etc/ssh/authorized_keys.d, i.e. SSH keys from users.users.<name>.openssh.authorizedKeys.keys.
    settings.GatewayPorts = "yes";
    settings.KbdInteractiveAuthentication = false;
    settings.PasswordAuthentication = false;
    settings.X11Forwarding = true;
  };
  services.smartd.enable = true;
  services.timesyncd.servers = [
    "2.europe.pool.ntp.org"
    "1.europe.pool.ntp.org"
    "0.europe.pool.ntp.org"
    "3.europe.pool.ntp.org"
  ];
  services.timesyncd.fallbackServers = [
    "2.pool.ntp.org"
    "1.pool.ntp.org"
    "0.pool.ntp.org"
    "3.pool.ntp.org"
  ];

  services.snowflake-proxy = {
    enable = true;
    capacity = 10;
  };
  services.octoprint = {
    enable = true;
    openFirewall = true;
    host = "::";
    port = 5000;
  };
  services.jellyfin = {
    enable = false;
    hardwareAcceleration = {
      device = "/dev/dri/renderD128";
      enable = true;
      type = "qsv";
    };
    openFirewall = true;
    forceEncodingConfig = true;
    transcoding = {
      enableHardwareEncoding = true;
      enableIntelLowPowerEncoding = true;
      hardwareDecodingCodecs = {
        av1 = true;
        h264 = true;
        #mpeg2 = true;
        hevc = true;
        hevc10bit = true;
        hevcRExt10bit = true;
        hevcRExt12bit = true;
        mpeg2 = true;
        vc1 = true;
        vp8 = true;
        vp9 = true;
      };
      hardwareEncodingCodecs = {
        av1 = true;
        hevc = true;
      };
    };
  };
  services.monero = {
    enable = true;
    dataDir = "/mnt/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5/storage001.local/blockchain/bitmonero";
  };
#  services.home-assistant = {
#    enable = true;
#    openFirewall = true;
##    configWritable = false;
##    lovelaceConfigWritable = false;
#    customComponents = with pkgs.home-assistant-custom-components; [
#      prometheus_sensor
#    ];
#    customLovelaceModules = with pkgs.home-assistant-custom-lovelace-modules; [
#      mini-graph-card
#      mini-media-player
#    ];
#    config = {
#      http = {
#        server_host = [
#          "0.0.0.0"
#          "::"
#        ];
#        server_port = 8123;
#      };
#      homeassistant = {
#        unit_system = "metric";
#        latitude = 50.1109;
#        longitude = 8.6821;
#      };
#    };
#  };

  services.btrfs.autoScrub = {
    enable = true;
    interval = "monthly";
  };
#  services.beesd.filesystems = {
#    "2df491cf-b27c-41cd-ad88-2aa2b25278ca" = {
#      spec = "UUID=2df491cf-b27c-41cd-ad88-2aa2b25278ca";
#      hashTableSizeMB = 294912; # Volume size * 4 / 1024 then convert into MB, rounded up to next value divisible by 16
#      verbosity = "crit";
#      extraOptions = [
#        "--loadavg-target"
#        #"5.0"
#        "192.0"
#      ];
#    };
#  };

#  services.beesd.filesystems = {
#    "-" = {
#      spec = "LABEL=root";
#      hashTableSizeMB = 2048;
#      verbosity = "crit";
#      extraOptions = [
#        "--loadavg-target"
#        "5.0"
#      ];
#    };
#  };

  # Firewall stuff
  services.pcscd.enable = true;
#  services.miniupnpd.enable = true;
#  services.miniupnpd.externalInterface = "vlan10";
#  services.miniupnpd.natpmp = true;
#  services.miniupnpd.upnp = true;
}
