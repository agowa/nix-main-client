{ config, lib, pkgs, inputs, ... }:

let
  # This node's v3 onion identity, kept in the repository as a sops-encrypted file:
  # secrets/tor/monerod_hs_ed25519_secret_key. scripts/make-onion-key.sh creates it (either by
  # letting tor invent a key pair or by importing one that is already live), README.md
  # ("Provisioning an onion identity") says what to do with the address afterwards.
  #
  # The file holds exactly what tor keeps in <HiddenServiceDir>/hs_ed25519_secret_key: a 32-byte,
  # NUL-padded "== ed25519v1-secret: type0 ==" header followed by the 64-byte expanded ed25519 key
  # (crypto_write_tagged_contents_to_file() in tor's src/lib/crypt_ops/crypto_format.c, format
  # documented in Appendix G of https://spec.torproject.org/rend-spec). That is all that has to be
  # stored: on every start hs_service.c loads *only* this file, derives the public key from it with
  # ed25519_public_key_generate(), builds the address with hs_build_address() and rewrites both
  # `hs_ed25519_public_key` and `hostname`. Verified against tor 0.4.9: a directory containing only
  # this one file yields the same .onion name as the directory it came from.
  onionSecretKey = ../secrets/tor/monerod_hs_ed25519_secret_key;

  # While that file is missing, everything below behaves as before - tor creates a key pair on
  # first start and the address only exists on the machine. Committing it is therefore also the
  # switch that turns the pinned identity on, with nothing else to remember to flip.
  onionIdentityPinned = builtins.pathExists onionSecretKey;
in
{
  # `format = "binary"` is what makes this work at all: sops-nix then decrypts the whole file and
  # writes it out verbatim instead of extracting a string value from a YAML document - necessary,
  # because a tor token file carries NUL bytes and raw key material and could never be a YAML
  # value. Root-only: the nixpkgs module installs it from a `+`-prefixed (root) ExecStartPre.
  sops.secrets."tor/monerod_hs_ed25519_secret_key" = lib.mkIf onionIdentityPinned {
    sopsFile = onionSecretKey;
    format = "binary";
  };

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
      # `secretKey` is what pins the name: the module installs the file above as
      # <HiddenServiceDir>/hs_ed25519_secret_key before tor starts and moves the service directory
      # from /var/lib/tor/onion/monerod to /run/tor/onion/monerod, so the private key exists only in
      # tmpfs and is re-created from sops on every start instead of being born on this machine. The
      # address follows from the key alone, which means it can be written down in advance:
      #
      #   sudo cat /run/tor/onion/monerod/hostname     # must equal secrets/monero.yaml -> monero.tor_hostname
      #
      # Without the file in the repo tor keeps generating its own name under
      # /var/lib/tor/onion/monerod (deleting that directory changes such an address; a directory
      # left over from before the identity was pinned is unused and can go).
      #
      # nixpkgs warns about this combination at evaluation time: an onion service inside a *relay*
      # can be spotted by statistical analysis of the relay's own traffic
      # (trac.torproject.org/ticket/8742). This host is a bridge, so the endpoints are anonymously
      # reachable but not unfindable; that is enough for wallets and for transaction relay.
      onionServices."monerod" = {
        version = 3;

        secretKey = lib.mkIf onionIdentityPinned
          config.sops.secrets."tor/monerod_hs_ed25519_secret_key".path;

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
