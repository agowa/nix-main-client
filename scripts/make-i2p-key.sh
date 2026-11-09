#!/usr/bin/env bash
#
# Put this node's I2P destination identity into the repository, encrypted with sops.
#
# The file this writes (secrets/i2p/monero_mainnet_keys.dat) is what makes the .b32.i2p name come
# from the configuration instead of being invented by i2pd on PC-001's first start: modules/i2pd.nix
# installs it as /var/lib/i2pd/monero-mainnet.dat, the keyset that every tunnel under this node's
# destination shares (monerod's P2P and RPC plus p2pool's sidechain P2P and stratum), and i2pd
# derives the address from it on every start. See README.md, "Provisioning an I2P destination".
#
# It then writes that address into secrets/monero.yaml as monero.i2p_hostname, because that is where
# modules/services.nix (monerod's anonymous-inbound) and modules/p2pool.nix (--i2p-address) read it
# from. Both halves are written by this one run on purpose: the address follows from the key, so
# storing one without the other leaves the node either announcing an address its key does not own
# or - with the entry left empty - running monerod without anonymous inbound P2P over I2P and
# keeping p2pool from advertising its destination at all.
#
#   ./scripts/make-i2p-key.sh                  # mint a fresh destination (new .b32.i2p)
#   ./scripts/make-i2p-key.sh import <file>    # adopt a keyset that is already live on PC-001
#   ./scripts/make-i2p-key.sh record           # re-derive the address of the committed keyset
#
# To keep an address the machine already has, copy its keyset out first - it needs root there:
#
#   ssh PC-001 sudo cat /var/lib/i2pd/monero-mainnet.dat > /tmp/i2p-keys.dat
#   ./scripts/make-i2p-key.sh import /tmp/i2p-keys.dat && shred -u /tmp/i2p-keys.dat
#
# Unlike the onion identity, none of this needs a network: an I2P address is a hash of the
# destination, so i2pd prints it while loading the keyset and never has to reach the rest of the
# network first. `record` therefore works anywhere, as long as the private half of one of the
# recipients in .sops.yaml is at hand to re-encrypt secrets/monero.yaml.
#
# The private key is what controls the address: mint it somewhere you trust, keep the plaintext out
# of the repository (this script only ever writes the encrypted form) and treat a leak as a reason to
# retire the address.

set -euo pipefail

REPO_ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
OUT="$REPO_ROOT/secrets/i2p/monero_mainnet_keys.dat"
MONERO_SECRETS="$REPO_ROOT/secrets/monero.yaml"   # where the derived address is recorded
KEYS_NAME="monero-mainnet.dat"  # must match `keys` of every tunnel in modules/i2pd.nix
P2P_TUNNEL_PORT=18085           # monerod-p2p's target port, used only by the check below

die() { echo "error: $*" >&2; exit 1; }

MODE="${1:-generate}"
case "$MODE" in generate|import|record) ;; *) sed -n '18,25p' "$0"; exit 2 ;; esac
[ "$MODE" = import ] && [ $# -lt 2 ] && die "import needs the path to an i2pd keyset (a monero-mainnet.dat)"

if [ "$MODE" != record ]; then
  [ -e "$OUT" ] && die "$OUT already exists - overwriting it would retire a live .b32.i2p address.
Remove it by hand if that is really what you want, or run '$(basename "$0") record' to write the
address of the identity that is committed right now into secrets/monero.yaml."
fi

# Everything unencrypted lives in a 0700 directory that is wiped on the way out.
TMP="$(mktemp -d)"; chmod 0700 "$TMP"   # install -d rather than mktemp -m: busybox has no -m
I2PD_PID=""
stop_i2pd() {
  [ -n "$I2PD_PID" ] || return 0
  pkill -P "$I2PD_PID" 2>/dev/null || true
  kill "$I2PD_PID" 2>/dev/null || true
}
cleanup() {
  stop_i2pd
  find "$TMP" -type f -exec shred -u {} + 2>/dev/null
  rm -rf "$TMP"
  rm -f "${OUT}.tmp" "${MONERO_SECRETS}.tmp"   # a failed run must not leave anything in the repo
}
trap cleanup EXIT
trap 'exit 130' INT TERM

if command -v sops >/dev/null; then SOPS=(sops); else SOPS=(nix shell nixpkgs#sops -c sops); fi

TOOLS="${I2P_TOOLS_BIN:-}"
if [ -z "$TOOLS" ]; then
  echo "getting i2pd-tools from the pinned nixpkgs ..." >&2
  TOOLS="$(nix build --impure --no-link --print-out-paths \
             --expr "(import $REPO_ROOT/nixpkgs/nixos-26.05 {}).i2pd-tools")"
fi
KEYGEN="$TOOLS/bin/keygen"; KEYINFO="$TOOLS/bin/keyinfo"
[ -x "$KEYGEN" ] && [ -x "$KEYINFO" ] || die "keygen and keyinfo are not in $TOOLS/bin"

KEY="$TMP/$KEYS_NAME"

case "$MODE" in
  generate)
    # keygen with no signature type given creates ED25519-SHA512 (signing key type 7), which is also
    # what i2pd itself writes when a tunnel still has no keyset - checked against both: `keyinfo -v`
    # reports "Signature Type: ED25519-SHA512, Encryption Type: 0" for either file, and the two are
    # the same length. Minting another type would work but would make this destination look unlike
    # one i2pd had made on its own, for no gain.
    "$KEYGEN" "$KEY" >/dev/null || die "keygen could not create a keyset"
    ;;
  import)
    install -m 0600 "$2" "$KEY"
    ;;
  record)
    [ -e "$OUT" ] || die "no identity committed at ${OUT#$REPO_ROOT/} yet - run '$(basename "$0")' to create one"
    # --output-type binary because this is a whole key file rather than a YAML document, exactly the
    # way modules/i2pd.nix reads it back with format = "binary".
    "${SOPS[@]}" -d --output-type binary "$OUT" > "$KEY" \
      || die "could not decrypt ${OUT#$REPO_ROOT/} - that needs the private half of one of the recipients in .sops.yaml"
    chmod 0600 "$KEY"
    ;;
