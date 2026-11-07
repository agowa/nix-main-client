# nix-main-client

This repository contains the nix configuration for my main clients (testing status right now).

## Build and switch current system to this configuration

```bash
nixos-rebuild switch --flake . --sudo
```

## Listening ports and firewall exposure

The firewall is enabled (`networking.firewall.enable = true`, nftables backend via `networking.nftables.enable`), so anything not listed here is dropped inbound. Loopback-only listeners need no exception, which is why many rows say *no*.

*Bind* is the address the socket is opened on: `::` / `0.0.0.0` means every interface (reachable from the network), `127.0.0.1` only this machine. Where a value comes from nixpkgs rather than this repo it says *module default*.

*Firewall exception* distinguishes **automatic** (a NixOS module opens its own port) from **manual** (hand-written in `modules/firewall.nix`).

### Services with a listening socket

| Software | Port | Bind | Firewall exception | Defined in |
| --- | --- | --- | --- | --- |
| OpenSSH (`sshd`) | TCP 22 | `::` and `0.0.0.0` (no `ListenAddress`/`Port` override) | **Yes** – automatic, `services.openssh.openFirewall` defaults to `true` | `modules/services.nix` |
| Tor ORPort (bridge relay) | TCP 443 | `::` / `0.0.0.0` | **Yes** – automatic via `services.tor.openFirewall = true` | `modules/tor.nix` |
| Tor SOCKS proxy | TCP 9050 | `127.0.0.1` (module default `client.socksListenAddress`) | No (loopback only) | `modules/tor.nix` (`client.enable = true`) |
| Tor DNS (`DNSPort`) | UDP 9053 (no TCP listener in practice) | `127.0.0.1` | No (loopback only) | `modules/tor.nix` (`client.dns.enable = true`) |
| Tor ExtORPort | TCP dynamic (`auto`) | `127.0.0.1` | No (loopback only) | implicit: nixpkgs sets `ExtORPort.port = "auto"` for `relay.role = "bridge"`, so nothing in `modules/tor.nix` mentions it |
| i2pd NTCP2 (transport) | TCP 19410 | `0.0.0.0` — IPv4 wildcard only, no `[::]` socket is opened even though `enableIPv6 = true`; port from `services.i2pd.port`, `ntcp2.port` is the default `0` (= use it) | **Yes** – manual: `config.services.i2pd.port` in `allowedTCPPorts` | port: `modules/i2pd.nix`; rule: `modules/firewall.nix` |
| i2pd SSU2 (transport) | UDP 19410 | as NTCP2, IPv4 wildcard only (`ssu2.enable`/`ssu2.port` left at their defaults, which are enabled / `0`) | **Yes** – manual: same option in `allowedUDPPorts` | port: `modules/i2pd.nix`; rule: `modules/firewall.nix` |
| i2pd WebUI | TCP 7070 | `127.0.0.1` (module default) | No (loopback only) | `modules/i2pd.nix` (`proto.http.enable = true`) |
| i2pd HTTP proxy | TCP 4444 | `127.0.0.1` (module default) | No | `modules/i2pd.nix` (`proto.httpProxy.enable = true`) |
| i2pd SOCKS proxy | TCP 4447 | `127.0.0.1` (module default) | No | `modules/i2pd.nix` (`proto.socksProxy.enable = true`) |
| i2pd I2CP | TCP 7654 | `127.0.0.1` (module default) | No | `modules/i2pd.nix` (`proto.i2cp.enable = true`) |
| i2pd I2PControl | TCP 7650 | `127.0.0.1` (module default) | No | `modules/i2pd.nix` (`proto.i2pControl.enable = true`) |
| OctoPrint | TCP 5000 | all interfaces, dual-stack (`0.0.0.0` **and** `[::]`) — the explicit `host = "::"` did not restrict it to IPv6 | **Yes** – automatic via `services.octoprint.openFirewall = true` | `modules/services.nix` |
| OctoPrint (Zeroconf + SSDP) | UDP 5353 + UDP 1900 | `0.0.0.0`, plus one socket per interface (`192.168.178.119`, `10.0.3.1`) | **No dedicated exception** – OctoPrint *requires* Zeroconf/SSDP to announce and discover devices, and the multicast this depends on gets through only because of the blanket `pkttype { broadcast,multicast } accept` rule below; unicast mDNS/SSDP to this host is still dropped, since neither `5353` nor `1900` is in `allowedUDPPorts` | OctoPrint's own Zeroconf stack — not configured in this repo (`modules/services.nix` enables the service) |
| monerod P2P | TCP 18080 | `0.0.0.0` — IPv4 wildcard only, no `[::]` socket (monerod's own default; no `p2p-bind-ip` is set) | **Yes** – manual entry `18080 # monerod`; the module has no firewall option | enable: `modules/services.nix`; rule: `modules/firewall.nix` |
| monerod RPC | TCP 18081 | `127.0.0.1` (module default `rpc.address`; `rpc.restricted` is left at its default `false`, so this is unauthenticated full RPC on loopback) | No (loopback only) | enable: `modules/services.nix`; address/port: module defaults |
| monerod ZMQ RPC | TCP 18082 | `127.0.0.1` | No (loopback only) | not set anywhere in this repo — port and address come from monerod's own defaults (the ZMQ *pub* socket on 18083 below is configured here) |
| monerod ZMQ pub | TCP 18083 | `127.0.0.1` — set explicitly with `zmq-pub=tcp://127.0.0.1:18083`; a second, separate listener from the ZMQ RPC server above (monerod's own default for that one stays 18082) | No (loopback only) | `modules/services.nix` (`services.monero.extraConfig`) |
| monerod restricted RPC (public node) | TCP 18089 | `0.0.0.0` — IPv4 wildcard only, set explicitly with `rpc-restricted-bind-ip=0.0.0.0` + `rpc-restricted-bind-port=18089`; `public-node=1` advertises this node and that endpoint to peers, so it is meant to be reached from outside | **Yes** – manual entry `18089 # monerod restricted RPC (public node)`; the module has no firewall option for `extraConfig` | `modules/services.nix` (`services.monero.extraConfig`); rule: `modules/firewall.nix`. Only the *restricted* subset of RPC methods is served here — the unauthenticated full RPC on 18081 above stays loopback-only |
| monerod anonymous P2P listeners | TCP 18084 (Tor) and TCP 18085 (I2P) | `127.0.0.1` — opened by monerod itself, because `anonymous-inbound=<hidden address>,127.0.0.1:<port>` names the local port it should deliver to; tor and i2pd are the only processes that reach them and they do so over loopback | No (loopback only) | ports: `modules/services.nix` (`moneroAnonymousInbound`, appended to monerod.conf by `systemd.services.monero.preStart`); front-ends: `modules/tor.nix`, `modules/i2pd.nix`. Not the clearnet P2P port 18080 on purpose: over an anonymity network monerod only speaks handshakes, timed syncs and transaction relay |
| monerod over Tor (P2P + restricted RPC) | one v3 onion service with two ports: `<name>.onion:18084` → `127.0.0.1:18084` (P2P, the listener above) and `<name>.onion:18089` → `127.0.0.1:18089` (restricted RPC, for wallets connecting over Tor). Nothing new is bound on an interface — tor dials into loopback | **No** – and none needed, tor connects outwards; its own inbound port is the ORPort 443 row above | hidden service: `modules/tor.nix` (`services.tor.relay.onionServices."monerod"`); monerod side: `modules/services.nix` (`tx-proxy=tor,127.0.0.1:9050,16` + the appended `anonymous-inbound`). The `.onion` name follows from the private key pinned in `secrets/tor/monerod_hs_ed25519_secret_key` (see *Provisioning an onion identity*), so it is known before tor ever starts; the name itself still lives in `secrets/monero.yaml` rather than here, and with that key present tor keeps its service directory in `/run/tor/onion/monerod` instead of `/var/lib/tor/onion/monerod`. nixpkgs warns at evaluation that an onion service inside a *relay* can be spotted by traffic analysis of the relay ([trac#8742](https://trac.torproject.org/projects/tor/ticket/8742)) — this host is a bridge, so the endpoints are anonymously reachable but not unfindable |
| monerod over I2P (P2P + restricted RPC) | two server tunnels sharing one keyset, i.e. one destination: `<b32>.b32.i2p` (default endpoint) → `127.0.0.1:18085` for P2P and `<b32>.b32.i2p:18089` → `127.0.0.1:18089` for the restricted RPC. Again no new socket on any interface | **No** – none needed, i2pd is already connected; its transport ports have the NTCP2/SSU2 rows above | tunnels: `modules/i2pd.nix` (`services.i2pd.inTunnels."monerod-p2p"`, `"monerod-rpc"`); monerod side: `modules/services.nix` (`tx-proxy=i2p,127.0.0.1:4447,16` + the appended `anonymous-inbound`). The `.b32.i2p` name derives from `/var/lib/i2pd/monero-mainnet.dat`, which i2pd generates on first start — back that file up (deleting it changes the address) and record the name in `secrets/monero.yaml`. The P2P tunnel uses `inPort = 0`, i.e. the destination's default streaming endpoint, because monerod's I2P addresses carry no port (its peers dial port 1 and i2pd falls back to that endpoint); the RPC tunnel pins `inPort = 18089` so wallet traffic matches it exactly |
| p2pool P2P | TCP 37888 | `[::]` **and** `0.0.0.0` — dual-stack, pinned with `--p2p [::]:37888,0.0.0.0:37888`; that is also p2pool's own default for the mini sidechain (main 37889, nano 37890), it is spelled out here so the firewall rule cannot drift if `services.p2pool.sidechain` changes | **Yes** – manual entry `37888 # p2pool P2P`; the module has no firewall option for `extraArgs` | enable: `modules/p2pool.nix`; rule: `modules/firewall.nix` |
| p2pool stratum (miners) | TCP 3333 | `[::]` **and** `0.0.0.0`, pinned with `--stratum [::]:3333,0.0.0.0:3333`. Unauthenticated **by design**: a stratum client can only point hashrate at the wallet configured here, so participating is not a security boundary. The same socket also answers HTTP requests (p2pool's statistics — `--no-stratum-http` would switch that off); its `/local/` endpoints stay closed because `--local-api` is not set | **Yes** – manual entry `3333 # p2pool stratum` | enable: `modules/p2pool.nix`; rule: `modules/firewall.nix` |
| p2pool over Tor (sidechain P2P + stratum) | the **same** v3 onion service as monerod, with two more ports: <name>.onion:28722 -> 127.0.0.1:37888 and <name>.onion:3333 -> 127.0.0.1:3333. The first is published under 28722 rather than 37888 because that is the constant p2pool puts in its announcements (`DEFAULT_P2P_PORT_ONION` in `src/p2p_server.h`, 28723 for I2P) - mapping it under its local port would advertise an endpoint nothing here answers on. Nothing new is bound on an interface, tor dials into loopback | **No** - none needed; tor connects outwards and its own inbound port is the ORPort 443 row above | hidden service: `modules/tor.nix` (`services.tor.relay.onionServices."monerod".map`); p2pool side: `modules/p2pool.nix`, which also has to *announce* the address for the P2P mapping ever to be used - `--onion-address <address>` (from `secrets/monero.yaml` -> `monero.tor_hostname`) puts its pubkey into every block p2pool finds, which is how peers learn it, and `--socks5 127.0.0.1:9050` + `--socks5-proxy-type tor` is how p2pool reaches other nodes' onions. Both are only set once the identity is pinned (see *Provisioning an onion identity*), and the announcement stays away while `monero.tor_hostname` is still empty |
| SABnzbd web UI + API | TCP 8085 | `127.0.0.1`, plus `inet_exposure = "none"`. It was **not** listening when this table was checked against `ss` output, despite the service being enabled — check `systemctl status sabnzbd` | **No** – deliberate; reach it through a tunnel: `ssh -L 8085:127.0.0.1:8085 PC-001` | `modules/sabnzbd.nix` |
| LanguageTool server | TCP 8081 | loopback only (`public` left at its default `false`; port is the module default) | No (loopback only) | enable: `modules/services.nix`; address/port: module defaults |
| systemd-resolved (stub resolver) | UDP + TCP 53 | `127.0.0.53`, also answers on `127.0.0.54` | No (loopback only) | `modules/networking.nix` (`DNSStubListener = true`) |
| systemd-resolved (mDNS) | UDP 5353 | wildcard socket (`0.0.0.0` and `[::]`) for the multicast groups `224.0.0.251` / `ff02::fb`. The port is **shared with OctoPrint**: both processes hold `UDP-5353` at the same time, which only works because they bind it with `SO_REUSEPORT` | **Partial** – no `allowedUDPPorts` entry (`5353` is commented out); only multicast to the two mDNS groups is accepted, via `extraInputRules` below | responder: `modules/networking.nix`; rules: `modules/firewall.nix` |
| dnsmasq — container bridge DNS + DHCP | UDP + TCP 53, UDP 67 | DNS on `10.0.3.1` and the container ULA `[fc42:…::1]`, DHCP on `0.0.0.0%lxcbr0` — confined to the container bridge, not reachable from the LAN | No (bridge-local) | implicit: `virtualisation.lxc.unprivilegedContainers = true` in `modules/virtualisation.nix` turns on the packaged `lxc-net` unit, which runs dnsmasq |
| CUPS (`cupsd`) | TCP 631 | `localhost` (`Listen localhost:631`) plus the unix socket `/run/cups/cups.sock` | No – `services.printing.openFirewall` defaults to `false`, and the listener is loopback-only anyway | `modules/desktop.nix` |
| mosh-server | UDP 60000-61000 (one ephemeral port per session) | wildcard; spawned on demand over SSH by `mosh-client`, not a resident daemon | **Yes** – automatic, `programs.mosh.openFirewall` defaults to `true` and adds the range to `allowedUDPPortRanges` | enable: `modules/programs.nix`; rule: nixpkgs mosh module |
| Steam Link / local network game transfers | UDP 27036 (peer discovery), TCP 27040 (data transfer) | opened by the Steam client inside the user session while running, on all interfaces | **Yes** – automatic via `programs.steam.localNetworkGameTransfers.openFirewall = true` (`remotePlay.openFirewall` stays `false`, so Remote Play's extra ports are *not* open) | enable: `modules/programs.nix`; rule: nixpkgs steam module |
| snowflake-proxy | UDP dynamic — one socket per WebRTC/DTLS association rather than a fixed port | wildcard (`0.0.0.0` and `[::]`); on the live host it held ~21 such sockets at once | No exception and none needed: every association is dialled out by this host, so replies return as established/related traffic. One of those sockets happened to land on UDP 60050, which *is* reachable from outside because it falls inside the open mosh range `60000-61000` | `modules/services.nix` |
| hddtemp | TCP 7634 | `0.0.0.0` | No — host-local only, and only because no rule allows it | not defined here: `hardware.sensor.hddtemp.enable` defaults to `false`, so this daemon was started outside the nix configuration |
| Lyrebird | Dynamic | | | |

### Manually opened ports without a system service

These are allow-listed in `modules/firewall.nix`, but the listener is started from the desktop/user session (or not running at all), so nothing in this repo pins its bind address. They are listed here so the firewall rules and this table stay reconcilable.

| Software / purpose | Port | Bind | Firewall exception | Defined in |
| --- | --- | --- | --- | --- |
| BiglyBT — BitTorrent data and web interface | TCP 6969, 7000, 8080, 27151, 28549, 29605 | `::` / `0.0.0.0` while BiglyBT runs; the ports and interfaces are chosen inside BiglyBT's own settings, not in this repo | **Yes** – manual `<BiglyBT>` block in `allowedTCPPorts` | rules: `modules/firewall.nix`; app installed via `users.users.user.packages` in `modules/users.nix` |
| BiglyBT — UDP (DHT / trackers) | UDP 6969, 27151, 27152, 28549, 29605, 49001 | as above | **Yes** – manual `<BiglyBT>` block in `allowedUDPPorts`; note `27152` is self-labelled "temporary for testing only" (BUGS.md #10) and `8080` collides with the port SABnzbd would otherwise use (`modules/sabnzbd.nix`) | as above |
| aria2 — DHT / UDP tracker, ad-hoc CLI use | **UDP 6881-6999 (range)** | wildcard while `aria2c` runs | **Yes** – manual line `allowedUDPPortRanges = [] ++ config.services.aria2.settings.listen-port`, but the value is nixpkgs' *module default* range: `services.aria2.enable` is never set, so this opens 6881-6999 on the public interface for a tool that runs only from the shell (BUGS.md #5) | `modules/firewall.nix` |
| NAT-PMP (`services.miniupnpd`) | UDP 5350 | **nothing listens**: `services.miniupnpd.*` is commented out in `modules/services.nix`, so this exception is currently orphaned. Worth double-checking the number too: NAT-PMP/PCP normally use UDP 5351, and mDNS is 5353 | **Yes** – manual entry `5350 # NAT-PMP` in `allowedUDPPorts` | rule: `modules/firewall.nix`; disabled service: `modules/services.nix` |

### Extra input rules (not tied to a single port)

`networking.firewall.extraInputRules` in `modules/firewall.nix` appends nftables rules that no table row above covers:

| Rule | Traffic accepted | Comment in the file |
| --- | --- | --- |
| `ip saddr 192.168.178.0/24 udp sport 1900 udp dport >= 1024 meta pkttype unicast limit rate 4/second burst 20 packets accept` | UPnP IGD port-mapping replies from the LAN gateway to **any** ephemeral unicast port, rate limited | "Accept UPnP IGD port mapping reply" |
| `ip protocol igmp accept` | IGMP (multicast group management; no transport ports involved) | "Accept IGMP" |
| `udp dport mdns ip6 daddr ff02::fb accept` | mDNS, UDP 5353 to the IPv6 multicast group | "Accept mDNS" |
| `udp dport mdns ip daddr 224.0.0.251 accept` | mDNS, UDP 5353 to the IPv4 multicast group | "Accept mDNS" |
| `pkttype { broadcast,multicast } accept` | **all** broadcast and multicast traffic, every protocol and every port | — |

The last rule is by far the broadest one in the file: it accepts any broadcast or multicast packet regardless of port. Two further rules (`ip saddr 192.168.178.1 accept` and a UPnP variant) are present but commented out, so they are inactive. Logging of refused traffic is switched off entirely (`logRefusedPackets`, `logRefusedUnicastsOnly`, `logReversePathDrops` all `false`).

### No listening socket / not exposed

- **Unix sockets only, no TCP port:** openvswitch `ovsdb-server` (`--remote=punix:/run/openvswitch/db.sock`, from `virtualisation.vswitch` in `modules/networking.nix`), libvirtd, podman's docker-compatible socket, pcscd, gpg-agent.
- **containerd is the exception:** besides its unix sockets it holds a loopback TCP listener on an auto-assigned port (`127.0.0.1:<dynamic>`). Nothing can reach it from outside, but it does appear in `ss` output.
- **Local display stack only:** sddm/weston/KDE Plasma talk over Wayland unix sockets, and `services.xserver.enable = false` means no X11 TCP port (6000+). `programs.wireshark.dumpcap.enable` installs the capture helper but does not start its remote-capture daemon.
- **Outbound only (no inbound service):** systemd-timesyncd to the NTP pool servers, fwupd, i2pd's reseed and UPnP/IGD traffic, monerod dialing out to peers — including `.onion`/`.b32.i2p` peers via `tx-proxy`, which points it at tor's SOCKS port 9050 and i2pd's SOCKS port 4447, both of which are loopback rows above, p2pool reading monerod's RPC (18081) and ZMQ pub (18083) on loopback plus its UPnP/IGD port-mapping attempt against the gateway. snowflake-proxy belongs here too in the sense that it never accepts a connection — it still binds dynamic WebRTC sockets, as its table row says.
- **Guest VMs:** a QEMU guest started with a graphical display opens its own VNC/SPICE listener (normally `127.0.0.1:59xx`) — created by the hypervisor at runtime, not by this configuration.
- **The hidden services themselves add no listener, but monerod does:** the onion service and the two I2P server tunnels are outbound — tor and i2pd publish a destination and forward into loopback, so they create no socket. `anonymous-inbound` however makes monerod open `127.0.0.1:18084` and `127.0.0.1:18085`, which *do* show up in `ss` output (see the row above), and it only does so once the two addresses are recorded in `secrets/monero.yaml`.
- **ICMP echo requests are answered** (`networking.firewall.allowPing` default `true`).
- **Currently disabled or commented out**, so they contribute no ports today: `services.unifi` (would open its own set including TCP 8080), `services.jellyfin` (TCP 8096), home-assistant (TCP 8123), `programs.kdeconnect` (UDP 1714-1764), `services.miniupnpd` (UDP 1900 + NAT-PMP/PCP), i2pd's yggdrasil transport, tor's control socket (`controlSocket.enable = false`, so only the unix socket exists). Re-check this section after enabling any of them.

### Verifying on the host

The evaluated firewall lists at the time of writing — every entry is accounted for by a row above:

```
allowedTCPPorts      = [ 22 443 3333 5000 6969 7000 8080 18080 18089 19410 27040 27151 28549 29605 37888 ]
allowedUDPPorts      = [ 5350 6969 19410 27036 27151 27152 28549 29605 49001 ]
allowedUDPPortRanges = [ { from = 6881; to = 6999; } { from = 60000; to = 61000; } ]
allowedTCPPortRanges = [ ]
```

Reproduce them, and compare against what is really listening:

```bash
nix eval .#nixosConfigurations.PC-001.config.networking.firewall.allowedTCPPorts
sudo nft list ruleset | grep -E 'dport|pkttype|igmp'   # effective rules on the machine
ss -Htulnp                                             # actual sockets and their owners
```

The bind columns were derived from `modules/*.nix` plus the module defaults of the pinned nixpkgs (`nixpkgs/nixos-26.05`) and cross-checked with `nix eval`. For programs that run in the user session — BiglyBT, Steam, aria2 — `ss -Htulnp` on PC-001 is the final word, since their sockets depend on the applications' own settings. The `IPv4 wildcard only`, `dual-stack` and *dynamic* port notes above come from that comparison rather than from the configuration itself.

## Secrets

Secrets are stored encrypted in this repository, so the repo can stay public. `secrets/secrets.yaml`
is a [sops](https://docs.sops.dev) file; it is decrypted on the machine at activation time by
[sops-nix](https://github.com/Mic92/sops-nix), which renders every entry declared in
`sops.secrets.<name>` into `/run/secrets/<name>`.

Recipients live in `.sops.yaml` and are public keys only. Two of them can decrypt the file:

| Recipient | Key |
| --- | --- |
| `git@frank.fyi` | operator key, for editing and re-encrypting |
| `root@PC-001` | PC-001's ed25519 **host** key; sops-nix converts it into an age identity at activation |

Because the machine decrypts with its own `/etc/ssh/ssh_host_ed25519_key`, no extra secret key
material has to be deployed. The flip side: if PC-001 is reinstalled and regenerates its host keys,
the new public key must be added to `.sops.yaml` (while an old recipient can still decrypt) and the
file re-encrypted.

### Editing a secret

```bash
nix shell nixpkgs#sops -c sops secrets/secrets.yaml
```

Add or change values, save, and sops re-encrypts automatically for all recipients in `.sops.yaml`.
Values must be strings; quote anything numeric, otherwise sops leaves it in plain text.

To find the age recipient belonging to a new SSH key:

```bash
nix shell nixpkgs#ssh-to-age -c sh -c "echo 'ssh-ed25519 AAAA... user@host' | ssh-to-age"
```

There is a second file of the same kind, `secrets/p2pool.yaml`, holding only
`sops.secrets."p2pool/wallet_address"` (encrypted to the same two recipients); modules point at it with
`sops.secrets.<name>.sopsFile`. Edit it the same way:

```bash
nix shell nixpkgs#sops -c sops secrets/p2pool.yaml
```

A third file, `secrets/monero.yaml`, holds the two hidden-service addresses monerod announces for
anonymous inbound P2P — `monero.tor_hostname` (the `.onion` name that follows from the pinned key in
`secrets/tor/monerod_hs_ed25519_secret_key`, see *Provisioning an onion identity*) and `monero.i2p_hostname` (the `.b32.i2p` destination behind
`/var/lib/i2pd/monero-mainnet.dat`). They identify this node, so they are not written into the
modules; `modules/services.nix` appends one `anonymous-inbound` option per address to monerod.conf
while the daemon starts. Both entries start out empty (sops does not encrypt empty strings), and an
empty one is skipped rather than handed to monerod, which would exit on an empty address.

Only the I2P destination still has to be read off the machine after the fact: tor's name comes from
the key in `secrets/tor/` (see *Provisioning an onion identity*), so it is known before anything
starts. After i2pd has started for the first time:

```bash
# http://127.0.0.1:7070/?page=i2p_tunnels         # -> monero.i2p_hostname (the .b32 name)
nix shell nixpkgs#sops -c sops secrets/monero.yaml   # paste it, save, then rebuild
```

### Provisioning an onion identity

`modules/tor.nix` runs one v3 onion service for this node's Monero side (monerod's P2P and restricted
RPC, plus p2pool - see the port table). Without further ado tor invents the name on first start, so
the address is not in the repository until somebody reads it off PC-001 and pastes it into
`secrets/monero.yaml`, and a reinstalled machine whose `/var/lib/tor/onion/monerod` went missing gets
a different address. Committing the *private key* instead fixes both, because a v3 address is nothing
but a rendering of its public key:

```bash
./scripts/make-onion-key.sh                # mint a fresh identity; prints the .onion that belongs to it
```

or, to keep an address PC-001 already has, hand the script that machine's key (it needs root there):

```bash
ssh PC-001 sudo cat /var/lib/tor/onion/monerod/hs_ed25519_secret_key > /tmp/onion-key
./scripts/make-onion-key.sh import /tmp/onion-key && shred -u /tmp/onion-key
```

Either way the script writes `secrets/tor/monerod_hs_ed25519_secret_key`. It is a sops file like the
others, but it holds one whole key file instead of a document (`sops -e --input-type binary`, read
back by sops-nix with `format = "binary"`), because tor's token file is not text: a 29-byte
`== ed25519v1-secret: type0 ==` header NUL-padded to 32 bytes, then the 64-byte expanded ed25519 key.
Nothing else needs storing - on every start tor loads that one file, derives `hs_ed25519_public_key`
and rewrites `hostname` from it, which is what makes the address reproducible rather than machine-
local.

Then put the printed address into `secrets/monero.yaml` as `monero.tor_hostname` (that is what
`modules/services.nix` appends to monerod.conf and what `modules/p2pool.nix` announces to p2pool's
peers) and rebuild. With the key present tor keeps the service directory in tmpfs rather than under
`/var/lib/tor`, so check that it agrees:

```bash
sudo cat /run/tor/onion/monerod/hostname      # must equal monero.tor_hostname
```

A `/var/lib/tor/onion/monerod` left over from before the identity was pinned is unused and can go.
Deleting `secrets/tor/monerod_hs_ed25519_secret_key` retires the address, and whoever can decrypt that
file controls it - so if it leaks anywhere it was not meant to, move to a new one.

### Adding a secret to a service

Declare it in the service's module and reference the rendered path, e.g. `modules/sabnzbd.nix`
declares `sops.secrets."sabnzbd/newshosting/password"` and hands the placeholder
`@sabnzbd-newshosting-password@` to `services.sabnzbd.secretValues`, so the credential is only ever
substituted into the ini file while the service starts.

## Pre-push validation

`.githooks/pre-push` refuses to push while the configuration does not evaluate.
It runs two evaluation-only checks (nothing is built):

```bash
nix flake check --no-build
nix eval --raw .#nixosConfigurations.PC-001.config.system.build.toplevel.drvPath
```

Hooks are stored per clone, so enable it once after cloning or pulling:

```bash
git config core.hooksPath .githooks
```

Switches for individual pushes (`git push --no-verify` also bypasses the hook):

| Variable | Effect |
| --- | --- |
| `NIX_PREPUSH_BUILD=dry` | additionally show what a rebuild would build (`nix build --dry-run`) |
| `NIX_PREPUSH_BUILD=full` | additionally build the whole system (slow, needs substitutes or a long compile) |
| `NIX_PREPUSH_TIMEOUT=120` | per-command timeout, so a stalled fetch cannot wedge a push |
| `SKIP_NIX_CHECK=1` | skip validation for this push |
| `NIX_PREPUSH_SYSTEM=PC-001` | which `nixosConfigurations` entry to check |
| `NIX_PREPUSH_FLAKE=.` | flake reference to check |

Timing on this machine with a warm Nix cache: about 25 s when Nix can reuse an
already copied source tree (dirty work tree, or a second run of the same commit),
about two minutes for the first push of each new commit. The difference comes
from `self.submodules = true` in `flake.nix`: with a clean work tree Nix copies
the flake together with both nixpkgs submodule trees into `/nix/store` (roughly
1 GB per distinct commit) before it can evaluate. `nixos-rebuild switch --flake`
pays the same price, so this is not specific to the hook; old copies go away with
`nix-collect-garbage -d`. The first run after a fresh clone additionally fetches
the nixpkgs history from the `../nixpkgs` remote (about 1 GiB, up to half an hour
on a slow link), so run one of the commands above by hand once after cloning. The
`warning: not writing modified lock file` line is expected: `flake.lock` is
gitignored and the hook passes `--no-write-lock-file` so it never touches the
working tree.
