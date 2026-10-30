{ config, lib, pkgs, inputs, ... }:

{
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
}
