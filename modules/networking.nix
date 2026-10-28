{ config, lib, pkgs, inputs, ... }:

{
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
}
