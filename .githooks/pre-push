#!/usr/bin/env bash
#
# pre-push: refuse to push a Nix configuration that does not evaluate.
#
# Always runs (evaluation only, nothing is built):
#   1. nix flake check --no-build
#        every flake output must evaluate; catches syntax and type errors
#        anywhere in the flake (~7 s warm on PC-001).
#   2. nix eval --raw .#nixosConfigurations.PC-001.config.system.build.toplevel.drvPath
#        the actual system configuration must instantiate to a derivation,
#        which is stricter than evaluating the flake (~16 s warm).
#
# A cold Nix git cache makes both commands far slower: flake.nix sets
# `self.submodules = true`, so Nix first fetches the nixpkgs submodule history
# from the ../nixpkgs remote (about 1 GiB, tens of minutes on a slow link).
# Run either command by hand once after a fresh clone to warm the cache.
#
# The "warning: not writing modified lock file" line is expected: flake.lock is
# gitignored in this repo and --no-write-lock-file keeps this hook free of side
# effects on the working tree.
#
# Hooks are per-clone, so enable it once after cloning / pulling this repo:
#   git config core.hooksPath .githooks
#
# Environment switches (all optional):
#   NIX_PREPUSH_BUILD=dry|full  additionally run nix build --dry-run (what would
#                               be built) or a real full build of the toplevel
#   NIX_PREPUSH_TIMEOUT=SECONDS per-command timeout, needs timeout(1); default: no limit
#   SKIP_NIX_CHECK=1            skip validation for this push (like git push --no-verify)
#   NIX_PREPUSH_FLAKE=.         flake reference to check
#   NIX_PREPUSH_SYSTEM=PC-001   nixosConfiguration to check

set -uo pipefail

zero_sha=0000000000000000000000000000000000000000   # the "no such commit" marker git sends for deletions

# --- locate the work tree ----------------------------------------------------
if ! top_level=$(git rev-parse --show-toplevel 2>/dev/null); then
    echo "pre-push: not inside a git work tree, skipping nix validation." >&2
    exit 0
fi
cd "$top_level" || exit 1

# --- consume stdin -----------------------------------------------------------
# git feeds one line per ref being pushed:
#   <local-ref> <local-sha> <remote-ref> <remote-sha>
# Read all of it: leaving it unread can make git die on EPIPE.
refs=()
pushing_content=0
while read -r local_ref local_sha remote_ref remote_sha; do
    [ -n "${local_ref:-}" ] || continue
    refs+=("$local_ref -> $remote_ref ($local_sha)")
    # A deletion has an all-zero local sha and pushes no new content.
    [ "$local_sha" = "$zero_sha" ] || pushing_content=1
done

if [ ${#refs[@]} -eq 0 ]; then
    exit 0                                   # nothing to push
fi

if [ "${SKIP_NIX_CHECK:-}" != "" ] && [ "${SKIP_NIX_CHECK:-}" != "0" ]; then
    echo "pre-push: SKIP_NIX_CHECK=$SKIP_NIX_CHECK is set, skipping nix validation." >&2
    exit 0
fi

if [ "$pushing_content" -eq 0 ]; then
    echo "pre-push: only remote refs are being deleted, nothing to evaluate."
    exit 0
fi

# --- preflight ---------------------------------------------------------------
fail() {
    printf '\npre-push: %s\n' "$1" >&2
    exit 1
}

command -v nix >/dev/null 2>&1 || fail "nix not found in PATH; install Nix or push with --no-verify."
[ -f flake.nix ] || fail "flake.nix not found at $top_level, cannot validate the configuration."

# The flake reads nixpkgs from path:./nixpkgs/<branch>, i.e. from the submodules.
# Without them evaluation fails with a message that has nothing to do with the
# config being pushed (see AGENT.md: init/update submodules after every pull).
missing_submodules=""
while read -r key path; do
    [ -n "${path:-}" ] || continue
    if [ -z "$(ls -A "$path" 2>/dev/null)" ]; then
        missing_submodules="$missing_submodules $path"
    fi
done < <(git config -f .gitmodules --get-regexp '\.path$' 2>/dev/null)

if [ -n "$missing_submodules" ]; then
    fail "submodule(s) not checked out:$missing_submodules — run 'git submodule update --init --recursive'."
fi

flake=${NIX_PREPUSH_FLAKE:-.}
system=${NIX_PREPUSH_SYSTEM:-PC-001}
build_mode=${NIX_PREPUSH_BUILD:-}
toplevel_attr="nixosConfigurations.$system.config.system.build.toplevel"

case "$build_mode" in
    ""|0|no|off|false|dry|dry-run|--dry-run|1|true|yes|full) ;;
    *) fail "unknown NIX_PREPUSH_BUILD='$build_mode' (expected dry or full)." ;;
