{ config, lib, pkgs, inputs, ... }:

{
  services.tor = {
    enable = true;
    relay = {
      enable = true;
      role = "bridge";

      # This node's monerod on Tor - one onion service carrying both endpoints that Monero
      # documents for an anonymity network (https://docs.getmonero.org/running-node/monerod-tori2p/):
      #
      #   18084 -> 127.0.0.1:18084   P2P, monerod's anonymous-inbound listener
      #   18089 -> 127.0.0.1:18089   restricted RPC, for wallets connecting over Tor
      #
      # The P2P port is not the clearnet one (18080): monerod only speaks handshakes, timed syncs
      # and transaction relay over anonymity networks, so it opens a separate listener for them.
      # Both sides of that pairing - this mapping and monerod's own anonymous-inbound/tx-proxy
      # options - are wired up in modules/services.nix, which also explains why the ports differ
      # from each other per network.
      #
      # tor creates the v3 name and its key pair on first start:
      #   sudo cat /var/lib/tor/onion/monerod/hostname
      # That address identifies this node, so it is not written here or in modules/services.nix;
      # it goes into secrets/monero.yaml as sops.secrets."monero/tor_hostname" like every other
      # secret in this repo, and monerod reads it from /run/secrets when it starts. Back up that
      # directory if the address matters to you - deleting it changes the name.
      #
      # nixpkgs warns about this combination at evaluation time: an onion service inside a *relay*
      # can be spotted by statistical analysis of the relay's own traffic
      # (trac.torproject.org/ticket/8742). This host is a bridge, so the endpoints are anonymously
      # reachable but not unfindable; that is enough for wallets and for transaction relay.
      onionServices."monerod" = {
        version = 3;
        map = [
          {
            port = 18084; # monerod anonymous-inbound (P2P over Tor)
            target = {
              addr = "127.0.0.1"; # monerod binds this itself, see modules/services.nix
              port = 18084;
            };
          }
          {
            port = 18089; # restricted RPC for wallets (rpc-restricted-bind-port)
            target = {
              addr = "127.0.0.1"; # monerod's rpc-restricted-bind-ip=0.0.0.0 also answers on loopback
              port = 18089;
            };
          }
        ];
      };
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
