# P2Pool - decentralized Monero mining, mined straight from this host's monerod.
#
# The payout address is *not* in this file: it lives encrypted in ../secrets/p2pool.yaml,
# is rendered into a systemd EnvironmentFile at activation and reaches p2pool only as
# $P2POOL_WALLET_ADDRESS. Payout addresses are public on P2Pool and this repository is
# public too, so the address stays where the other credentials live:
#
#   nix shell nixpkgs#sops -c sops secrets/p2pool.yaml     # edit (needs PC-001's host key or git@frank.fyi)
#
# Miners connect to p2pool's stratum server on TCP 3333 - open to the network on purpose,
# e.g. xmrig --url PC-001:3333 (payouts always go to the wallet configured here).
{ config, lib, pkgs, inputs, ... }:

let
  cfg = config.services.p2pool;

  # Same file modules/tor.nix feeds to tor: the repository either carries this node's onion identity
  # or it does not, and that is what decides whether p2pool has an address to announce. See there for
  # the format and scripts/make-onion-key.sh for how it is created.
  onionIdentityPinned = builtins.pathExists ../secrets/tor/monerod_hs_ed25519_secret_key;

  # Same file modules/i2pd.nix installs for i2pd: the repository either carries this node's I2P
  # destination or it does not, and that is what decides whether p2pool has an I2P address to
  # announce. See there for the format and scripts/make-i2p-key.sh for how it is created.
  destinationKeysPinned = builtins.pathExists ../secrets/i2p/monero_mainnet_keys.dat;
