{ config, lib, pkgs, inputs, ... }:

{
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
}
