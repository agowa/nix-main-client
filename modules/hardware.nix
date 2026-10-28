{ config, lib, pkgs, inputs, ... }:

{
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
}