esac

# keyinfo is upstream's own reader for this file format, so it doubles as the validator: a truncated
# copy, a text destination (`keyinfo -d` output rather than the keyset) or a file with converted line
# endings all come back as "bad key file format" instead of an address. Checking here is what keeps
# such a file from being committed at all - i2pd answers an unreadable keyset by quietly minting a
# new one, so nothing downstream would ever complain about the address changing.
ADDR="$("$KEYINFO" "$KEY" 2>/dev/null)" \
  || die "$KEYS_NAME is not an i2pd keyset that this i2pd-tools can read (keyinfo: bad key file format)"
[[ "$ADDR" =~ ^[a-z2-7]{52}\.b32\.i2p$ ]] \
  || die "keyinfo printed '$ADDR', which is not a .b32.i2p address (52 lowercase base32 characters plus the suffix)"

if [ "$MODE" != record ]; then
  I2PD="${I2PD_BIN:-}"
  if [ -z "$I2PD" ]; then
    echo "getting i2pd from the pinned nixpkgs, to check that it accepts this keyset ..." >&2
    I2PD="$(nix build --impure --no-link --print-out-paths \
              --expr "(import $REPO_ROOT/nixpkgs/nixos-26.05 {}).i2pd")/bin/i2pd"
  fi
  BEFORE_SHA="$(sha256sum < "$KEY")"

  # A throwaway router over a temporary datadir, because i2pd is the only thing that knows for certain
  # which keysets it will accept. It logs "Clients: Local address <address> loaded" while starting up,
  # and that line is the whole point of this run: this exact file yields exactly this name in the
  # router that will actually use it. Reaching the I2P network is not required for it - the tunnels
  # stay unbuilt, but the destination is loaded from disk either way - so unlike tor's, this check
  # works with no connectivity at all.
  ROUTER_DIR="$TMP/router"; install -d -m 0700 "$ROUTER_DIR"
  cp "$KEY" "$ROUTER_DIR/$KEYS_NAME"
  # The tunnel monerod-p2p in modules/i2pd.nix, copied: same keyset name (resolved relative to the
  # datadir), same inport. Only the destination matters here, but loading it through a real server
  # tunnel is what i2pd on PC-001 will do.
  cat > "$TMP/tunnels.conf" <<TUNNELS
