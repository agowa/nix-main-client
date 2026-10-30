# BUGS.md — configuration review

Reviewed at commit `91e798e` (after fast-forward pull + `git submodule update`).
All modules pass `nix-instantiate --parse`. Line numbers refer to this commit.

**Status legend:** `Open` = confirmed in source · `Verify on host` = cannot be proven from files alone ·
`Fixed upstream` = resolved in commits pulled today · `Documented / accepted` = author added rationale, behaviour unchanged · `Withdrawn` = original claim was wrong.

| # | Sev | Status | Location | Summary |
| --- | ----- | -------- | ---------- | --------- |
| 1 | High | Open (verify on host) | `modules/networking.nix:130` | `networkConfig` is under **`00-ovsbr0`** while `matchConfig`/`linkConfig` are under **`60-ovsbr0`**. The generated `.network` therefore has no `[Match]` section, so it matches *every* interface, and its `00-` prefix sorts ahead of `40-vlan10` — networkd can apply `DHCP=no` / `LinkLocalAddressing=no` to `vlan10`. Likely a typo for `60-ovsbr0`. |
| 2 | Med | Open | `modules/desktop.nix:55` | `ensureDefaultPrinter = "Samsung_C48x_Series_TEST"` but both printer definitions are commented out, so `ensurePrinters` is empty — the CUPS default-printer setter targets a printer that does not exist. |
| 3 | Med | Verify on host | `modules/boot.nix:28` vs `modules/filesystems.nix:15` | Kernel param `rd.luks.options=discard` applies discard to all initrd LUKS devices and likely overrides the deliberate `allowDiscards = false` on the swap volume (NixOS omits the option when false, so the cmdline default wins). |
| 4 | Med | Verify on host | `hardware-configuration.nix:30` vs `modules/filesystems.nix:12` | `swapDevices` points at a raw `by-uuid`, while a LUKS mapping commented `# swap` (`luks-57e4edfa…`) is configured elsewhere. If the raw device is used, swap is unencrypted. |
| 5 | High | Documented / accepted (risk remains) | `modules/firewall.nix:10-11` | `allowedUDPPortRanges = [] ++ config.services.aria2.settings.listen-port` inherits aria2's *module default* range **UDP 6881–6999**, although `services.aria2` is never enabled (ad-hoc CLI use only). Dead ports open on the public interface, and firewall contents silently track nixpkgs defaults. |
| 6 | High | Open | `modules/i2pd.nix:9` | `reseed.verify = false` accepts an unsigned/tampered seed bundle (`i2pseeds.su3` committed in-tree), enabling router-poisoning / Sybil seeding of the local I2P node. |
| 7 | Med-High | Open | `modules/programs.nix:7` + `modules/networking.nix:230` | `VerifyHostKeyDNS yes` while resolved has `DNSSEC = false`: SSHFP lookups are unauthenticated, so a DNS-capable on-path attacker can inject host keys and defeat TOFU. |
| 8 | Med | Open | `modules/services.nix:50-54` | OctoPrint bound to `::` port 5000 with `openFirewall = true` and no auth/reverse proxy — exposed until the first-run wizard completes. |
| 9 | Med | Open | `modules/services.nix:27,30` | `GatewayPorts = "yes"` lets inbound clients bind `-R` forwards on non-loopback addresses of this host; `X11Forwarding = true` adds further surface. |
| 10 | Low-Med | Open | `modules/firewall.nix:18` | Port `27152` still open, self-labelled "temporary for testing only". |
| 11 | Low-Med | Open | `modules/security.nix:10`, `modules/services.nix:4` | Blanket `allowUnfree = true` (twice) plus `hardware.enableAllFirmware` — broader unfree/firmware acceptance than the handful of packages that need it. Also misplaced in `security.nix`. |
| 12 | High | **Fixed upstream** (`5b9e9e5`) | `modules/system.nix:13-16` | `autoUpgrade` rebuilt from a *channel* while the system is flake-based with pinned nixpkgs, so unattended rebuilds drifted from committed config. Now `enable = false`; the stale `channel = …` line remains but is inert. |
| 13 | Med | Open (style part fixed) | `modules/programs.nix:27-29` | Trailing `Host *` sets `Port 2222` + `User root`, so every host not matched earlier is contacted on port 2222 as root. Indentation was corrected in `d5f7efa`; the behaviour itself is unchanged and still surprising for ordinary `ssh user@host`. |
| 14 | Low | Open | `modules/programs.nix:11-12` | `ControlMAster auto` + `ControlPersist 60s` with no `ControlPath`: connection multiplexing is silently inert. (Key name is case-insensitive, so the typo itself is harmless.) |
| 15 | Med | Open | `modules/services.nix:6,16` | A second full nixpkgs instance (`inputs.nixos-unstable`) is imported solely for `unifi`, which is `enable = false` — pure evaluation/store cost. Gate the import on the service being enabled. |
| 16 | Low-Med | Open | `modules/system.nix:22-27` | `nix-index` enabled with all shell integrations disabled and no `nix-index-database` input, so a local index is built that nothing invokes. |
| 17 | — | **Withdrawn** (documented) | `modules/filesystems.nix:30-40` | Claimed `strictatime` contradicts the "improve SSD performance" comment. Author notes `lazytime` defers those inode updates, so the penalty is largely avoided. Claim overstated. |
| 18 | Low-Med | Verify on host | `modules/filesystems.nix:19-28` + `49-53` | Both `environment.etc.crypttab` and `fileSystems.<>.encrypted.blkDev` describe the same RAID5 LUKS volume; worth confirming only one actually opens it (see nixpkgs#459869 reference in-file). |
| 19 | Low | Open | `modules/hardware.nix:17-18` | Intel CPU microcode updates and `cpu.intel.sgx.provision` enabled on an AMD host (`kvm-amd`, `amd_pstate`) — SGX provisioning can never act. Intel *GPU* settings are legitimately needed. |
| 20 | Low | Open | `modules/hardware.nix:52` | Legacy `nixpkgs.config.packageOverrides`; prefer `nixpkgs.overlays` so the override is visible consistently to all consumers. |
| 21 | Low | Open | `modules/boot.nix:33-106` | `boot.initrd.kernelModules` and `boot.kernelModules` are byte-identical (verified by diffing the two blocks); several entries (`btusb`, `sr_mod`, `v4l2loopback`) do not belong in the initrd. Factor into one shared list. |
| 22 | Low | Open | `modules/boot.nix:9` | `vm.max_map_count = 2147483647` (INT_MAX) removes the guard that surfaces map leaks; ~1048576 satisfies Wine/Steam needs. |
| 23 | Low | Verify on host | `modules/systemd.nix:48` | `systemd-user-sessions.after = lib.mkForce []` drops ordering so logins may be permitted before local filesystems are ready. Prefer explicit `After=local-fs.target remote-fs.target`. |
| 24 | Low | **Fixed upstream** (`9b82fd9`, `be93fb4`) | `modules/environment.nix`, `modules/users.nix` | Duplicate packages: `libva-utils` ×2, `keepassxc` ×2. Both removed upstream in this pull (one dead `lmstudio` comment block also dropped). |
| 25 | Low | Open | `flake.nix:12` | `self.lfs = true` is inert: no `.gitattributes` exists and `i2pseeds.su3` reports `filter: unspecified`, so nothing is LFS-managed. |
| 26 | Low | Partially addressed | `modules/services.nix:91-146`, `modules/systemd.nix:10-43`, `flake.nix:8-10` | Large commented-out blocks retained (home-assistant, two `beesd` variants, sshfs mounts, unused flake inputs). Git history preserves these better. One such block removed upstream today. |
| 27 | Low | Open (process) | repo-wide | No automated evaluation gate: GitHub enforces verified signatures (GH013) and nothing checks that the config evaluates before `master` moves. A pre-push hook running `nix flake check` / a `--dry-run` rebuild would catch eval breakage. |
| 28 | Low | Open | `modules/filesystems.nix:36` | `fsType = "ntfs-3g"` uses the FUSE driver, deprecated upstream; kernel `ntfs3` is faster and needs no FUSE round-trip. |

## Suggested verification commands (run on PC-001)

```sh
networkctl status vlan10                      # item 1: which .network file won?
ls /etc/systemd/network | sort                # item 1: 00-ovsbr0.network present?
lpstat -p 2>/dev/null; systemctl status cups* # item 2
dmsetup table --showoptions                   # item 3: discards on swap volume?
swapon --noheadings; ls -l /dev/mapper/       # item 4: is swap on a mapper?
systemctl list-units 'systemd-cryptsetup@*'   # item 18: which mapping opened the RAID5 disk?
```

## Notes

- Items 12 and 24 were fixed upstream between the first review and this re-validation.
- Item 17 is withdrawn — my performance claim did not hold up against `lazytime` semantics.
- Items 3, 4, 18, 23 are plausible-but-unproven from source alone; they need runtime state from the actual host.
