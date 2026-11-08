{ config, lib, pkgs, inputs, ... }:

let
  opts = { system = pkgs.stdenv.hostPlatform.system; config = { allowUnfree = true; }; };
  # nixos-25-05 = import inputs.nixos-25-05 opts; # unused
  # nixos-25-11 = import inputs.nixos-25-11 opts; # unused
  # nixos-26-05 = import inputs.nixos-26-05 opts; # unused
  nixos-unstable = import inputs.nixos-unstable opts;

  # monerod's anonymous inbound P2P, one entry per anonymity network. The port is both what
  # monerod binds on loopback and the port under which tor/i2pd publish this node inside their
  # networks: modules/tor.nix maps onion :18084 -> 127.0.0.1:18084, modules/i2pd.nix sends I2P
  # :18085 to 127.0.0.1:18085. Both differ from the clearnet P2P port (18080) on purpose - over
  # an anonymity network monerod serves a reduced protocol (handshake, timed sync, new txes) and
  # opens its own listener for it. The address itself is a secret and only known at start time,
  # see sops.secrets."monero/*" and systemd.services.monero.preStart below.
  # `announcePort` says whether the address is written with a port: a .onion needs it (that is
  # the port peers connect to and monerod stores it), while an i2p destination must be given bare
  # because monero's i2p addresses have no port field at all - i2pd then delivers incoming streams
  # to the tunnel with inPort = 0, see modules/i2pd.nix.
  moneroAnonymousInbound = [
    { secret = "monero/tor_hostname"; port = 18084; announcePort = true; }  # .onion, modules/tor.nix
    { secret = "monero/i2p_hostname"; port = 18085; announcePort = false; } # .b32.i2p, modules/i2pd.nix
  ];

