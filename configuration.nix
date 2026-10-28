# Edit this configuration file to define what should be installed on
# your system.  Help is available in the configuration.nix(5) man page
# and in the NixOS manual (accessible by running ‘nixos-help’).

{ config, lib, pkgs, inputs, ... }:

let
  opts = { system = pkgs.stdenv.hostPlatform.system; config = { allowUnfree = true; }; };
  # nixos-25-05 = import inputs.nixos-25-05 opts; # unused
  # nixos-25-11 = import inputs.nixos-25-11 opts; # unused
  # nixos-26-05 = import inputs.nixos-26-05 opts; # unused
  nixos-unstable = import inputs.nixos-unstable opts;

in {
  imports =
    [ # Include the results of the hardware scan.
      ./hardware-configuration.nix
      ./modules/boot.nix
      ./modules/filesystems.nix
      ./modules/hardware.nix
      ./modules/i18n.nix
      ./modules/desktop.nix
      ./modules/users.nix
      ./modules/environment.nix
      ./modules/networking.nix
      ./modules/firewall.nix
      ./modules/programs.nix
    ];

#  xdg.configFile."systemd/system/auditd.service.d/overrides.conf".text = ''
#  [Unit]
#  RefuseManualStop=no
#'';

  systemd.coredump.enable = false;
#  systemd.services.auditd.unitConfig = {
#    RefuseManualStop = "no";
#  };
#  systemd.automounts = [
#    {
#      enable = true;
#      name = "mnt-storage001.local.automount";
#      description = "Automount storage001.local";
#      where = "/mnt/storage001.local";
#      automountConfig = {
#        TimeoutIdleSec = 0;
#      };
#      wantedBy = [
#        "multi-user.target"
#      ];
#    }
#  ];
#  systemd.mounts = [
#    {
#      name = "mnt-storage001.local.mount";
#      wantedBy = [
#        "remote-fs.target"
#        "multi-user.target"
#      ];
#      before = [
#        "remote-fs.target"
#      ];
#      description = "Storage001 SSHFS";
#      what = "user@storage001.fritz.box:/mnt/data";
#      where = "/mnt/storage001.local";
#      type = "fuse." + pkgs.sshfs-fuse.meta.mainProgram;
#      options = "_netdev,rw,reconnect,port=2222,nosuid,allow_other,uid=" + (toString config.users.users.user.uid) +",gid=" + (toString config.users.groups.users.gid) + ",follow_symlinks,idmap=user,default_permissions,identityfile=/root/.ssh/user_storage001.local_automount_id_ed25519";
#    }
#  ];
  systemd.services."user@".serviceConfig = {
    Delegate = "yes";
  };

  systemd.services."systemd-user-sessions".after = lib.mkForce [];

  system.nssDatabases.hosts = lib.mkForce [
    "mymachines"
    #"resolve [!UNAVAIL=return]"
    "resolve"
    "files"
    "myhostname"
    "dns"
  ];

  xdg.portal.xdgOpenUsePortal = true; # Sets environment variable NIXOS_XDG_OPEN_USE_PORTAL to 1 This will make xdg-open use the portal to open programs, which resolves bugs involving programs opening inside FHS envs or with unexpected env vars set from wrappers. See https://github.com/NixOS/nixpkgs/issues/160923 for more info.

  # UniFi
  services.unifi = {
    enable = false;
    openFirewall = true;
    initialJavaHeapSize = 4096;
    unifiPackage = nixos-unstable.pkgs.unifi; # nixos-25.11's unifi package is marked as insecure
    jrePackage = pkgs.jdk25_headless;
  };

  virtualisation.lxc.enable = true;
  virtualisation.lxc.unprivilegedContainers = true;
  virtualisation.podman.enable = true;
  virtualisation.podman.dockerSocket.enable = true;
  virtualisation.podman.autoPrune.enable = true; # If enabled, a systemd timer will run podman system prune -f as specified by the dates option.
  virtualisation.podman.dockerCompat = true;
  virtualisation.containers.enable = true;
  virtualisation.containers.ociSeccompBpfHook.enable = true;
  virtualisation.containerd.enable = true;
  virtualisation.waydroid.enable = true;
  virtualisation.libvirtd.enable = true;
  virtualisation.libvirtd.qemu.package = pkgs.qemu_full;
  virtualisation.libvirtd.qemu.swtpm.enable = true;
  virtualisation.libvirtd.qemu.vhostUserPackages = [
    pkgs.virtiofsd
  ];
#  virtualisation.libvirtd.qemu.ovmf.packages = [
#    pkgs.OVMFFull.fd
#    pkgs.pkgsCross.aarch64-multiplatform.OVMF.fd
#  ];

  # TPM2
  security.tpm2.enable = true;
  security.tpm2.pkcs11.enable = true;  # expose /run/current-system/sw/lib/libtpm2_pkcs11.so
  security.tpm2.tctiEnvironment.enable = true;  # TPM2TOOLS_TCTI and TPM2_PKCS11_TCTI env variables

  # Allow unfree packages
  nixpkgs.config.allowUnfree = true;

  # As we're also "playing" with modifying/updateing nixpkgs ourselves apply the recommended default value
  # to avoid having nix reuse an older cached version when we tried to update a fetched file within a package
  # and want to provoke an error showing the new hash value.
  # See https://github.com/NixOS/nix/issues/969 and
  # https://nixos.org/manual/nixpkgs/unstable/#opt-fetchedSourceNameDefault for why this is required.
#  nixpkgs.config.fetchedSourceNameDefault = "full";
#  nixpkgs.config.fetchedSourceNameDefault = "versioned";

#  nixpkgs.config.enableParallelBuildingByDefault = true;

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
  services.i2pd = {
    enable = true;
    yggdrasil.enable = false;
#  websocket.enable = true;
    upnp.enable = true;
    reseed.verify = false;
    reseed.file = "./i2pseeds.su3"; # wget --user-agent="Wget/1.11.4" https://reseed.stormycloud.org/i2pseeds.su3
    proto.socksProxy.enable = true;
    proto.i2cp.enable = true;
    proto.i2pControl.enable = true;
    proto.http.enable = true; # WebUI
    port = 19410;
    enableIPv6 = true;
    enableIPv4 = true;
    ntcp2 = {
      enable = true;
      published = true;
    };
#    ssu2.enable = true;
#    ssu2.published = true;
    proto.httpProxy.enable = true;
  };
  services.tor = {
    enable = true;
    relay = {
      enable = true;
      role = "bridge";
    };
    openFirewall = true;
    tsocks.enable = true;
    client = {
      enable = true;
      dns.enable = true;
    };
    settings = {
      # Network settings
      ORPort = 443;

      # Reject all exit traffic
      ExitPolicy = "reject *:*";

      # Performance and security settings
      CookieAuthentication = true;
      AvoidDiskWrites = 1;
      HardwareAccel = 1;
      SafeLogging = 1;
      NumCPUs = 16;

      # Bandwidth settings
#      MaxAdvertisedBandwidth = "100 MB";
#      BandWidthRate = "50 MB";
#      RelayBandwidthRate = "50 MB";
#      RelayBandwidthBurst = "100 MB";
    };
    controlSocket.enable = false;
  };
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
  # This value determines the NixOS release from which the default
  # settings for stateful data, like file locations and database versions
  # on your system were taken. It‘s perfectly fine and recommended to leave
  # this value at the release version of the first install of this system.
  # Before changing this value read the documentation for this option
  # (e.g. man configuration.nix or on https://nixos.org/nixos/options.html).
  system.stateVersion = "25.05"; # Did you read the comment?

  # system.autoUpgrade.channel = "https://nixos.org/channels/nixos-unstable";
  system.autoUpgrade = {
    enable = true; # periodically runs "nixos-rebuild switch --upgrade"
    channel = "https://nixos.org/channels/nixos-${config.system.nixos.release}";
  };
  nix.gc.automatic = true;
  nix.gc.options = "--delete-older-than 30d";
  nix.gc.dates = "weekly";
  nix.optimise.automatic = true;
  nix.settings.experimental-features = ["nix-command flakes"];
  programs.nix-index = {
    enable = true;
    enableBashIntegration = false;
    enableZshIntegration = false;
    enableFishIntegration = false;
  };
}

