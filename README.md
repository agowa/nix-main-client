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
| monerod ZMQ RPC | TCP 18082 | `127.0.0.1` | No (loopback only) | not set anywhere in this repo — port and address come from monerod's own defaults |
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
- **Outbound only (no inbound service):** systemd-timesyncd to the NTP pool servers, fwupd, i2pd's reseed and UPnP/IGD traffic, monerod dialing out to peers. snowflake-proxy belongs here too in the sense that it never accepts a connection — it still binds dynamic WebRTC sockets, as its table row says.
- **Guest VMs:** a QEMU guest started with a graphical display opens its own VNC/SPICE listener (normally `127.0.0.1:59xx`) — created by the hypervisor at runtime, not by this configuration.
- **ICMP echo requests are answered** (`networking.firewall.allowPing` default `true`).
- **Currently disabled or commented out**, so they contribute no ports today: `services.unifi` (would open its own set including TCP 8080), `services.jellyfin` (TCP 8096), home-assistant (TCP 8123), `programs.kdeconnect` (UDP 1714-1764), `services.miniupnpd` (UDP 1900 + NAT-PMP/PCP), i2pd's yggdrasil transport, tor's control socket (`controlSocket.enable = false`, so only the unix socket exists). Re-check this section after enabling any of them.

### Verifying on the host

The evaluated firewall lists at the time of writing — every entry is accounted for by a row above:

```
allowedTCPPorts      = [ 22 443 5000 6969 7000 8080 18080 19410 27040 27151 28549 29605 ]
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
