#!/usr/bin/env bash
#
# Put this node's v3 onion identity into the repository, encrypted with sops.
#
# The file this writes (secrets/tor/monerod_hs_ed25519_secret_key) is what makes the .onion name
# come from the configuration instead of being invented by tor on PC-001's first start: modules/tor.nix
# hands it to services.tor.relay.onionServices."monerod".secretKey, and tor derives the public key
# and the address from it on every start. See README.md, "Provisioning an onion identity".
#
#   ./scripts/make-onion-key.sh                 # mint a fresh identity (new .onion)
#   ./scripts/make-onion-key.sh import <file>   # adopt a key that is already live on PC-001
#
# To keep an address the machine already has, copy its private key out first - it needs root there:
#
#   ssh PC-001 sudo cat /var/lib/tor/onion/monerod/hs_ed25519_secret_key > /tmp/onion-key
#   ./scripts/make-onion-key.sh import /tmp/onion-key && shred -u /tmp/onion-key
#
# The private key is what controls the address: mint it somewhere you trust, keep the plaintext out
# of the repository (this script only ever writes the encrypted form) and treat a leak as a reason to
# retire the address.

set -euo pipefail

REPO_ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
OUT="$REPO_ROOT/secrets/tor/monerod_hs_ed25519_secret_key"
SERVICE_DIR_NAME="monerod"   # must match the onionServices.<name> in modules/tor.nix

die() { echo "error: $*" >&2; exit 1; }

[ -e "$OUT" ] && die "$OUT already exists - overwriting it would retire a live .onion address.
Remove it by hand if that is really what you want."

MODE="${1:-generate}"
case "$MODE" in generate|import) ;; *) sed -n '3,20p' "$0"; exit 2 ;; esac
[ "$MODE" = import ] && [ $# -lt 2 ] && die "import needs the path to an hs_ed25519_secret_key file"

# Everything unencrypted lives in a 0700 directory that is wiped on the way out.
TMP="$(mktemp -d)"
TOR_PID=""
stop_tor() {
  [ -n "$TOR_PID" ] || return 0
  pkill -P "$TOR_PID" 2>/dev/null || true
  kill "$TOR_PID" 2>/dev/null || true
}
cleanup() {
  stop_tor
  find "$TMP" -type f -exec shred -u {} + 2>/dev/null
  rm -rf "$TMP"
  rm -f "${OUT}.tmp"   # a failed run must not leave anything in the repository either
}
trap cleanup EXIT
trap 'exit 130' INT TERM   # so that the trap above actually runs when interrupted

HS_DIR="$TMP/onion/$SERVICE_DIR_NAME"
KEY="$HS_DIR/hs_ed25519_secret_key"
install -d -m 0700 "$HS_DIR" "$TMP/data"

# tor's own token file: a 29-byte header NUL-padded to 32, then the 64-byte expanded ed25519 key.
# Checking both catches having handed over something else entirely (a hostname file, a v2 key, a
# truncated copy) - and tor itself is no help here, because it answers an unreadable key by quietly
# generating a fresh one rather than by failing.
is_tor_token_file() {
  [ "$(wc -c < "$1")" = 96 ] || return 1
  # cmp rather than a string compare: the file is binary and bash warns about (and drops) NUL bytes
  # read through $( ).
  printf '== ed25519v1-secret: type0 ==' | cmp -s - <(head -c 29 "$1")
}

IMPORTED_SHA=""
case "$MODE" in
  import)
    install -m 0600 "$2" "$KEY"
    is_tor_token_file "$KEY" || die "$2 is not a tor hs_ed25519_secret_key (want 96 bytes starting with '== ed25519v1-secret: type0 =='): $(wc -c < "$KEY") bytes, first bytes $(od -An -tx1 -N8 "$KEY" | tr -s ' ')"
    IMPORTED_SHA="$(sha256sum < "$KEY")"
    ;;
esac

