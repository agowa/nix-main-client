{ config, lib, pkgs, inputs, ... }:

{
  # List packages installed in system profile. To search, run:
  # $ nix search wget
  environment.systemPackages = with pkgs; [
    asciinema
    smartmontools # nixos-unstable.smartmontools
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
    bees
    p7zip
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
}
