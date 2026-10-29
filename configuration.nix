# Edit this configuration file to define what should be installed on
# your system.  Help is available in the configuration.nix(5) man page
# and in the NixOS manual (accessible by running ‘nixos-help’).

{ config, lib, pkgs, inputs, ... }:

{
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
      ./modules/virtualisation.nix
      ./modules/services.nix
      ./modules/i2pd.nix
      ./modules/tor.nix
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