in
{
  # services.p2pool does not exist in the pinned nixpkgs (nixos-26.05 ships the p2pool
  # *package* but no module), so the upstream module is imported from the nixos-unstable
  # input this flake already carries for unifi. It only declares options and one unit, and
  # cfg.package still resolves to pkgs.p2pool of the system's own nixpkgs - i.e. we run the
  # pinned program with a newer wrapper around it. Drop this import once nixos-26.05 has
  # services.p2pool itself.
  imports = [ (inputs.nixos-unstable + /nixos/modules/services/networking/p2pool.nix) ];

  sops.secrets."p2pool/wallet_address" = {
    sopsFile = ../secrets/p2pool.yaml;
    restartUnits = [ "p2pool.service" ]; # the EnvironmentFile is only read when the unit starts
  };

  # systemd's EnvironmentFile wants KEY=VALUE, which a bare secret value is not - hence a
  # template that wraps it. Owner + mode hand it to p2pool and to nobody else.
  #
  # `placeholder`, not the secret's `.path`: sops-nix replaces the placeholder string inside the
  # rendered template with the decrypted value while the system activates, so the file really holds
  # the address. Interpolating `.path` here would write the *file name* /run/secrets/... into the
  # EnvironmentFile and p2pool would be handed that as its wallet, fail to decode it and exit.
  # The second line is the address of the onion service in modules/tor.nix, which p2pool cannot be
  # handed as a file path - it wants the literal name on its command line. It is a secret for the same
  # reason monerod's copy of it is: the address identifies this node (modules/services.nix reads the
  # very same entry for monerod's anonymous-inbound).
  sops.templates."p2pool.env" = {
    owner = "p2pool";
    mode = "0440";
    # Both lines have to end in a newline of their own: nix drops the line break that directly
    # follows an opening '', so starting the second string with one would glue the two assignments
    # into a single EnvironmentFile line and hand p2pool a wallet address with the next variable
    # appended to it.
    content = ''
      P2POOL_WALLET_ADDRESS=${config.sops.placeholder."p2pool/wallet_address"}
    '' + lib.optionalString onionIdentityPinned ''
      P2POOL_ONION_ADDRESS=${config.sops.placeholder."monero/tor_hostname"}
    '' + lib.optionalString destinationKeysPinned ''
      P2POOL_I2P_ADDRESS=${config.sops.placeholder."monero/i2p_hostname"}
    '';
  };

  services.p2pool = {
    enable = true;

    # Next to the blockchain on the RAID5 volume (modules/filesystems.nix): p2pool keeps a
    # full copy of its sidechain plus a cache here. Spelled out rather than derived from
    # services.monero.dataDir so that disabling monero cannot silently move it to /var/lib.
    dataDir = "/mnt/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5/storage001.local/blockchain/p2pool";

    # The local monerod of modules/services.nix: its RPC on loopback plus the ZMQ pub
    # socket that its extraConfig already publishes on 18083. p2pool needs the *full* RPC,
    # not the restricted one - it asks monerod for block templates.
    host = "127.0.0.1";
    rpcPort = config.services.monero.rpc.port;
    zmqPort = 18083;

    # The mini sidechain: pays out at a rate that is realistic without a full rig.
    # (Main wants ~15 MH/s and more for all shares to land inside the PPLNS window,
    # nano pays out fastest but smallest.)
    sidechain = "mini";

    environmentFile = config.sops.templates."p2pool.env".path;
    walletAddress = "$P2POOL_WALLET_ADDRESS"; # expanded by the unit's shell from the EnvironmentFile

    # Neither port has an option in the module, and both are pinned here instead of left to
    # p2pool's own defaults (checked in v4.16 src/params.cpp: unset => [::] + 0.0.0.0) so
    # that the exceptions in modules/firewall.nix cannot drift away from them:
    #   * stratum is what miners connect to and it is open on purpose - an unauthenticated
    #     stratum endpoint only lets others point hashrate at *this* wallet, which is the
    #     whole idea. The same socket also answers HTTP (p2pool's statistics; switch that
    #     off with "--no-stratum-http"), while the /local/ endpoints stay closed because
    #     --local-api is not set.
    #   * the P2P port would otherwise follow the sidechain default, so switching
    #     services.p2pool.sidechain to nano would silently move it from 37888 to 37890 and
    #     take the firewall exception with it.
    #
    # The " \<newline>" in front of the first entry is not formatting: nixpkgs' module
    # appends extraArgs directly after "--wallet <address>" without any separator, so without
    # it p2pool would be handed "--wallet 4...D1c--stratum ...", reject the wallet address
    # and exit. It also stays harmless if upstream ever fixes that.
    # Outbound connections to .onion peers go through tor's SOCKS5 port - the same loopback proxy
    # monerod uses for its tx-proxy=tor (modules/services.nix). Nothing becomes reachable from
    # outside: this is tor's client side, and p2pool only dials out through it.
    socks5 = {
      enable = onionIdentityPinned;
      ip = config.services.tor.client.socksListenAddress.addr;
      port = config.services.tor.client.socksListenAddress.port;
    };

    extraArgs = [
      " \\\n--stratum [::]:3333,0.0.0.0:3333"
      "--p2p [::]:37888,0.0.0.0:37888"

      # p2pool has no option for a hidden service, so both halves of it are passed verbatim:
      #
      #   * --socks5-proxy-type tor - the module only emits --socks5, and p2pool otherwise guesses
      #     what kind of proxy it got from its port (9050 => Tor, 4447 => I2P, anything else plain).
      #     Onion peers are only dialled when that guess comes out as TOR, so moving
      #     services.tor.client.socksListenAddress off 9050 would silently stop them while
      #     --onion-address kept advertising a node nobody can reach. Naming the type drops the guess.
      #
      #   * --onion-address makes p2pool announce <this address>:28722 to its peers: the pubkey goes
      #     into the merge-mining extra of every block it finds (block_template.cpp, key
      #     keccak_onion_address_v3), which is how the address spreads, and 28722 is the constant
      #     peers then dial. That is what makes tor's 28722 mapping in modules/tor.nix worth having -
      #     an address that is never announced can only ever be reached by hand.
      #
      # The ${VAR:+...} wrapper (escaped so the *unit's* shell expands it, not nix) keeps the whole
      # flag off the command line while secrets/monero.yaml still holds an empty entry: p2pool decodes
      # the address into a pubkey at startup and exits when that fails, with Restart = always spinning
      # on it - the same reason modules/services.nix skips blank addresses for monerod.
      #
      # I2P is announced the same way, from the destination in modules/i2pd.nix: --i2p-address takes a
      # .b32.i2p name (from_i2p_b32() in src/i2p.h decodes it into a destination hash and p2pool exits
      # if that fails, hence the same guard) and peers then dial it at DEFAULT_P2P_PORT_I2P = 28723,
      # which is why that tunnel exists in modules/i2pd.nix. The two announcements are independent of
      # each other - block_template.cpp puts the onion pubkey under keccak_onion_address_v3 and the
      # destination hash under keccak_i2p_b32_address, both into the merge-mining extra of every block
      # this node finds - so naming both is what spreads both.
      #
      # What announcing I2P does *not* buy is outgoing I2P peers: p2pool has one SOCKS5 proxy with one
      # type (--socks5-proxy-type is auto|plain|tor|i2p), and src/p2p_server.cpp only dials .b32.i2p
      # peers while that type is i2p. It stays `tor` here, so this node reaches onion peers through
      # tor's SOCKS port and finds I2P peers by having them dial in. Incoming is unaffected by that
      # choice - both tunnels hand their connections to p2pool over loopback, which upstream handles
      # on purpose: "Incoming localhost connection. See if TOR/I2P are configured. Mixed TOR/I2P will
      # make it choose a special TOR+I2P peer ID in get_peerId." Switching the type to i2p would flip
      # that trade, not add to it.
      #
      # Worth knowing before wondering why hidden-service peers stop at ten: those loopback
      # connections are what --in-peers-localhost counts (m_maxIncomingPeersLocalhost = 10 by default,
      # clamped to >= 10), and Tor's and I2P's tunnels share that one counter. Raise it there if this
      # node should accept more inbound peers than that.
    ] ++ lib.optionals onionIdentityPinned [
      "--socks5-proxy-type tor"
      "\${P2POOL_ONION_ADDRESS:+--onion-address $P2POOL_ONION_ADDRESS}"
    ] ++ lib.optionals destinationKeysPinned [
      "\${P2POOL_I2P_ADDRESS:+--i2p-address $P2POOL_I2P_ADDRESS}"
    ];

    # Note: p2pool is built with UPnP support here and will therefore try to map its P2P
    # port on the gateway by itself (modules/firewall.nix already accepts IGD replies from
    # 192.168.178.0/24). Add "--no-upnp" to extraArgs to turn that off.
  };

  # createHome only covers a missing home; this also repairs ownership of a data directory
  # left behind by a manual run, and creates the parent directories on the RAID volume.
  systemd.tmpfiles.rules = [ "d ${cfg.dataDir} 0755 p2pool p2pool - -" ];

  # monerod has to be there first: p2pool exits when it cannot reach the RPC/ZMQ ports,
  # and Restart = always would just spin until the daemon answers.
  systemd.services.p2pool = {
    after = [ "monero.service" ];
    wants = [ "monero.service" ];
  };
}
