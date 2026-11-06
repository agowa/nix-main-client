{ config, lib, pkgs, inputs, ... }:

{
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

    # This node's monerod on I2P - the counterpart of the onion service in modules/tor.nix and
    # built the same way as https://docs.getmonero.org/running-node/monerod-tori2p/#i2p describes
    # it: two server tunnels that share one keyset, i.e. a single destination with two ports
    # inside I2P.
    #
    #   <b32>.b32.i2p (default endpoint) -> 127.0.0.1:18085   P2P, monerod's anonymous-inbound
    #   <b32>.b32.i2p:18089              -> 127.0.0.1:18089   restricted RPC for wallets

    #
    # Like tor this needs no firewall exception: i2pd is already connected to the network and only
    # publishes a destination, and monerod binds both target ports on loopback itself.
    #
    # Why the two tunnels use different `inPort` values: monerod's i2p addresses carry no port at
    # all (i2p_address::port() in the monero tree is a constant 1), so a peer relaying a
    # transaction to this node dials destination port 1 and i2pd delivers it to the destination's
    # *default* streaming endpoint - which is what a server tunnel with `inPort = 0` attaches to
    # (ClientDestination::HandleDataMessage falls back to the default when no port matches). The
    # RPC tunnel keeps an explicit inport, so wallet connections to :18089 match it exactly and
    # never end up at the monerod P2P tunnel. This is also why the docs show `inport = 0` for the
    # node tunnel but no inport at all for the RPC one.
    #
    # The .b32.i2p name follows from monero-mainnet.dat below, which i2pd generates on first start
    # (it is the destination's identity - back it up, deleting it changes the address). It is not
    # written into this public repo: it goes to secrets/monero.yaml as sops.secrets."monero/i2p_hostname",
    # where monerod picks it up at start - see modules/services.nix. Read it after the first start
    # on the WebUI's tunnels page (http://127.0.0.1:7070/?page=i2p_tunnels) or with
    #   journalctl -u i2pd | grep monerod
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

      # Relative to /var/lib/i2pd. Shared with the RPC tunnel below, so both ports live on one
      # destination - spelled out because this file *is* the identity.
      keys = "monero-mainnet.dat";
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

      keys = "monero-mainnet.dat"; # same destination as monerod-p2p above
    };
  };
}
