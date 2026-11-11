#!/usr/bin/env bash
set -euo pipefail

fail() { printf 'publish-ssh: %s\n' "$*" >&2; exit 1; }
[[ $# == 4 ]] || fail 'usage: publish-ssh.sh RELEASE_DIR PRODUCT VERSION SOURCE_SHA'
release="$1"; product="$2"; version="$3"; commit="$4"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$script_dir/release.sh" verify "$release" "$product" "$version" "$commit"

# Reuse inherited SSH credentials. The storage root and rename gate are
# deliberately project-scoped; the SSH host must have the root mounted.
for var in CI_NETWORK_DRIVE_PATH CI_SSH_HOST CI_SSH_USER CI_SSH_ID_RSA CI_PIPELINE_ID CI_JOB_ID; do
  value="${!var:-}"
  [[ -n "$value" && "$value" != null && "$value" != masked_null ]] || fail "required CI variable not configured: $var"
done
[[ "${CI_RELEASE_STORAGE_RENAME_VERIFIED:-}" == 1 ]] || fail 'storage rename semantics not verified (set CI_RELEASE_STORAGE_RENAME_VERIFIED=1 only after physical check)'
[[ "$CI_NETWORK_DRIVE_PATH" == /* && "$CI_NETWORK_DRIVE_PATH" != / && "$CI_NETWORK_DRIVE_PATH" != *$'\n'* ]] || fail 'invalid storage root'
[[ "$CI_SSH_HOST" =~ ^[A-Za-z0-9._-]+$ && "$CI_SSH_USER" =~ ^[A-Za-z_][A-Za-z0-9_-]*$ ]] || fail 'invalid inherited SSH target'
[[ "$CI_PIPELINE_ID" =~ ^[0-9]+$ && "$CI_JOB_ID" =~ ^[0-9]+$ ]] || fail 'invalid job identifiers'
for executable in ssh ssh-agent ssh-add mktemp; do
  command -v "$executable" >/dev/null 2>&1 || fail "missing SSH tool: $executable"
done

# Authentication comes from group variables (private key + optional
# passphrase). Host authentication is separate and must remain pinned.
known_hosts_source=""
if [[ -n "${CI_SSH_KNOWN_HOSTS:-}" && "${CI_SSH_KNOWN_HOSTS}" != null ]]; then
  known_hosts_source="$CI_SSH_KNOWN_HOSTS"
elif [[ -n "${CI_RELEASE_SSH_KNOWN_HOSTS:-}" && "${CI_RELEASE_SSH_KNOWN_HOSTS}" != null ]]; then
  known_hosts_source="$CI_RELEASE_SSH_KNOWN_HOSTS"
else
  fail 'trusted SSH host keys not configured: CI_SSH_KNOWN_HOSTS (group) or CI_RELEASE_SSH_KNOWN_HOSTS (project)'
fi

umask 077
ssh_dir="$(mktemp -d)"
stage=""
published=0
agent_started=0
cleanup() {
  if [[ -n "$stage" && "$published" != 1 ]]; then
    ssh "${ssh_options[@]}" "$target" "bash -s -- cleanup $root_b64 $product $version $stage" < "$remote" || true
  fi
  if [[ "$agent_started" == 1 ]]; then ssh-agent -k >/dev/null 2>&1 || true; fi
  rm -rf -- "$ssh_dir"
}
trap cleanup EXIT

private_key="$ssh_dir/id_rsa"
known_hosts="$ssh_dir/known_hosts"
printf '%s\n' "$CI_SSH_ID_RSA" > "$private_key"

# GitLab variables may contain host-key data directly or a File-variable
# path. Never learn the key from the unverified SSH connection itself.
if [[ -f "$known_hosts_source" ]]; then
  cat -- "$known_hosts_source" > "$known_hosts"
else
  printf '%s\n' "$known_hosts_source" > "$known_hosts"
fi
[[ -s "$known_hosts" && "$(cat "$known_hosts")" != null && "$(cat "$known_hosts")" != masked_null ]] || fail 'trusted SSH host keys are empty/placeholder'
chmod 600 "$private_key" "$known_hosts"

# ssh-add in CI cannot reliably read a passphrase from stdin. Use an
# SSH_ASKPASS helper with no TTY; no private key/passphrase is logged.
cat > "$ssh_dir/askpass" <<'ASKPASS'
#!/bin/sh
printf '%s' "$CI_SSH_ID_RSA_SECRET"
ASKPASS
chmod 700 "$ssh_dir/askpass"
eval "$(ssh-agent -s)" >/dev/null
agent_started=1
if [[ -n "${CI_SSH_ID_RSA_SECRET:-}" && "${CI_SSH_ID_RSA_SECRET}" != null && "${CI_SSH_ID_RSA_SECRET}" != masked_null ]]; then
  DISPLAY="${DISPLAY:-:0}" SSH_ASKPASS="$ssh_dir/askpass" SSH_ASKPASS_REQUIRE=force ssh-add "$private_key" </dev/null >/dev/null || fail 'could not unlock inherited SSH private key'
else
  ssh-add "$private_key" </dev/null >/dev/null || fail 'could not load inherited SSH private key'
fi

target="${CI_SSH_USER}@${CI_SSH_HOST}"
ssh_options=( -o BatchMode=yes -o StrictHostKeyChecking=yes -o IdentitiesOnly=yes -o "UserKnownHostsFile=$known_hosts" -i "$private_key" -T )
remote="$script_dir/remote-publish.sh"
root_b64="$(printf '%s' "$CI_NETWORK_DRIVE_PATH" | base64 -w0)"

stage="$(ssh "${ssh_options[@]}" "$target" "bash -s -- prepare $root_b64 $product $version $CI_PIPELINE_ID $CI_JOB_ID" < "$remote")" || fail 'remote publication preparation failed'
[[ "$stage" =~ ^\.[0-9]+\.[0-9]+\.[0-9]+\.tmp-[0-9]+-[0-9]+\.[A-Za-z0-9]+$ ]] || fail 'remote returned invalid stage name'

# Receive the exact validated release files, never rebuild on the host.
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
published=1
