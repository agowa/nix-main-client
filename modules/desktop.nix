{ config, lib, pkgs, inputs, ... }:

{
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
  services.printing = {
    enable = true;
    drivers = with pkgs; [
      ptouch-driver
#      ptouch-print
      foomatic-db-ppds-withNonfreeDb
    ];
  };
  hardware.printers = {
    ensurePrinters = [
#      {
#        name = "Samsung_C48x_Series_TEST";
#        location = "Home";
#        deviceUri = "socket://sec30cda7a5b2de.local";
#        model = "Samsung-C48x-Postscript-Samsung.ppd.gz";
#        ppdOptions = {
#          PageSize = "A4";
#        };
#      }
#      {
#        name = "Brother_QL-500_TEST";
#        location = "Home";
#        deviceUri = "usb://Brother/QL-500?serial=B2G671060";
#        model = "Brother-QL-500-ptouch-ql.ppd.gz";
#        ppdOptions = {};
#      }
    ];
    ensureDefaultPrinter = "Samsung_C48x_Series_TEST";
  };

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
}
