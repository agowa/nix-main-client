{ config, lib, pkgs, inputs, ... }:

let
  # This node's I2P destination identity, kept in the repository as a sops-encrypted file:
  # secrets/i2p/monero_mainnet_keys.dat. scripts/make-i2p-key.sh creates it (either by letting
  # i2pd-tools invent a key set or by importing one that is already live), and README.md
  # ("Provisioning an I2P destination") says what to do with the address afterwards.
  #
  # The file holds exactly what i2pd keeps in <datadir>/monero-mainnet.dat: the raw PrivateKeys
  # buffer of one destination - signing key type, signing key pair and encryption key pair written
  # with PrivateKeys::ToBuffer(), which is also what i2pd-tools' `keygen` writes. That is all that
  # has to be stored: a ClientDestination reads this one file, and the .b32.i2p name is base32 over
  # the SHA-256 of the destination it contains, so it follows from these bytes alone. Checked with
  # the i2pd of the pinned nixpkgs rather than trusted: pointing a throwaway router at a directory
  # holding only this file makes it log "Clients: Local address <address> loaded" for the very name
  # `keyinfo` prints from the same bytes, and it leaves the file untouched.
  destinationKeys = ../secrets/i2p/monero_mainnet_keys.dat;

  # While that file is missing everything below still works - i2pd creates a key set on first start
  # and the address only exists on the machine - so committing it is also the switch that turns the
  # pinned destination on, with nothing else to remember to flip. Same arrangement as
  # onionIdentityPinned in modules/tor.nix.
  destinationKeysPinned = builtins.pathExists destinationKeys;

  # ... and only while i2pd actually runs, for the same reason as installOnionIdentity in
  # modules/tor.nix: a declared secret must be decryptable at activation even when nothing reads
  # it, so dropping it together with the service is what makes the build-vm variant (modules/vm.nix)
  # boot without PC-001's host key.
  installDestinationKeys = destinationKeysPinned && config.services.i2pd.enable;

  # Where i2pd looks for a tunnel's key set: `keys` is resolved relative to the datadir, which is
  # /var/lib/i2pd because the nixpkgs module starts i2pd with --service. Spelled out once here
  # because it is both the name every tunnel below refers to and the file sops has to place.
  keysFileName = "monero-mainnet.dat";

  # p2pool's sidechain P2P port, pinned in modules/p2pool.nix for the same reason as its stratum
  # port: so that a change there cannot quietly leave these tunnels pointing at nothing.
  p2poolP2pPort = 37888;
