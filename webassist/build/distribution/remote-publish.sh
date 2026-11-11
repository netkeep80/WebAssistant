#!/usr/bin/env bash
# Runs on the storage host through SSH. It never builds product artifacts.
set -euo pipefail
fail() { printf 'remote-publish: %s\n' "$*" >&2; exit 1; }
[[ $# -ge 4 ]] || fail 'missing operation/root/product/version'
operation="$1"; root_b64="$2"; product="$3"; version="$4"
[[ "$root_b64" =~ ^[A-Za-z0-9+/=]+$ ]] || fail 'invalid encoded storage path'
root="$(printf '%s' "$root_b64" | base64 -d)" || fail 'invalid storage path encoding'
[[ "$root" == /* && "$root" != / && "$root" != *$'\n'* && "$root" != *$'\r'* ]] || fail 'invalid storage path'
[[ "$product" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ && "$product" != '.' && "$product" != '..' ]] || fail 'invalid product'
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'invalid version'
parent="$root/shared/${product}-build/products/$product/releases"
final="$parent/$version"
lock="$parent/.${version}.publish-lock"

check_stage_name() {
  [[ "$1" =~ ^\.[0-9]+\.[0-9]+\.[0-9]+\.tmp-[0-9]+-[0-9]+\.[A-Za-z0-9]+$ ]] || fail 'invalid staging name'
}

case "$operation" in
  prepare)
    [[ $# == 6 ]] || fail 'prepare requires pipeline and job IDs'
    [[ "$5" =~ ^[0-9]+$ && "$6" =~ ^[0-9]+$ ]] || fail 'invalid pipeline/job ID'
    mkdir -p -- "$parent"
    mkdir -- "$lock" || fail 'publication lock exists; investigate stale transaction before retry'
    if ! stage="$(mktemp -d "$parent/.${version}.tmp-${5}-${6}.XXXXXXXX")"; then
      rmdir -- "$lock" || true
      fail 'cannot allocate staging directory'
    fi
    basename -- "$stage"
    ;;
  finalize)
    [[ $# == 10 ]] || fail 'finalize requires staging, zip name and four SHA-256 values'
    stage_name="$5"; zipname="$6"
    check_stage_name "$stage_name"
    [[ "$zipname" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ && "$zipname" == *"-linux-x64-${version}.zip" ]] || fail 'invalid artifact basename'
    for hash in "${@:7:4}"; do [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || fail 'invalid SHA-256'; done
    [[ -d "$lock" ]] || fail 'missing transaction lock'
    stage="$parent/$stage_name"
    [[ -d "$stage" && ! -L "$stage" ]] || fail 'missing staged release'
    expected="$(printf '%s\n' 'altlinux-x64' "altlinux-x64/$zipname" "altlinux-x64/$zipname.sha256" "altlinux-x64/$zipname.provenance.json" 'manifest.json' | LC_ALL=C sort)"
    actual="$(cd "$stage" && find . -mindepth 1 -printf '%P\n' | LC_ALL=C sort)"
    [[ "$expected" == "$actual" && -d "$stage/altlinux-x64" && ! -L "$stage/altlinux-x64" ]] || fail 'staged release structure mismatch'
    files=("manifest.json" "altlinux-x64/$zipname" "altlinux-x64/$zipname.sha256" "altlinux-x64/$zipname.provenance.json")
    i=7
    for file in "${files[@]}"; do
      [[ -f "$stage/$file" && ! -L "$stage/$file" ]] || fail "missing or symlink: $file"
      [[ "$(sha256sum "$stage/$file" | cut -d' ' -f1)" == "${!i}" ]] || fail "transferred bytes differ: $file"
      i=$((i + 1))
    done
    if [[ -e "$final" || -L "$final" ]]; then
      [[ -d "$final" && ! -L "$final" ]] || fail 'final release exists but is not a directory'
      diff -qr -- "$stage" "$final" >/dev/null || fail 'existing VERSION has different bytes (immutable conflict)'
      rm -rf -- "$stage"
      rmdir -- "$lock"
      echo 'immutable release already exists; exact-byte no-op'
      exit 0
    fi
    # A rename on external storage may mean COPY -> DELETE, not an atomic move.
    # Copy to the final location, verify exact bytes, then remove the staging
    # directory. The final path is NOT a readiness signal while this runs;
    # consumers must wait for publication success and independently verify.
    mkdir -- "$final" || fail 'cannot create final release directory (existing destination or storage failure)'
    cp -a -- "$stage/." "$final/" || fail 'release copy failed; final directory may be incomplete and requires manual recovery'
    actual_final="$(cd "$final" && find . -mindepth 1 -printf '%P\\n' | LC_ALL=C sort)"
    [[ "$actual_final" == "$expected" && -d "$final/altlinux-x64" && ! -L "$final/altlinux-x64" ]] || fail 'copied release structure differs; final directory may be incomplete'
    for file in "${files[@]}"; do
      [[ -f "$final/$file" && ! -L "$final/$file" ]] || fail "copied release file missing: $file"
    done
    i=7
    for file in "${files[@]}"; do
      [[ "$(sha256sum "$final/$file" | cut -d' ' -f1)" == "${!i}" ]] || fail "post-copy SHA mismatch: $file"
      i=$((i + 1))
    done
    diff -qr -- "$stage" "$final" >/dev/null || fail 'copy differs from verified staging'
    rm -rf -- "$stage" || fail 'verified final release exists but staging cleanup failed'
    rmdir -- "$lock"
    echo 'immutable release published'
    ;;
  cleanup)
    [[ $# == 5 ]] || fail 'cleanup requires staging name'
    stage_name="$5"; check_stage_name "$stage_name"
    [[ -d "$lock" ]] || fail 'lock absent: refusing unrelated cleanup'
    if [[ -e "$parent/$stage_name" ]]; then rm -rf -- "$parent/$stage_name"; fi
    rmdir -- "$lock"
    ;;
  *) fail 'unknown operation' ;;
esac
