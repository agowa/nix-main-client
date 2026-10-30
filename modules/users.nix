{ config, lib, pkgs, inputs, ... }:

{
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
      lutris
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
      vlc
      wireshark
      pwgen
      gupnp-tools
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
      monero-gui
      element-desktop

#      (pkgs.lmstudio.overrideAttrs (old: {
#        propagatedBuildInputs = (old.propagatedBuildInputs or []) ++ [
#          pkgs.uv
#          pkgs.python3Packages.fastmcp
#        ];
#      }))
      (pkgs.symlinkJoin {
        name = "lmstudio-with-mcp";
        paths = [ pkgs.lmstudio ];
        nativeBuildInputs = [ pkgs.makeWrapper ];
        postBuild = ''
          wrapProgram $out/bin/lm-studio \
            --prefix PATH : ${pkgs.lib.makeBinPath [
              pkgs.uv
              (pkgs.python3.withPackages (ps: [
                ps.fastmcp
              ]))
            ]}
        '';
      })
    ];
  };
}