[monerod-p2p]
type = server
host = 127.0.0.1
port = $P2P_TUNNEL_PORT
keys = $KEYS_NAME
inport = 0
TUNNELS
  # Nothing but turning the two proxies off: i2pd exits on a key it does not recognise, and both of
  # these would otherwise try to bind the ports of the router that may well be running right now.
  printf '[httpproxy]\nenabled = false\n[socksproxy]\nenabled = false\n' > "$TMP/i2pd.conf"

  # The exit status goes into a file because the loop below cannot tell a live i2pd from an exited one
  # that has not been reaped yet (`kill -0` answers for zombies too). --port=0 lets i2pd pick its own
  # transport port so it cannot collide with a running router.
  ( "$I2PD" --datadir="$ROUTER_DIR" --conf="$TMP/i2pd.conf" --tunconf="$TMP/tunnels.conf" \
            --log=stdout --loglevel=info --port=0 >"$TMP/i2pd.log" 2>&1; echo "$?" > "$TMP/i2pd.exit" ) &
  I2PD_PID=$!

  echo "waiting for i2pd to load the destination ..." >&2
  for i in $(seq 1 60); do
    grep -qF -- "Local address $ADDR loaded" "$TMP/i2pd.log" 2>/dev/null && break
    if [ -e "$TMP/i2pd.exit" ]; then
      cat "$TMP/i2pd.log" >&2
      die "i2pd exited (status $(cat "$TMP/i2pd.exit")) before loading the destination"
    fi
    sleep 0.5
  done

  if ! grep -qF -- "Local address $ADDR loaded" "$TMP/i2pd.log" 2>/dev/null; then
    cat "$TMP/i2pd.log" >&2 || true
    die "i2pd did not report 'Local address $ADDR loaded' within 30 s - it loads keysets while starting up, so this means the file is not what i2pd expects"
  fi

  stop_i2pd       # i2pd does not stop on its own; the helper also covers an interrupted run via cleanup()
  wait "$I2PD_PID" 2>/dev/null || true
  I2PD_PID=""

  if [ "$(sha256sum < "$ROUTER_DIR/$KEYS_NAME")" != "$BEFORE_SHA" ]; then
    die "i2pd rewrote the keyset instead of loading it - it would have minted a *new* address. The file is fine on its own, so this usually means it was copied with converted line endings or came from another I2P implementation."
  fi
fi

install -d -m 0755 "$(dirname "$OUT")"
# Both flags are needed and both say why: sops picks its recipients by matching path_regex against the
# *input* file's path, and that input is deliberately outside the repository (it is plaintext).
# `--config` names the rule file explicitly, `--filename-override` hands sops the repository path the
# rules should be matched against - without them the run fails with "no matching creation rules found",
# and listing recipients on the command line instead would let them drift from .sops.yaml.
if [ "$MODE" != record ]; then
  # `--input-type binary` because a keyset is raw key material, not a document: sops-nix reads the file
  # back with format = "binary" and writes it out verbatim, which is the only way i2pd can use it.
  "${SOPS[@]}" --config "$REPO_ROOT/.sops.yaml" \
               --filename-override "$OUT" \
               -e --input-type binary "$KEY" > "$OUT.tmp"
  # 0600 rather than the umask default: this is encrypted, but it is still private key material and
  # there is no reason for it to be world-readable in a checkout (secrets/p2pool.yaml, monero.yaml).
  chmod 0600 "$OUT.tmp"
  mv "$OUT.tmp" "$OUT"
fi

# The address belongs next to the key it came from. It is a secret for the same reason monerod's onion
# name and p2pool's payout address are: this repository is public and the name identifies this node.
[ -e "$MONERO_SECRETS" ] || die "$MONERO_SECRETS does not exist, so there is nothing to merge into without dropping monero.tor_hostname. Run scripts/make-onion-key.sh first - it creates that file with both entries."
# `sops set` decrypts and re-encrypts in place for the recipients already listed in the file, so every
# other entry in it (monero.tor_hostname above all) survives untouched. The old value is read only to
# warn about a replacement; it never goes to disk.
OLD="$("${SOPS[@]}" --extract '["monero"]["i2p_hostname"]' -d "$MONERO_SECRETS" 2>/dev/null || true)"
OLD="${OLD%\"}"; OLD="${OLD#\"}"
if [ -n "$OLD" ] && [ "$OLD" != "$ADDR" ]; then
  echo "warning: replacing the recorded address $OLD with $ADDR. Peers that learned the old one now" >&2
  echo "         point at a destination that does not exist, and p2pool stops announcing something reachable." >&2
fi
"${SOPS[@]}" --set "[\"monero\"][\"i2p_hostname\"] \"$ADDR\"" "$MONERO_SECRETS" \
  || die "could not write monero.i2p_hostname into $MONERO_SECRETS - that needs the private half of one of the recipients in .sops.yaml to re-encrypt the file. The identity itself is already committed; finish on a machine that has one with: $(basename "$0") record"

echo
echo "encrypted identity written: ${OUT#$REPO_ROOT/}"
echo "  address      $ADDR"
echo "  recorded in  ${MONERO_SECRETS#$REPO_ROOT/} -> monero.i2p_hostname (monerod's anonymous-inbound and"
echo "               p2pool's --i2p-address both read that one entry)"
echo
echo "next: rebuild, then check that i2pd agrees with what was recorded:"
echo
echo "  sudo nixos-rebuild switch --flake ."
echo "  sudo journalctl -u i2pd | grep 'Local address'      # must end in $ADDR"