esac

# Wrap a nix invocation in an optional timeout so a stalled fetch cannot wedge pushes.
run_nix() {
    if [ -n "${NIX_PREPUSH_TIMEOUT:-}" ]; then
        if command -v timeout >/dev/null 2>&1; then
            local rc=0
            timeout "$NIX_PREPUSH_TIMEOUT" "$@" || rc=$?
            if [ $rc -eq 124 ] || [ $rc -eq 137 ]; then
                echo "pre-push: timed out after ${NIX_PREPUSH_TIMEOUT}s: $*" >&2
                echo "pre-push: raise NIX_PREPUSH_TIMEOUT, warm the cache by hand, or push with --no-verify." >&2
            fi
            return $rc
        fi
        echo "pre-push: NIX_PREPUSH_TIMEOUT set but timeout(1) is unavailable, running without a limit." >&2
    fi
    "$@"
}

echo "pre-push: validating nix configuration in $top_level (flake: $flake)"
for r in "${refs[@]}"; do
    echo "           $r"
done

status=0
start_all=$(date +%s)

# --- 1. every flake output must evaluate -------------------------------------
echo "pre-push: [1/2] nix flake check --no-build"
if ! run_nix nix flake check --no-build --no-write-lock-file "$flake"; then
    echo "pre-push: 'nix flake check' failed." >&2
    status=1
fi

# --- 2. the system configuration must instantiate ----------------------------
echo "pre-push: [2/2] nix eval $toplevel_attr.drvPath"
if drv=$(run_nix nix eval --raw --no-write-lock-file "$flake#$toplevel_attr.drvPath"); then
    echo "           toplevel derivation: ${drv:-<empty>}"
else
    echo "pre-push: evaluating $system failed." >&2
    status=1
fi

# --- optional build ----------------------------------------------------------
case "$build_mode" in
    ""|0|no|off|false) ;;
    dry|--dry-run|dry-run)
        echo "pre-push: [extra] nix build --dry-run $toplevel_attr (NIX_PREPUSH_BUILD=dry)"
        run_nix nix build --no-link --dry-run "$flake#$toplevel_attr" || {
            echo "pre-push: dry-run rebuild failed." >&2
            status=1
        }
        ;;
    1|true|yes|full)
        echo "pre-push: [extra] full build of $toplevel_attr (NIX_PREPUSH_BUILD=$build_mode, this can take a long time)"
        run_nix nix build --no-link --print-out-paths "$flake#$toplevel_attr" || {
            echo "pre-push: building the system failed." >&2
            status=1
        }
        ;;
    *)
        fail "unknown NIX_PREPUSH_BUILD='$build_mode' (expected dry or full)."
        ;;
esac

elapsed=$(( $(date +%s) - start_all ))

if [ $status -ne 0 ]; then
    printf '\npre-push: nix validation FAILED after %ss. Nothing was pushed.\n' "$elapsed" >&2
    echo "Fix the configuration, or push with --no-verify / SKIP_NIX_CHECK=1 to override deliberately." >&2
    exit $status
fi

printf 'pre-push: nix configuration validated in %ss.\n' "$elapsed"
exit 0