# tor itself is the only thing that knows its own key format for certain, so let it do both jobs:
# create a key pair (generate) and print the address that belongs to whichever key is present now.
TOR="${TOR_BIN:-}"
if [ -z "$TOR" ]; then
  echo "getting tor from the pinned nixpkgs ..." >&2
  TOR="$(nix build --impure --no-link --print-out-paths \
           --expr "(import $REPO_ROOT/nixpkgs/nixos-26.05 {}).tor")/bin/tor"
fi

cat > "$TMP/torrc" <<TORRC
DataDirectory $TMP/data
SocksPort 0
ORPort 0
AvoidDiskWrites 0
HiddenServiceDir $HS_DIR
HiddenServiceVersion 3
HiddenServicePort 18084 127.0.0.1:18084
TORRC

# The exit status goes into a file because the loop below cannot tell a live tor from an exited one
# that has not been reaped yet (`kill -0` answers for zombies too).
( "$TOR" -f "$TMP/torrc" >"$TMP/tor.log" 2>&1; echo "$?" > "$TMP/tor.exit" ) &
TOR_PID=$!

# tor registers its hidden services (and thus writes the key files) only once it has directory
# info, so this waits on the network rather than on disk: up to two minutes, with a note after the
# first half-minute so a silent run is not mistaken for a stuck one.
echo "waiting for tor to create $SERVICE_DIR_NAME's key pair and hostname ..." >&2
for i in $(seq 1 240); do
  [ -s "$HS_DIR/hostname" ] && break
  if [ -e "$TMP/tor.exit" ]; then
    cat "$TMP/tor.log" >&2
    die "tor exited (status $(cat "$TMP/tor.exit")) before writing a hostname"
  fi
  [ "$i" = 60 ] && echo "  (still bootstrapping: fetching the Tor consensus takes a moment)" >&2
  sleep 0.5
done

if [ ! -s "$HS_DIR/hostname" ]; then
  cat "$TMP/tor.log" >&2 || true
  die "tor wrote no hostname within 120 s - it needs to reach the Tor network once; see its log above"
fi

stop_tor    # tor does not stop on its own; the helper also covers an interrupted run via cleanup()
wait "$TOR_PID" 2>/dev/null || true

ONION="$(cut -d: -f1 < "$HS_DIR/hostname")"

is_tor_token_file "$KEY" || die "tor left no usable hs_ed25519_secret_key behind (want 96 bytes with tor's header, found $(wc -c < "$KEY") bytes)"

if [ -n "$IMPORTED_SHA" ] && [ "$(sha256sum < "$KEY")" != "$IMPORTED_SHA" ]; then
  die "tor rewrote the imported key instead of loading it - it would have minted a *new* address. The file is fine on its own, so this usually means it came from a v2 service or was copied with converted line endings."
fi

install -d -m 0755 "$(dirname "$OUT")"
if command -v sops >/dev/null; then SOPS=(sops); else SOPS=(nix shell nixpkgs#sops -c sops); fi
# Both flags are needed and both say why: sops picks its recipients by matching path_regex against
# the *input* file's path, and that input is deliberately outside the repository (it is plaintext).
# `--config` names the rule file explicitly, `--filename-override` hands sops the repository path
# the rules should be matched against - without them the run fails with "no matching creation rules
# found", and listing recipients on the command line instead would let them drift from .sops.yaml.
"${SOPS[@]}" --config "$REPO_ROOT/.sops.yaml" \
             --filename-override "$OUT" \
             -e --input-type binary "$KEY" > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"

echo
echo "encrypted identity written: ${OUT#$REPO_ROOT/}"
echo "  address      $ONION"
echo
echo "next: record that address, it is what the modules and monerod announce -"
echo
echo "  nix shell nixpkgs#sops -c sops $REPO_ROOT/secrets/monero.yaml"
echo "      monero:"
echo "          tor_hostname: \"$ONION\""
echo
echo "then rebuild and check that tor agrees:"
echo
echo "  sudo cat /run/tor/onion/$SERVICE_DIR_NAME/hostname"