in {
  # `format = "binary"` is what makes this work at all: sops-nix then decrypts the whole file and
  # writes it out verbatim instead of extracting a string value from a YAML document - necessary,
  # because an i2pd key set is raw key material with NUL bytes in it and could never be a YAML value.
  #
  # `path` puts it where i2pd reads tunnel key sets from. sops-nix keeps the real file under
  # /run/secrets and leaves a symlink at this path (sops-install-secrets: MkdirAll of the parent,
  # then createSymlink), so nothing about the secret's location is unusual for sops-nix - it just has
  # to be inside i2pd's datadir for `keys = "monero-mainnet.dat"` to find it. Two details make that
  # safe rather than accidental:
  #
  #   * owner = "i2pd" because the daemon, not root, opens the file (mode stays 0400).
  #   * /var/lib/i2pd already exists by the time sops runs: secrets are installed from an activation
  #     script that nixpkgs orders after `users` (lib.stringAfter ["specialfs" "users" "groups"]),
  #     and that is where NixOS creates this home directory for i2pd's createHome. So the MkdirAll
  #     above never gets to make it root-owned, which would have left the router unable to write its
  #     own state.
  sops.secrets."i2p/monero_mainnet_keys" = lib.mkIf installDestinationKeys {
    sopsFile = destinationKeys;
    format = "binary";
    path = "/var/lib/i2pd/${keysFileName}";
    owner = "i2pd";
    # A different key set means a different destination, and i2pd only reads it while starting up.
    restartUnits = [ "i2pd.service" ];
  };

  services.i2pd = {
    enable = true;
    yggdrasil.enable = false;
#  websocket.enable = true;
    upnp.enable = true;
    reseed.verify = false;
    reseed.file = "${../i2pseeds.su3}"; # wget --user-agent="Wget/1.11.4" https://reseed.stormycloud.org/i2pseeds.su3
    proto.socksProxy.enable = true;
    proto.i2cp.enable = true;
    proto.i2pControl.enable = true;
    proto.http.enable = true; # WebUI
    port = 19410;
    enableIPv6 = true;
    enableIPv4 = true;
    ntcp2 = {
      enable = true;
      published = true;
    };
#    ssu2.enable = true;
#    ssu2.published = true;
    proto.httpProxy.enable = true;

    # This node's Monero side on I2P - the counterpart of the onion service in modules/tor.nix and
    # built the same way as https://docs.getmonero.org/running-node/monerod-tori2p/#i2p describes
    # it: server tunnels that share one keyset, i.e. a single destination with several ports inside
    # I2P. Four of them, so that monerod and p2pool live under one name the way they do on Tor:
    #
    #   <b32>.b32.i2p (default endpoint) -> 127.0.0.1:18085   monerod P2P, its anonymous-inbound
    #   <b32>.b32.i2p:18089              -> 127.0.0.1:18089   monerod restricted RPC, for wallets
    #   <b32>.b32.i2p:28723              -> 127.0.0.1:37888   p2pool sidechain P2P (the port p2pool tells peers to dial)
    #   <b32>.b32.i2p:3333               -> 127.0.0.1:3333    p2pool stratum, for miners over I2P
    #
    # Like tor this needs no firewall exception: i2pd is already connected to the network and only
    # publishes a destination, and each service binds its own target port on loopback.
    #
    # Why the tunnels use different `inPort` values: monerod's i2p addresses carry no port at all
    # (i2p_address::port() in the monero tree is a constant 1), so a peer relaying a transaction to
    # this node dials destination port 1 and i2pd delivers it to the destination's *default* streaming
    # endpoint - which is what a server tunnel with `inPort = 0` attaches to (ClientDestination::
    # HandleDataMessage falls back to the default when no port matches). Everything else keeps an
    # explicit inport, so wallet connections to :18089 and p2pool's peers match theirs exactly and
    # never end up at the monerod P2P tunnel. This is also why the docs show `inport = 0` for the node
    # tunnel but no inport at all for the RPC one.
    #
    # The .b32.i2p name follows from the keyset named by `keys` below. With secrets/i2p/ committed it
    # is that file, installed by sops before i2pd starts, so the address is known while this file is
    # being evaluated and can be announced on the very first boot; without it i2pd invents one on
    # first start (deleting /var/lib/i2pd/monero-mainnet.dat changes such an address). Either way the
    # name itself stays out of this public repository: it goes to secrets/monero.yaml as
    # sops.secrets."monero/i2p_hostname", which modules/services.nix appends to monerod.conf and
    # modules/p2pool.nix passes to --i2p-address. scripts/make-i2p-key.sh writes both halves at once;
    # on a machine that already runs this config the name can also be read off afterwards with
    #   journalctl -u i2pd | grep 'Local address'      or   http://127.0.0.1:7070/?page=i2p_tunnels
    inTunnels."monerod-p2p" = {
      # The module writes every tunnel it is given - its collector only checks for a port and an
      # address and ignores this flag, whose default is false. Set anyway, so that the tunnel
      # cannot quietly disappear if upstream ever starts honouring it.
      enable = true;

      type = "server"; # raw TCP; the monero P2P protocol passes through untouched

      # Where i2pd forwards to locally: monerod's anonymous-inbound listener for I2P, which it
      # binds on loopback because modules/services.nix names 127.0.0.1:18085.
      address = "127.0.0.1";
      port = 18085;

      # 0 = attach to the destination's default streaming endpoint, which is where monerod's
      # peers end up because their address format has no I2P-side port (see above). Pinning this
      # to 18085 instead would make inbound P2P silently unreachable.
      inPort = 0;

      # Relative to /var/lib/i2pd. Shared with every tunnel below, so all these ports live on one
      # destination - spelled out because this file *is* the identity.
      keys = keysFileName;
    };

    inTunnels."monerod-rpc" = {
      enable = true;

      type = "server"; # raw TCP, so JSON-RPC passes through without HTTP rewriting

      # monerod's restricted RPC (rpc-restricted-bind-ip=0.0.0.0 also answers on loopback), the
      # endpoint that is already there for public-node duty - only the *restricted* subset of RPC
      # methods, never the unauthenticated full one on 18081.
      address = "127.0.0.1";
      port = 18089;

      # Wallets dial <b32>.b32.i2p:18089 (same number as on clearnet); an exact inport keeps this
      # apart from the P2P tunnel above, which sits on the default endpoint.
      inPort = 18089;

      keys = keysFileName; # same destination as monerod-p2p above
    };

    # p2pool's sidechain P2P, published under the port p2pool *names in its announcement* rather than
    # under its local one: DEFAULT_P2P_PORT_I2P in p2pool's src/p2p_server.h is a hard-coded 28723
    # (28722 on Tor, which modules/tor.nix maps the same way), so a peer that learns this destination
    # from a mined block connects to <this b32>.b32.i2p:28723. Mapping the local port under its
    # clearnet number instead would leave every announcement pointing at a port nothing answers on
    # here - and unlike monerod, p2pool does put a port in its addresses, which is why this tunnel
    # cannot sit on the default endpoint.
    inTunnels."p2pool-p2p" = {
      enable = true;

      type = "server"; # raw TCP; p2pool's sidechain protocol passes through untouched

      address = "127.0.0.1"; # p2pool's --p2p listener, see modules/p2pool.nix
      port = p2poolP2pPort;  # mini sidechain default; pinned there so it cannot drift

      inPort = 28723;        # DEFAULT_P2P_PORT_I2P, the port peers dial

      keys = keysFileName;   # same destination as monerod-p2p above
    };

    # Stratum for miners reaching this pool over I2P instead of over the LAN/clearnet socket. Same
    # endpoint as modules/p2pool.nix pins for Tor's 3333 mapping and the same lack of authentication -
    # a stratum client can only point hashrate at the wallet configured there, which is already
    # reachable from outside on TCP 3333 and merely gains an anonymity-preserving way in here. Delete
    # this tunnel to keep the destination to sidechain P2P; p2pool announces that either way.
    inTunnels."p2pool-stratum" = {
      enable = true;

      type = "server"; # raw TCP, so stratum (and its HTTP) passes through untouched

      address = "127.0.0.1"; # p2pool's --stratum listener, also pinned in modules/p2pool.nix
      port = 3333;

      # Miners dial <b32>.b32.i2p:3333, the number they use on clearnet too. Needs its own inport for
      # the same reason as monerod-rpc: only one tunnel per destination can sit on the default one.
      inPort = 3333;

      keys = keysFileName; # same destination as monerod-p2p above
    };
  };
}
