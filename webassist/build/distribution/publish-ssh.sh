#!/usr/bin/env bash
set -euo pipefail
fail() { printf 'publish-ssh: %s\n' "$*" >&2; exit 1; }
[[ $# == 4 ]] || fail 'usage: publish-ssh.sh RELEASE_DIR PRODUCT VERSION SOURCE_SHA'
release="$1"; product="$2"; version="$3"; commit="$4"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$script_dir/release.sh" verify "$release" "$product" "$version" "$commit"

for var in CI_NETWORK_DRIVE_PATH CI_RELEASE_SSH_HOST CI_RELEASE_SSH_USER CI_RELEASE_SSH_PRIVATE_KEY CI_RELEASE_SSH_KNOWN_HOSTS CI_PIPELINE_ID CI_JOB_ID; do
  value="${!var:-}"
  [[ -n "$value" && "$value" != null && "$value" != masked_null ]] || fail "project variable not configured: $var"
done
[[ "${CI_RELEASE_STORAGE_RENAME_VERIFIED:-}" == 1 ]] || fail 'storage rename semantics not verified (set CI_RELEASE_STORAGE_RENAME_VERIFIED=1 only after physical check)'
[[ "$CI_NETWORK_DRIVE_PATH" == /* && "$CI_NETWORK_DRIVE_PATH" != / && "$CI_NETWORK_DRIVE_PATH" != *$'\n'* ]] || fail 'invalid storage root'
[[ "$CI_RELEASE_SSH_HOST" =~ ^[A-Za-z0-9._-]+$ && "$CI_RELEASE_SSH_USER" =~ ^[A-Za-z_][A-Za-z0-9_-]*$ ]] || fail 'invalid SSH target'
[[ "$CI_PIPELINE_ID" =~ ^[0-9]+$ && "$CI_JOB_ID" =~ ^[0-9]+$ ]] || fail 'invalid job identifiers'
[[ -f "$CI_RELEASE_SSH_PRIVATE_KEY" && -f "$CI_RELEASE_SSH_KNOWN_HOSTS" ]] || fail 'SSH credentials must be GitLab file-type variables'
[[ "$(cat "$CI_RELEASE_SSH_PRIVATE_KEY")" != null && "$(cat "$CI_RELEASE_SSH_KNOWN_HOSTS")" != null ]] || fail 'SSH credentials contain placeholder values'
chmod 600 -- "$CI_RELEASE_SSH_PRIVATE_KEY"
target="${CI_RELEASE_SSH_USER}@${CI_RELEASE_SSH_HOST}"
ssh_options=( -o BatchMode=yes -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$CI_RELEASE_SSH_KNOWN_HOSTS" -i "$CI_RELEASE_SSH_PRIVATE_KEY" -T )
remote="$script_dir/remote-publish.sh"
root_b64="$(printf '%s' "$CI_NETWORK_DRIVE_PATH" | base64 -w0)"

stage="$(ssh "${ssh_options[@]}" "$target" "bash -s -- prepare $root_b64 $product $version $CI_PIPELINE_ID $CI_JOB_ID" < "$remote")" || fail 'remote publication preparation failed'
[[ "$stage" =~ ^\.[0-9]+\.[0-9]+\.[0-9]+\.tmp-[0-9]+-[0-9]+\.[A-Za-z0-9]+$ ]] || fail 'remote returned invalid stage name'
cleanup() {
  ssh "${ssh_options[@]}" "$target" "bash -s -- cleanup $root_b64 $product $version $stage" < "$remote" || true
}
trap cleanup EXIT

# Receive only the four already-validated release files, never rebuild on the host.
stage_full="${CI_NETWORK_DRIVE_PATH}/shared/${product}-build/products/$product/releases/$stage"
stage_b64="$(printf '%s' "$stage_full" | base64 -w0)"
tar -C "$release" -cf - . | ssh "${ssh_options[@]}" "$target" \
  "bash -c 'set -e; dest=\$(printf %s $stage_b64 | base64 -d); tar -xf - -C \"\$dest\"'"

zipname="$(jq -er '.artifacts["altlinux-x64"].file' "$release/manifest.json")"
zipname="${zipname#altlinux-x64/}"
files=("manifest.json" "altlinux-x64/$zipname" "altlinux-x64/$zipname.sha256" "altlinux-x64/$zipname.provenance.json")
hashes=()
for file in "${files[@]}"; do hashes+=("$(sha256sum "$release/$file" | cut -d' ' -f1)"); done
ssh "${ssh_options[@]}" "$target" \
  "bash -s -- finalize $root_b64 $product $version $stage $zipname ${hashes[*]}" < "$remote"
trap - EXIT