in {
  # UniFi
  services.unifi = {
    enable = false;
    openFirewall = true;
    initialJavaHeapSize = 4096;
    unifiPackage = nixos-unstable.pkgs.unifi; # nixos-25.11's unifi package is marked as insecure
    jrePackage = pkgs.jdk25_headless;
  };

  # List services that you want to enable:
  services.fwupd.enable = true;
  services.languagetool.enable = true;
  services.openssh = {
    enable = true;
    allowSFTP = true;
    authorizedKeysInHomedir = false; # When false, the only files trusted by default are those in /etc/ssh/authorized_keys.d, i.e. SSH keys from users.users.<name>.openssh.authorizedKeys.keys.
    settings.GatewayPorts = "yes";
    settings.KbdInteractiveAuthentication = false;
    settings.PasswordAuthentication = false;
    settings.X11Forwarding = true;
  };
  services.smartd.enable = true;
  services.timesyncd.servers = [
    "2.europe.pool.ntp.org"
    "1.europe.pool.ntp.org"
    "0.europe.pool.ntp.org"
    "3.europe.pool.ntp.org"
  ];
  services.timesyncd.fallbackServers = [
    "2.pool.ntp.org"
    "1.pool.ntp.org"
    "0.pool.ntp.org"
    "3.pool.ntp.org"
  ];

  services.snowflake-proxy = {
    enable = true;
    capacity = 10;
  };
  services.octoprint = {
    enable = true;
    openFirewall = true;
    host = "::";
    port = 5000;
  };
  services.jellyfin = {
    enable = false;
    hardwareAcceleration = {
      device = "/dev/dri/renderD128";
      enable = true;
      type = "qsv";
    };
    openFirewall = true;
    forceEncodingConfig = true;
    transcoding = {
      enableHardwareEncoding = true;
      enableIntelLowPowerEncoding = true;
      hardwareDecodingCodecs = {
        av1 = true;
        h264 = true;
        #mpeg2 = true;
        hevc = true;
        hevc10bit = true;
        hevcRExt10bit = true;
        hevcRExt12bit = true;
        mpeg2 = true;
        vc1 = true;
        vp8 = true;
        vp9 = true;
      };
      hardwareEncodingCodecs = {
        av1 = true;
        hevc = true;
      };
    };
  };
  # Monero daemon. The Tor and I2P configuration lives next door (modules/tor.nix,
  # modules/i2pd.nix) but exists for this node: monerod reaches those networks with tx-proxy
  # (outbound) and anonymous-inbound (inbound), both below. modules/p2pool.nix mines through the
  # RPC and ZMQ ports of this daemon.
  services.monero = {
    enable = true;
    dataDir = "/mnt/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5/storage001.local/blockchain/bitmonero";

    priorityNodes = [
      "p2pmd.xmrvsbeast.com:18080"
      "nodes.hashvault.pro:18080"
    ];

    # The rest has no dedicated option in the nixpkgs module, so it is spliced into
    # monerod.conf verbatim.
    extraConfig = ''
      zmq-pub=tcp://127.0.0.1:18083
      out-peers=32
      in-peers=64
      enforce-dns-checkpointing=1
      enable-dns-blocklist=1
      rpc-restricted-bind-ip=0.0.0.0
      rpc-restricted-bind-port=18089
      public-node=1

      # Anonymity networks, see https://docs.getmonero.org/running-node/monerod-tori2p/ and
      # docs/ANONYMITY_NETWORKS.md in the monero tree (both are experimental upstream, and both
      # cover P2P transaction relay only - blocks keep syncing over IPv4 by design).
      #
      # tx-proxy is what makes such a network work at all: it names the SOCKS proxy monerod dials
      # .onion/.b32.i2p peers through, and monerod only hands its own hidden address to peers of a
      # network it has a proxy for - with anonymous-inbound but no tx-proxy it logs "Listed
      # --anonymous-inbound without listing any --tx-proxy" and exits. Both proxies here are the
      # loopback SOCKS ports of the services configured next door (tor's client SOCKS, i2pd's
      # socksproxy; both enabled there), so nothing becomes reachable from outside. The trailing 16
      # caps the outbound connections per network (monerod's own default is unlimited); noise
      # padding stays on, i.e. no fourth ",disable_noise" field.
      tx-proxy=tor,${config.services.tor.client.socksListenAddress.addr}:${toString config.services.tor.client.socksListenAddress.port},16
      tx-proxy=i2p,${config.services.i2pd.proto.socksProxy.address}:${toString config.services.i2pd.proto.socksProxy.port},16

      # anonymous-inbound=<hidden address>,127.0.0.1:<port> belongs here as well (with a port for
      # Tor, without one for I2P - see moneroAnonymousInbound), but the two addresses identify this
      # node and are therefore not written into a public repository: they live in
      # secrets/monero.yaml and get appended to monerod.conf while it is generated - see
      # sops.secrets."monero/*" and systemd.services.monero.preStart below. The onion name gets put
      # there by scripts/make-onion-key.sh, because it follows from the identity pinned in
      # secrets/tor/; the I2P one only exists after i2pd's first start and is recorded by hand.
    '';
  };

  # The hidden addresses used for anonymous inbound P2P: one onion name (modules/tor.nix,
  # relay.onionServices."monerod") and one .b32.i2p destination (modules/i2pd.nix,
  # inTunnels."monerod-p2p" + "monerod-rpc", which share a keyset). They are secrets for the same
  # reason the p2pool payout address is: this repository is public and they identify this node.
  #
  # `sopsFile` is not decoration here: sops.defaultSopsFile (modules/sops.nix) points at
  # secrets/secrets.yaml, which carries only the SABnzbd credentials. Without it these two look for a
  # `monero:` section in that file, recurseSecretKey() in sops-nix's installer finds none and
  # activation dies with "the key 'monero/tor_hostname' cannot be found" - which aborts the whole
  # switch, not just monerod. Both addresses live in secrets/monero.yaml; modules/p2pool.nix reads
  # the onion one out of the very same entry.
  #
  # The onion name no longer has to wait for tor's first start: scripts/make-onion-key.sh pins this
  # node's identity as secrets/tor/monerod_hs_ed25519_secret_key and writes the address derived from
  # that key into monero.yaml, so it is known - and announced - on the very first boot. The .b32.i2p
  # name is still invented by i2pd on first start (its identity is /var/lib/i2pd/monero-mainnet.dat)
  # and has to be recorded afterwards:
  #
  #   http://127.0.0.1:7070/?page=i2p_tunnels             -> monero.i2p_hostname, the .b32 name of
  #       (destination behind /var/lib/i2pd/monero-mainnet.dat)   that tunnel's destination
  #
  #   nix shell nixpkgs#sops -c sops secrets/monero.yaml
  #
  # An entry that is still empty is skipped by the hook below rather than handed to monerod (sops
  # leaves empty strings alone). owner = "monero" because that hook reads them as the daemon's user;
  # mode stays 0400, so only root and monero can read.
  sops.secrets."monero/tor_hostname" = {
    sopsFile = ../secrets/monero.yaml;
    owner = "monero";
    restartUnits = [ "monero.service" ]; # a new address only takes effect when the conf is rewritten
  };

  sops.secrets."monero/i2p_hostname" = {
    sopsFile = ../secrets/monero.yaml;
    owner = "monero";
    restartUnits = [ "monero.service" ];
  };

  # The nixpkgs module writes ${dataDir}/monerod.conf in preStart, so this is appended after it:
  # one anonymous-inbound option per network whose address has actually been recorded. Skipping a
  # blank secret on purpose - get_anonymous_inbounds() in monero's src/p2p/net_node.cpp rejects an
  # empty address and the daemon then exits at startup, which would take p2pool down with it. Until
  # both addresses are in sops the node simply runs without anonymous inbound P2P. sops-nix renders
  # /run/secrets during activation, before any service unit starts (modules/sops.nix).
  systemd.services.monero.preStart = lib.mkAfter (''
    monerod_conf='${config.services.monero.dataDir}/monerod.conf'
  '' + lib.concatMapStrings (inbound: ''
    address="$(tr -d '[:space:]' < '${config.sops.secrets."${inbound.secret}".path}')"
    if [ -n "$address" ]; then
      echo "anonymous-inbound=$address${lib.optionalString inbound.announcePort ":${toString inbound.port}"},127.0.0.1:${toString inbound.port}" >> "$monerod_conf"
    fi
  '') moneroAnonymousInbound);

#  services.home-assistant = {
#    enable = true;
#    openFirewall = true;
##    configWritable = false;
##    lovelaceConfigWritable = false;
#    customComponents = with pkgs.home-assistant-custom-components; [
#      prometheus_sensor
#    ];
#    customLovelaceModules = with pkgs.home-assistant-custom-lovelace-modules; [
#      mini-graph-card
#      mini-media-player
#    ];
#    config = {
#      http = {
#        server_host = [
#          "0.0.0.0"
#          "::"
#        ];
#        server_port = 8123;
#      };
#      homeassistant = {
#        unit_system = "metric";
#        latitude = 50.1109;
#        longitude = 8.6821;
#      };
#    };
#  };

  services.btrfs.autoScrub = {
    enable = true;
    interval = "monthly";
  };
#  services.beesd.filesystems = {
#    "2df491cf-b27c-41cd-ad88-2aa2b25278ca" = {
#      spec = "UUID=2df491cf-b27c-41cd-ad88-2aa2b25278ca";
#      hashTableSizeMB = 294912; # Volume size * 4 / 1024 then convert into MB, rounded up to next value divisible by 16
#      verbosity = "crit";
#      extraOptions = [
#        "--loadavg-target"
#        #"5.0"
#        "192.0"
#      ];
#    };
#  };

#  services.beesd.filesystems = {
#    "-" = {
#      spec = "LABEL=root";
#      hashTableSizeMB = 2048;
#      verbosity = "crit";
#      extraOptions = [
#        "--loadavg-target"
#        "5.0"
#      ];
#    };
#  };

  # Firewall stuff
  services.pcscd.enable = true;
#  services.miniupnpd.enable = true;
#  services.miniupnpd.externalInterface = "vlan10";
#  services.miniupnpd.natpmp = true;
#  services.miniupnpd.upnp = true;
}
