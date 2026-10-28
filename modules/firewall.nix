{ config, lib, pkgs, inputs, ... }:

{
  # Open ports in the firewall.
  # networking.firewall.allowedTCPPorts = [ ... ];
  # networking.firewall.allowedUDPPorts = [ ... ];
  # Or disable the firewall altogether.
  # networking.firewall.enable = false;

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
    config.services.i2pd.port
    18080 # monerod
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
}
