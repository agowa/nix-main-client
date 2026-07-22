# Edit this configuration file to define what should be installed on
# your system.  Help is available in the configuration.nix(5) man page
# and in the NixOS manual (accessible by running ‘nixos-help’).

{ config, lib, pkgs, inputs, ... }:

let
  opts = { system = pkgs.stdenv.hostPlatform.system; config = { allowUnfree = true; }; };
  nixos-25-05 = import inputs.nixos-25-05 opts;
  nixos-25-11 = import inputs.nixos-25-11 opts;
  nixos-26-05 = import inputs.nixos-26-05 opts; # unused
  nixos-unstable = import inputs.nixos-unstable opts;

in {
  imports =
    [ # Include the results of the hardware scan.
      ./hardware-configuration.nix
    ];

  # Bootloader.
  boot.loader.systemd-boot.enable = true;
  boot.loader.efi.canTouchEfiVariables = true;
  boot.initrd.systemd.enable = true;

  boot.kernel.sysctl."vm.max_map_count" = 2147483647;
  boot.kernel.sysctl."vm.swappiness" = 100;
  # Hugepages
  boot.kernel.sysctl."vm.nr_hugepages" = 3072; # 6 GB
  #boot.kernel.sysctl."vm.nr_hugepages" = 51200; # 100 GB
  #boot.kernel.sysctl."vm.nr_hugepages" = 102400; # 200 GB

  boot.kernelParams = [
#    "mem_encrypt=on" # AMD SME
    "amd_iommu=pgtbl_v1"
#    "amd_iommu=pgtbl_v2"
    "iommu=pt"
    "iommu=nobypass"
    "iommu.passthrough=0" # 0 - Use IOMMU translation for DMA.
    "iommu.strict=1" # 1 - Strict mode.
    "kvm.ignore_msrs=1"
    "kvm.enable_virt_at_load=1" # Avoid delay at start of 1st VM.
    "pci=realloc"
    "amd_pstate=guided"
    "rd.luks.options=discard"
    "bgrt_disable" # Don't display OEM logo after loading ACPI tables
  ];

  # load in 1st stage before root file system has been mounted.
  boot.initrd.kernelModules = [
    "nvme"
    "mpt3sas"
    "dm_log"
    "dm_cache"
    "dm_cache_smq"
    "dm_thin_pool"
    "dm_raid"
    "dm_integrity"
    "dm_snapshot"

    # These are reported as "builtin", however btrfs documentation states:
    # > Many kernels are configured with SHA256 as built-in and not as a module. The accelerated versions are however provided by the modules and must be loaded explicitly (modprobe sha256) before mounting the filesystem to make use of them. You can check in /sys/fs/btrfs/FSID/checksum which one is used. If you see sha256-generic, then you may want to unmount and mount the filesystem again.
    # Therefore listing these here explicitly just in case.
    "sha256"
    "sha512"
    "sha3"
    "blake2b"
    "crc32"
    "crc32c"

    "btrfs"
    "loop"
    "v4l2loopback"
#    "xe"
    "mtd_intel_dg"
#    "snd_hda_intel"
#    "drm"
#    "i915"
    "efi_pstore"
    "configfs"
    "amd64_edac"
    "sr_mod"
    "igb"
    "btusb"
  ];

  # load in 2nd stage after root file system has been mounted.
  boot.kernelModules = [
    "nvme"
    "mpt3sas"
    "dm_log"
    "dm_cache"
    "dm_cache_smq"
    "dm_thin_pool"
    "dm_raid"
    "dm_integrity"
    "dm_snapshot"

    # These are reported as "builtin", however btrfs documentation states:
    # > Many kernels are configured with SHA256 as built-in and not as a module. The accelerated versions are however provided by the modules and must be loaded explicitly (modprobe sha256) before mounting the filesystem to make use of them. You can check in /sys/fs/btrfs/FSID/checksum which one is used. If you see sha256-generic, then you may want to unmount and mount the filesystem again.
    # Therefore listing these here explicitly just in case.
    "sha256"
    "sha512"
    "sha3"
    "blake2b"
    "crc32"
    "crc32c"

    "btrfs"
    "loop"
    "v4l2loopback"
#    "xe"
    "mtd_intel_dg"
#    "snd_hda_intel"
#    "drm"
#    "i915"
    "efi_pstore"
    "configfs"
    "amd64_edac"
    "sr_mod"
    "igb"
    "btusb"
  ];

  boot.blacklistedKernelModules = [
    "ast"
  ];

  boot.initrd.services.lvm.enable = true; # required for cache_check binary to be placed at correct location for mounting of LVM volumes with an attached cache volume.
  services.lvm.boot.thin.enable = true; # required for chache_check binary too.
  services.lvm.dmeventd.enable = true;
#  services.multipath.enable = true;

  # Cryptsetup and non-root filesystems
  # Improve performance on SSDs
  boot.initrd.luks.devices.luks-308704a3-5f1f-4cac-99ba-45d040ec57b9 = {
    # System partition
    allowDiscards = true;
    bypassWorkqueues = true;
    device = "/dev/disk/by-uuid/308704a3-5f1f-4cac-99ba-45d040ec57b9";
  };
  boot.initrd.luks.devices.luks-57e4edfa-54c0-4b4a-985a-adbe5fd6d497 = {
    # swap
    allowDiscards = false;
    bypassWorkqueues = true;
    device = "/dev/disk/by-uuid/57e4edfa-54c0-4b4a-985a-adbe5fd6d497";
  };
  # See https://github.com/NixOS/nixpkgs/issues/459869
  environment.etc.crypttab = let disk = {
    # raid 5 data partition
    # /dev/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5
    name = "luks-019cf9c3-7ed8-70e1-bb36-35e054c42b12";
    uuid = "4f1280e8-83f9-4dad-911f-07bf124d5ef0";
  }; in {
    text = ''
      ${disk.name} UUID=${disk.uuid} none luks,timeout=10s
    '';
  };

  fileSystems."/".options = [
    "lazytime"
    "strictatime"
  ];
  fileSystems."/mnt/FA380FB6380F7145" = {
    device = "/dev/disk/by-uuid/FA380FB6380F7145";
    fsType = "ntfs-3g";
    options = [
      "nofail"
      "lazytime"
      "strictatime"
      "nodev"
      "nosuid"
      ("uid=" + (toString config.users.users.user.uid))
      ("gid=" + (toString config.users.groups.users.gid))
    ];
  };
  fileSystems."/mnt/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5" = {
    device = "/dev/disk/by-uuid/2df491cf-b27c-41cd-ad88-2aa2b25278ca";
    encrypted = {
      blkDev = "/dev/disk/by-uuid/4f1280e8-83f9-4dad-911f-07bf124d5ef0";
      enable = true;
      label = "luks-019cf9c3-7ed8-70e1-bb36-35e054c42b12";
    };
    fsType = "btrfs";
    options = [
      "nofail"
      "lazytime"
      "strictatime"
      "nodev"
      "noexec"
      "nosuid"
    ];
  };

  # Use latest kernel.
  # Use "nix repl" > ":l <nixpkgs>" > "pkgs.linuxPackages" [press tab twice to show all linux Packages]
  # To show the kernel version of one of them add ".kernel" to the of their full package name.
  boot.kernelPackages = pkgs.linuxPackages_latest;
#  boot.kernelPackages = pkgs.linuxPackages_testing;


  # Enable networking
  networking.hostName = "PC-001"; # Define your hostname.
  #networking.wireless.enable = true;  # Enables wireless support via wpa_supplicant.
  # Configure network proxy if necessary
  #networking.proxy.default = "http://user:password@proxy:port/";
  #networking.proxy.noProxy = "127.0.0.1,localhost,internal.domain";
  #networking.networkmanager.enable = true;
  virtualisation.vswitch.resetOnStart = true;
  virtualisation.vswitch.enable = true;
  virtualisation.vswitch.package = pkgs.openvswitch-dpdk;
  networking.nftables.enable = true;
  networking.useNetworkd = true;
  networking.networkmanager.enable = false;

  # Systemd networkd generates the IPv6-LL IPs. So disable the kernels generation for them here
  boot.kernel.sysctl."net.ipv6.conf.all.addr_gen_mode" = 1;
  boot.kernel.sysctl."net.ipv6.conf.default.addr_gen_mode" = 1;
  boot.kernel.sysctl."net.ipv6.conf.lo.addr_gen_mode" = 1; # Except for lo
  boot.kernel.sysctl."net.ipv6.conf.eno1.addr_gen_mode" = 1; # Openvswitch managed, no IPs on here
  boot.kernel.sysctl."net.ipv6.conf.eno2.addr_gen_mode" = 1; # Openvswitch managed, no IPs on here
  boot.kernel.sysctl."net.ipv6.conf.ovs-system.addr_gen_mode" = 1; # Openvswitch managed, no IPs on here
  boot.kernel.sysctl."net.ipv6.conf.ovsbr0.addr_gen_mode" = 1; # Openvswitch managed, no IPs on here
  boot.kernel.sysctl."net.ipv6.conf.vlan10.addr_gen_mode" = 0; # Keep default here for now.
#  boot.kernel.sysctl."net.ipv6.conf.vlan20.addr_gen_mode" = 0; # Keep default here for now.
  boot.kernel.sysctl."net.ipv6.conf.vlan30.addr_gen_mode" = 1; # IPv4 only testnet, so disable ipv6 ll

  # PMTU
  boot.kernel.sysctl."net.ipv4.ip_forward_use_pmtu" = 1;
  boot.kernel.sysctl."net.ipv4.tcp_mtu_probing" = 1; # PMTU black hole detection

  # Enable ECN for outgoing connections
  boot.kernel.sysctl."net.ipv4.tcp_ecn" = 1; # Control use of Explicit Congestion Notification (ECN) by TCP. ECN is used only when both ends of the TCP connection indicate support for it. This feature is useful in avoiding losses due to congestion by allowing supporting routers to signal congestion before having to drop packets. Possible values are: 0 Disable ECN. Neither initiate nor accept ECN. 1 Enable ECN when requested by incoming connections and also request ECN on outgoing connection attempts. 2 Enable ECN when requested by incoming connections but do not request ECN on outgoing connections. Default: 2



  systemd.network.links."10-eno1" = {
    enable = true;
    matchConfig = {
      PermanentMACAddress = "ac:1f:6b:eb:32:02";
    };
    linkConfig = {
      Name = "eno1";
#      MacAddressPolicy = "persistent";
    };
  };
  systemd.network.links."10-eno2" = {
    enable = true;
    matchConfig = {
      PermanentMACAddress = "ac:1f:6b:eb:32:03";
    };
    linkConfig = {
      Name = "eno2";
#      MacAddressPolicy = "persistent";
    };
  };
#  systemd.network.links."40-vlan10" = {
#    enable = true;
#    matchConfig = {
#      name = "vlan10";
#    };
#    linkConfig = {
#      MacAddressPolicy = "persistent";
#    };
#  };
  systemd.network.networks."40-eno1".matchConfig = {
    Name = "eno1";
  };
  systemd.network.networks."40-eno1".linkConfig = {
#    Unmanaged = true;
    ActivationPolicy = "up";
    RequiredForOnline = "no";
    Multicast = true;
  };
  systemd.network.networks."40-eno1".networkConfig = lib.mkForce {
    DHCP = "no";
    MulticastDNS = "no";
    LLMNR = "no";
    LinkLocalAddressing = "no";
    LLDP = "yes";
    EmitLLDP = "nearest-bridge";
    KeepMaster = "yes";
  };
  systemd.network.networks."40-eno2".matchConfig = {
    Name = "eno2";
  };
  systemd.network.networks."40-eno2".linkConfig = {
#    Unmanaged = true;
    ActivationPolicy = "up";
    RequiredForOnline = "no";
    Multicast = true;
  };
  systemd.network.networks."40-eno2".networkConfig = lib.mkForce {
    DHCP = "no";
    MulticastDNS = "no";
    LLMNR = "no";
    LinkLocalAddressing = "no";
    LLDP = "yes";
    EmitLLDP = "nearest-bridge";
    KeepMaster = "yes";
  };
  systemd.network.networks."00-ovs-system".matchConfig = {
    Name = "ovs-system";
    Kind = "openvswitch";
    Driver = "openvswitch";
  };
  systemd.network.networks."00-ovs-system".linkConfig = {
#    Unmanaged = true;
    ActivationPolicy = "always-down";
    RequiredForOnline = "no";
  };
  systemd.network.networks."00-ovs-system".networkConfig = lib.mkForce {
    DHCP = "no";
    MulticastDNS = "no";
    LLMNR = "no";
    LinkLocalAddressing = "no";
    LLDP = "no";
    EmitLLDP = "no";
  };
  systemd.network.networks."60-ovsbr0".matchConfig = {
    Name = "ovsbr0";
    Kind = "openvswitch";
    Driver = "openvswitch";
  };
  systemd.network.networks."60-ovsbr0".linkConfig = {
#    Unmanaged = true;
    ActivationPolicy = "always-down";
    RequiredForOnline = "no";
  };
  systemd.network.networks."00-ovsbr0".networkConfig = lib.mkForce {
    DHCP = "no";
    MulticastDNS = "no";
    LLMNR = "no";
    LinkLocalAddressing = "no";
    LLDP = "no";
    EmitLLDP = "no";
  };
  systemd.network.networks."40-vlan10".matchConfig = {
    Name = "vlan10";
  };
  systemd.network.networks."40-vlan10".linkConfig = {
#    Unmanaged = true;
    ActivationPolicy = "up";
    RequiredForOnline = "yes";
    Multicast = true;
  };
  systemd.network.networks."40-vlan10".networkConfig = lib.mkForce {
    DHCP = "yes";
    MulticastDNS = "yes";
    DNSDefaultRoute = "yes";
    IPv6AcceptRA = "yes";
    IPv6DuplicateAddressDetection = 1;
    IPv6PrivacyExtensions = "kernel";
    LLDP = "yes";
    EmitLLDP = "nearest-bridge";
  };
  systemd.network.networks."40-vlan10".dhcpV4Config = {
    Anonymize = "yes";
    UseDNS = "yes";
    UseNTP = "yes";
    UseDomains = "no";
    UseHostname = "no";
    UseRoutes = "yes";
  };
  systemd.network.networks."40-vlan10".dhcpV6Config = {
    UseDNS = "yes";
    UseNTP = "yes";
    UseHostname = "no";
    UseDomains = "no";
  };
  systemd.network.networks."40-vlan30".matchConfig = {
    Name = "vlan30";
  };
  systemd.network.networks."40-vlan30".linkConfig = {
#    Unmanaged = true;
#    ActivationPolicy = "up";
    ActivationPolicy = "down";
    RequiredForOnline = "no";
    Multicast = false;
  };
  systemd.network.networks."40-vlan30".networkConfig = lib.mkForce {
    DHCP = "ipv4";
    MulticastDNS = "no";
    DNSDefaultRoute = "no";
    IPv6AcceptRA = "no";
    IPv6SendRA = "no";
    IPv6LinkLocalAddressGenerationMode = "none";
    LinkLocalAddressing = "no";
    LLDP = "yes";
    EmitLLDP = "nearest-bridge";
    LLMNR = "no";
  };
  systemd.network.networks."40-vlan30".dhcpV4Config = {
    Anonymize = "yes";
    UseDNS = "no";
    UseNTP = "no";
    UseSIP = "no";
    UseDomains = "no";
    UseHostname = "no";
    UseGateway = "no";
    UseTimezone = "no";
  };
  networking.vswitches.ovsbr0 = {
    interfaces = {
      eno1 = {
        vlan = 10;
        name = "eno1";
      };
      eno2 = {
        vlan = 11;
        name = "eno2";
      };
      vlan10 = {
        name = "vlan10";
        type = "internal";
        vlan = 10;
      };
      vlan30 = {
        name = "vlan30";
        type = "internal";
        vlan = 30;
      };
    };
  };

  services.resolved = {
    enable = true;
    settings.Resolve = {
      DNSSEC = false;
      DNSOverTLS = "opportunistic";
      LLMNR = false;
      MulticastDNS = true;
      Cache = true;
      DNSStubListener = true;
      ReadEtcHosts = true;
    };
  };

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

  # Open ports in the firewall.
  # networking.firewall.allowedTCPPorts = [ ... ];
  # networking.firewall.allowedUDPPorts = [ ... ];
  # Or disable the firewall altogether.
  # networking.firewall.enable = false;

  hardware.i2c.enable = true;
  hardware.ledger.enable = true;
  hardware.infiniband.enable = true;
  hardware.gpgSmartcards.enable = true;
  hardware.steam-hardware.enable = true;
  hardware.sensor.hddtemp.enable = true;
  hardware.sensor.hddtemp.drives = [ "/dev/disk/by-path/*" ];
#  hardware.amdgpu = {
#    amdvlk.enable = true;
#    opencl.enable = true;
#    initrd.enable = true;
#  }
  hardware.intel-gpu-tools.enable = true;
  hardware.cpu.intel.updateMicrocode = true;
  hardware.cpu.intel.sgx.provision.enable = true;
  hardware.cpu.amd.updateMicrocode = true;
  hardware.cpu.amd.sev.enable = true;
  hardware.cpu.amd.sevGuest.enable = true;
  hardware.cpu.x86.msr.enable = true;
  hardware.cpu.x86.msr.settings.allow-writes = "on";
  hardware.bluetooth.enable = true;
  hardware.enableAllFirmware = true;
#  hardware.enableAllHardware = true;
  services.switcherooControl.enable = true;

  # XBOX controller
#  hardware.xpadneo.enable = true;
#  hardware.xone.enable = true;

  hardware.graphics = {
    enable = true;
    enable32Bit = true;
    extraPackages = with pkgs; [
      intel-vaapi-driver # LIBVA_DRIVER_NAME=i965 (older but works better for Firefox/Chromium)
      intel-media-driver # LIBVA_DRIVER_NAME=iHD
      libvdpau-va-gl
      libva-vdpau-driver
      mesa
      intel-ocl
      vpl-gpu-rt
    ];
    extraPackages32 = with pkgs.pkgsi686Linux; [
      intel-vaapi-driver
      intel-media-driver
      mesa
    ];
  };

  nixpkgs.config.packageOverrides = pkgs: {
    intel-vaapi-driver = pkgs.intel-vaapi-driver.override { enableHybridCodec = true; };
  };

  # Set your time zone.
  time.timeZone = "Europe/Berlin";

  # Select internationalisation properties.
  i18n.defaultLocale = "en_US.UTF-8";

  i18n.extraLocaleSettings = {
    LC_ADDRESS = "de_DE.UTF-8";
    LC_IDENTIFICATION = "de_DE.UTF-8";
    LC_MEASUREMENT = "de_DE.UTF-8";
    LC_MONETARY = "de_DE.UTF-8";
    LC_NAME = "de_DE.UTF-8";
    LC_NUMERIC = "de_DE.UTF-8";
    LC_PAPER = "de_DE.UTF-8";
    LC_TELEPHONE = "de_DE.UTF-8";
    LC_TIME = "de_DE.UTF-8";
  };

  # Enable the KDE Plasma Desktop Environment.
  #services.displayManager.gdm.enable = true;
  #services.displayManager.gdm.wayland = true;
  #services.displayManager.gdm.autoSuspend = false;
  #services.desktopManager.gnome.enable = true;
  #services.desktopManager.gnome.flashback.enableMetacity = true;

  services.displayManager.sddm.enable = true;
  services.displayManager.sddm.wayland.enable = true;
  services.displayManager.sddm.wayland.compositor="weston";
  #services.displayManager.sddm.wayland.compositor = "kwin"; # Default "weston"
  services.displayManager.sddm.autoNumlock = true;
  services.desktopManager.plasma6.enable = true;

  services.xserver.enable = false;
  programs.xwayland.enable = true;

  # Configure keymap in X11
  services.xserver.xkb = {
    layout = "us";
    variant = "";
  };

  # Enable CUPS to print documents.
  services.printing.enable = true;

  # Enable sound with pipewire.
  services.pulseaudio.enable = false;
  security.rtkit.enable = true;
  services.pipewire = {
    enable = true;
    alsa.enable = true;
    alsa.support32Bit = true;
    pulse.enable = true;
    # If you want to use JACK applications, uncomment this
    #jack.enable = true;

    # use the example session manager (no others are packaged yet so this is enabled by default,
    # no need to redefine it in your config for now)
    #media-session.enable = true;
  };

  # Enable touchpad support (enabled default in most desktopManager).
  services.libinput.enable = true;

  # UniFi
  services.unifi = {
    enable = false;
    openFirewall = true;
    initialJavaHeapSize = 4096;
    unifiPackage = nixos-unstable.pkgs.unifi; # nixos-25.11's unifi package is marked as insecure
    jrePackage = pkgs.jdk25_headless;
  };

  # Define a user account. Don't forget to set a password with ‘passwd’.
#  users.mutableUsers = false; # Overwrite all changes to users from outside of the nix configuration. Including passwords.
  users.users.root =  {
    subGidRanges = lib.mkForce [
      {
        count = 65536;
        startGid = (config.users.groups.${config.users.users.root.group}.gid + 1) * 65536;
      }
    ];
    subUidRanges = lib.mkForce [
      {
        count = 65536;
        startUid = (config.users.users.root.uid + 1) * 65536;
      }
    ];
  };
  users.users.user = {
    isNormalUser = true;
    linger = true; # Allow starting systemd user units at boot before login.
    description = "user";
    pamMount = {
      # Example: <volume user="test" fstype="tmpfs" mountpoint="/home/test" options="size=10M,uid=%(USER),mode=0700" />
      fstype = "tmpfs";
      mountpoint = "~/.cache";
      options = "uid=%(USER),mode=0755";
    };
    extraGroups = [
#      "networkmanager"
      "wheel"
      "tss" # tss group has access to TPM devices
#      config.services.kubo.group
      "libvirtd"
#      config.systemd.sockets.podman.socketConfig.SocketGroup
      "wireshark"
    ];
    uid = 1000;
    group = config.users.groups.users.name;
    subGidRanges = [
      {
        count = 65536;
        # Gid 0 == root system. So in order for the group "root" to have their own subGidRange
        # that doesn't overlay with the systems primary Gid range we add 1 to the gid.
        # then we multiply it with 65536 to allow each group to have their own range.
        # With systemd-homed the allocatable range is 524288-1878982656 (0x80000-0x6fff0000).
        startGid = (config.users.groups.${config.users.users.user.group}.gid + 1) * 65536;
      }
    ];
    subUidRanges = [
      {
        count = 65536;
        # Uid 0 == root system. So in order for the user "root" to have their own subUidRange
        # that doesn't overlay with the systems primary Uid range we add 1 to the gid.
        # then we multiply it with 65536 to allow each group to have their own range.
        # With systemd-homed the allocatable range is 524288-1878982656 (0x80000-0x6fff0000).
        startUid = (config.users.users.user.uid + 1) * 65536;
      }
    ];
    openssh.authorizedKeys.keys = [
      "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHodj/qhnLC0Mi30toyyP0U3NqGt5/UwhAOJl4fVORam"
      "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK+KT+HKGbv01SikaVhvk4ZG8B7e/igvlpKpsEqf90u5"
      "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBGL5o6+ly03jlVmOeS3nhg54S/3+8oLYHdA3G9SueqZ7r29BaFgr/UY7S8oITdKld/ZStSqBzL2WWR+rTTEsq7RFScgtx7FT2OiCymednZbnf1aV6c921osLAi8ST8I7sQ==" # YubiKey with USB-A on Keychain
#      "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJd49OgA6nLFYiiPYsVtQpjeyrfRezZ1aI04NItkDJ+Z eddsa-key-20260403" # Fujitsu siemens mini pc
    ];
    packages = with pkgs; [
#      kdePackages.kate
#      kdePackages.wacomtablet
      kdePackages.skanpage
#      kdePackages.xwaylandvideobridge
      kdePackages.partitionmanager
      kdePackages.kruler
      kdePackages.krfb
      kdePackages.krdc
      kdePackages.kpkpass
      kdePackages.kolourpaint
      kdePackages.kfind
      kdePackages.kdf
      kdePackages.kdenlive
      kdePackages.kcalc
      kdePackages.flatpak-kcm
      kdePackages.filelight
##      kdePackages.full
#      kdePackages.ffmpegthumbs
#      kdePackages.bluedevil
      aria2
      yt-dlp
      gparted
      vivaldi
      vivaldi-ffmpeg-codecs
      nixos-25-11.lutris
      wget
      wget2
      lgogdownloader
      handbrake
      python313Packages.xattr
      keepassxc
      google-chrome
      librewolf
      firefox-devedition
      inkscape-with-extensions
      libreoffice-qt
      hunspell
      hunspellDicts.en_US
      hunspellDicts.de_DE
      mosh
      screen
      onlyoffice-desktopeditors #formerly onlyoffice-bin
      prismlauncher  # MultiMC successor
      tor-browser
      obsidian
#      dolphin-emu
      arduino-ide
      arduino-cli
      podman-compose
      protonup-qt
      brave
      keepassxc
      vlc
      wireshark
      pwgen
      nixos-25-05.gupnp-tools
      nixpkgs-review
      filezilla
      signal-desktop
      signal-cli
      qrencode
      devenv
      dua # Filelight on the CLI
      biglybt # nixos-unstable.biglybt
      httrack
      rclone
    ];
  };


  # List packages installed in system profile. To search, run:
  # $ nix search wget
  environment.systemPackages = with pkgs; [
    asciinema
    smartmontools # nixos-unstable.smartmontools
    libva-utils
    powershell
    usbutils
    pciutils
    winetricks
    wineWow64Packages.waylandFull
    (ffmpeg-full.override { withUnfree = true; })
    fwupd
    htop
    killall
    tree
    file
    sshfs-fuse
    mesa-demos #formerly glxinfo
    vulkan-tools
    libva-utils
    vdpauinfo
    clinfo
    mesa
#    nvtopPackages.amd
    nvtopPackages.intel
#    nvtopPackages.msm
#    nvtopPackages.panfrost
#    nvtopPackages.panthor
#    nvtopPackages.v3d
    btrfs-progs
    openssl
    pkg-config
    mt-st
    mtx
    lsscsi
    efibootmgr
    busybox #needed for e.g. "strings" command
    mergerfs
    mergerfs-tools
    virtiofsd # libvirtd/kvm

    thin-provisioning-tools # trying to mount a LVM volume with a dm-cache throws a warning of missing the cache_check binary.
    # /usr/sbin/cache_check: execvp failed: No such file or directory
    # WARNING: Check is skipped, please install recommended missing binary /usr/sbin/cache_check!
    ptouch-driver
    ptouch-print
    foomatic-db-ppds-withNonfreeDb
    bees
  ];
  environment.sessionVariables = {
    RUSTICL_FEATURES = "fp16,fp64";
    RUSTICL_ENABLE = "llvmpipe,iris,nouveau";
    VKD3D_CONFIG = "dxr11,dxr";
    QT_SELECT = "6";
    QT_QPA_PLATFORM = "wayland";
    QT_AUTO_SCREEN_SCALE_FACTOR = "1";
    QT_ENABLE_HIGHDPI_SCALING = "1";
    CLUTTER_BACKEND = "wayland";
    SDL_VIDEODRIVER = "wayland";
    XDG_SESSION_TYPE = "wayland";
    ANV_DEBUG = "video-decode,video-encode"; # Intel graphics Vulkan https://wiki.archlinux.org/title/Hardware_video_acceleration#Configuring_Vulkan_Video
    LIBVA_DRIVER_NAME = "iHD";
    VDPAU_DRIVER = "va_gl";
#    __EGL_VENDOR_LIBRARY_FILENAMES = "/nix/store/6gknxbslzfy5hyvl9pqzg6wp48xsbl2r-mesa-25.0.7/share/glvnd/egl_vendor.d/50_mesa.json";
    __EGL_VENDOR_LIBRARY_FILENAMES = "/run/opengl-driver/share/glvnd/egl_vendor.d/50_mesa.json";
    __NV_PRIME_RENDER_OFFLOAD = 1;
    __GLX_VENDOR_LIBRARY_NAME = "intel"; # or ="mesa"
    VK_ICD_FILENAMES = pkgs.mesa.outPath + "/share/vulkan/icd.d/intel_icd.x86_64.json";
  };
  fonts.packages = [
    pkgs.dejavu_fonts
    pkgs.liberation_ttf
    pkgs.open-sans
    pkgs.vista-fonts
    pkgs.open-fonts
    pkgs.texlivePackages.collection-fontsrecommended
    pkgs.texlivePackages.collection-fontsextra
    pkgs.texlivePackages.ocr-b
    pkgs.texlivePackages.noto-emoji
    pkgs.texlivePackages.barcodes
    pkgs.texlivePackages.sansmathfonts
    pkgs.nerd-fonts.monaspace
    pkgs.nerd-fonts.dejavu-sans-mono
    pkgs.dosemu_fonts
    pkgs.google-fonts
    pkgs.corefonts # Microsofts TrueType Core Fonts

  ];
  fonts.fontconfig.cache32Bit = true;
  fonts.fontconfig.hinting.style = "full";
  fonts.fontconfig.useEmbeddedBitmaps = true;

  programs.mosh.enable = true;
  programs.ssh.extraConfig = "
  Host *
    VerifyHostKeyDNS yes
    CheckHostIP yes
    StrictHostKeyChecking accept-new
    ForwardAgent no
    ControlMAster auto
    ControlPersist 60s
    PreferredAuthentications publickey
    ServerAliveInterval 11
    HashKnownHosts no

    Host github.com
      HostName github.com
      Port 22
      User git

    Host git@gitlab.agowa338.de
      HostName gitlab.agowa338.de
      IdentityFile ~/.ssh/id_ed25519
      User git

    Host *
      Port 2222
      User root

  ";
  programs.kdeconnect.enable = false;
#  programs.kdeconnect.enable = true;
  programs.k3b.enable = true;
  programs.mtr.enable = true;
  programs.firefox.enable = true;
  programs.thunderbird.enable = true;
  programs.chromium.enable = true;
  programs.steam.enable = true;
  programs.steam.protontricks.enable = true;
  programs.steam.localNetworkGameTransfers.openFirewall = true;
  programs.steam.extest.enable = true; # For Steam inputs on Wayland
  programs.steam.extraCompatPackages = with pkgs; [
    proton-ge-bin
  ];
  programs.steam.package = pkgs.steam.override {
    extraEnv = {
#      MANGOHUD = true;
      OBS_VKCAPTURE = true;
      RADV_TEX_ANISO = 16;
      VK_ICD_FILENAMES = pkgs.driversi686Linux.mesa.outPath + "/share/vulkan/icd.d/intel_icd.i686.json" + ":" + pkgs.mesa.outPath + "/share/vulkan/icd.d/intel_icd.x86_64.json";
    };
    extraLibraries = p: with p; [
#      atk
#      gamemode
#      vkd3d
#      vulkan-hdr-layer-kwin6
#      vulkan-extension-layer
#      vulkan-memory-allocator
#      dxvk_2
#      mangohud
#      vkd3d-proton
      mesa
      intel-vaapi-driver #.override { enableHybridCodec = true; }
      intel-media-driver
      libva-vdpau-driver
      libvdpau-va-gl
      vdpauinfo
      driversi686Linux.mesa
      driversi686Linux.intel-vaapi-driver #.override { enableHybridCodec = true; }
      driversi686Linux.intel-media-driver
      driversi686Linux.libva-vdpau-driver
      driversi686Linux.libvdpau-va-gl
      driversi686Linux.vdpauinfo
    ];
  };
  programs.gamemode.enable = true;
  programs.virt-manager.enable = true;
  programs.vim = {
    enable = true;
    package = pkgs.vim-full;
    defaultEditor = true;
  };
  programs.gnupg.agent = {
    enable = true;
    enableSSHSupport = true;
  };
  programs.git = {
    enable = true;
    lfs = {
      enable = true;
      enablePureSSHTransfer = true;
    };
    package = pkgs.gitFull.override { withLibsecret = true; };
    config = {
      credential.helper = "libsecret";
      user = {
        email = "git@frank.fyi";
        name = "Klaus Frank";
        signingkey = "E21BE845B37D95CBB18375EE2122D9726719FE40";
      };
      init.defaultBranch = "master";
      commit.gpgSign = true;
      push = {
        autoSetupRemote = true;
        recurseSubmodules = "on-demand";
      };
      core = {
        abbrev = 12;
        preloadIndex = true;
      };
      pretty.fixes = "Fixes: %h (\\\"%s\\\")";
      diff.renames = "copies";
      fetch = {
        writeCommitGraph = true;
        fsckObjects = true;
        parallel = 0;
      };
      log.date = "iso";
      pull.ff = true;
      status.showStash = true;
      tag.forceSignAnnotated = true;
      checkout.workers = -1;
    };
    prompt.enable = true;
  };
  programs.tmux = {
    enable = true;
    terminal = "screen-256color";
    clock24 = true;
    aggressiveResize = true;
  };
  programs.java = {
    enable = true; # This installs jdk and sets JAVA_HOME.
    binfmt = true;
    package = pkgs.jdk;
  };

  programs.obs-studio = {
    enable = true;
    enableVirtualCamera = true;

#    # optional Nvidia hardware acceleration
#    package = (
#      pkgs.obs-studio.override {
#        cudaSupport = true;
#      }
#    );

    plugins = with pkgs.obs-studio-plugins; [
      wlrobs
      obs-backgroundremoval
      obs-pipewire-audio-capture
      obs-vaapi #optional AMD hardware acceleration
      obs-gstreamer
      obs-vkcapture
    ];
  };

  programs.wireshark.enable = true;
  programs.wireshark.dumpcap.enable = true;

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
    reseed.file = "/etc/nixos/i2pseeds.su3"; # wget --user-agent="Wget/1.11.4" https://reseed.stormycloud.org/i2pseeds.su3
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
  networking.firewall.allowedUDPPortRanges = [
  ] ++ config.services.aria2.settings.listen-port;
  networking.firewall.allowedUDPPorts = [
    config.services.i2pd.port
#    5353 # mDNS
    5350 # NAT-PMP
    # <BiglyBT>
    27151
    27152 # temporary for testing only
    28549
    29605
    49001
    6969
    # </BiglyBT>
  ];
  networking.firewall.allowedTCPPorts = [
    # <BiglyBT>
    27151
    8080
    29605
    28549
    6969
    7000
    # </BiglyBT>
  ];
  networking.firewall.enable = true;
  networking.firewall.logRefusedPackets = true;
  networking.firewall.logRefusedUnicastsOnly = false;
  networking.firewall.logReversePathDrops = false;
    #ipv4 udp sport 1900 saddr 192.168.178.1 accept
    #ip saddr 192.168.178.1 accept
  networking.firewall.extraInputRules = ''
    ip saddr 192.168.178.0/24 udp sport 1900 udp dport >= 1024 meta pkttype unicast limit rate 4/second burst 20 packets accept comment "Accept UPnP IGD port mapping reply"
    ip protocol igmp accept comment "Accept IGMP"
    udp dport mdns ip6 daddr ff02::fb accept comment "Accept mDNS"
    udp dport mdns ip daddr 224.0.0.251 accept comment "Accept mDNS"
    pkttype { broadcast,multicast} accept
  '';

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
