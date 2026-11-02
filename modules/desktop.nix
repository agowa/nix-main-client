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
      # Brother QL-500 label printer: provides the ptouch PPDs used below and
      # the `rastertoptch` filter that those PPDs invoke.
      ptouch-driver
#      ptouch-print
      # Samsung C48x Series: provides Samsung_C48x_Series.ppd.gz, i.e. the
      # "Samsung C48x Series PS" PostScript PPD used below (binary/unfree).
      samsung-unified-linux-driver_1_00_36
      foomatic-db-ppds-withNonfreeDb
    ];
  };

  # Both queues are declared here so that every activation re-creates them; the
  # ensure-printers service runs
  #   lpadmin -p <name> -v <deviceUri> -m <model> [-L loc] [-D desc] [-o opt=val ...] -E
  # `model` has to be the first column of `lpinfo -m`, i.e. a path below the
  # `model` directory of CUPS' DataDir (the cups-progs tree assembled from
  # services.printing.drivers). The human readable "make and model" string that
  # `lpoptions -p <queue>` reports as printer-make-and-model is *not* accepted.
  # Re-passing `-m` on every switch re-copies the PPD out of the current nixpkgs
  # closure into /var/lib/cups/ppd, which matters because foomatic PPDs name
  # their filters with absolute store paths: after an update that path is gone
  # and the queue silently ends up paused with `cups-missing-filter-warning`
  # (that is what happened to the QL-500). `-E` clears the resulting disabled state.
  hardware.printers = {
    ensurePrinters = [
      {
        # lpinfo -m: Samsung_C48x_Series.ppd.gz -> "Samsung C48x Series PS"
        name = "Samsung_C48x_Series";
        description = "Samsung C48x Series";
        location = "Home";
        deviceUri = "socket://192.168.178.102"; # mDNS alias: sec30cda7a5b2de.local
        model = "Samsung_C48x_Series.ppd.gz";
        ppdOptions = {
          PageSize = "A4";
          # Remaining options of this PPD: ColorMode (False = grayscale),
          # Quality (Best|Normal), InputSlot, MediaType and the JCL ones
          # (JCLEconomode = toner save, JCLBlackOptimization).
        };
      }
      {
        # lpinfo -m: ptouch-driver/Brother-QL-500-ptouch-ql.ppd.gz
        name = "Brother_QL-500";
        description = "Brother QL-500";
        location = "Home";
        deviceUri = "usb://Brother/QL-500?serial=B2G671060";
        model = "ptouch-driver/Brother-QL-500-ptouch-ql.ppd.gz";
        # Values taken from the running queue (`lpoptions -p Brother_QL-500 -l`),
        # so switching to this declaration changes nothing; note that the PPD's
        # own defaults differ (PageSize 62mm, CutMark "Unknown").
        ppdOptions = {
          PageSize = "29x90mm"; # tape width / label format: 29mm, 50mm, 62mm, 62x100mm, ...
          MediaType = "Tape"; # continuous tape; "Labels" for die-cut labels
          PrintQuality = "High"; # or "Fast"
          PrintDensity = "0PrinterDefault"; # 1VeryLight .. 5VeryDark
          AutoEject = "True";
          CutMark = "True";
          ExtraMargin = "0mm"; # ignored for die-cut tape
          MirrorPrint = "False";
        };
      }
    ];
    ensureDefaultPrinter = "Samsung_C48x_Series";
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
