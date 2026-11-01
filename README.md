# nix-main-client

This repository contains the nix configuration for my main clients (testing status right now).

## Build and switch current system to this configuration

```bash
nixos-rebuild switch --flake . --sudo
```

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
