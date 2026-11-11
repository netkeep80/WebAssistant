#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
release_tool="$root/build/distribution/release.sh"
remote="$root/build/distribution/remote-publish.sh"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
version=0.3.77
product=generic-product
commit=0123456789abcdef0123456789abcdef01234567
package="$tmp/packaged"
mkdir -p "$package"
zipname="CustomInstaller-linux-x64-${version}.zip"
printf 'simulated exact archive bytes\n' > "$package/$zipname"
sha="$(sha256sum "$package/$zipname" | cut -d' ' -f1)"
size="$(stat -c %s "$package/$zipname")"
(cd "$package" && sha256sum "$zipname" > "$zipname.sha256")
jq -n --arg f "$zipname" --arg v "$version" --arg c "$commit" --arg s "$sha" --argjson size "$size" '{artifact:$f,version:$v,sourceSha:$c,rid:"linux-x64",sha256:$s,size:$size,packageEntrypoint:"build/linux/package.sh"}' > "$package/$zipname.provenance.json"
bash "$release_tool" assemble "$package" "$tmp/out" "$product" "$version" "$commit"
release="$tmp/out/products/$product/releases/$version"
bash "$release_tool" verify "$release" "$product" "$version" "$commit"
fail_expected() {
  if "$@" >"$tmp/rejected.log" 2>&1; then
    echo "Expected command to reject invalid input: $*" >&2
    exit 1
  fi
}
fail_expected bash "$release_tool" assemble "$package" "$tmp/out" "$product" "$version" "$commit"
fail_expected bash "$release_tool" verify "$release" "$product" "$version" deadbeef
cp -a "$release" "$tmp/tampered"
printf 'tampering' >> "$tmp/tampered/altlinux-x64/$zipname"
fail_expected bash "$release_tool" verify "$tmp/tampered" "$product" "$version" "$commit"
cp -a "$release" "$tmp/extra"
printf x > "$tmp/extra/unknown"
fail_expected bash "$release_tool" verify "$tmp/extra" "$product" "$version" "$commit"

root_b64="$(printf '%s' "$tmp/storage" | base64 -w0)"
stage="$(bash "$remote" prepare "$root_b64" "$product" "$version" 111 222)"
parent="$tmp/storage/shared/${product}-build/products/$product/releases"
cp -a "$release/." "$parent/$stage/"
hashes=("$(sha256sum "$release/manifest.json" | cut -d' ' -f1)" \
        "$(sha256sum "$release/altlinux-x64/$zipname" | cut -d' ' -f1)" \
        "$(sha256sum "$release/altlinux-x64/$zipname.sha256" | cut -d' ' -f1)" \
        "$(sha256sum "$release/altlinux-x64/$zipname.provenance.json" | cut -d' ' -f1)")
fail_expected bash "$remote" finalize "$root_b64" "$product" "$version" "$stage" "$zipname" bad "${hashes[@]:1}"
bash "$remote" finalize "$root_b64" "$product" "$version" "$stage" "$zipname" "${hashes[@]}"
test ! -e "$parent/$stage" # verified copy must remove the staging directory
diff -qr "$release" "$parent/$version"
stage="$(bash "$remote" prepare "$root_b64" "$product" "$version" 112 223)"
cp -a "$release/." "$parent/$stage/"
bash "$remote" finalize "$root_b64" "$product" "$version" "$stage" "$zipname" "${hashes[@]}"
stage="$(bash "$remote" prepare "$root_b64" "$product" "$version" 113 224)"
cp -a "$release/." "$parent/$stage/"
printf changed >> "$parent/$stage/altlinux-x64/$zipname"
fail_expected bash "$remote" finalize "$root_b64" "$product" "$version" "$stage" "$zipname" "${hashes[@]}"
bash "$remote" cleanup "$root_b64" "$product" "$version" "$stage"
# Exact bytes remain immutable after a rejected conflicting publication.
diff -qr "$release" "$parent/$version"
# A partially copied final directory must never be accepted as a release.
partial_root_b64="$(printf '%s' "$tmp/partial-storage" | base64 -w0)"
partial_stage="$(bash "$remote" prepare "$partial_root_b64" "$product" "$version" 114 225)"
partial_parent="$tmp/partial-storage/shared/${product}-build/products/$product/releases"
cp -a "$release/." "$partial_parent/$partial_stage/"
mkdir -p "$partial_parent/$version"
cp -- "$release/manifest.json" "$partial_parent/$version/manifest.json"
fail_expected bash "$remote" finalize "$partial_root_b64" "$product" "$version" "$partial_stage" "$zipname" "${hashes[@]}"
test -f "$partial_parent/$version/manifest.json"
test ! -e "$partial_parent/$version/altlinux-x64/$zipname"
bash "$remote" cleanup "$partial_root_b64" "$product" "$version" "$partial_stage"
# Contract: final publication must be a verified copy followed by staging delete.
grep -Fq 'cp -a -- "$stage/." "$final/"' "$remote"
if grep -Fq 'mv -T' "$remote"; then echo 'atomic rename assumption found' >&2; exit 1; fi
# The complete publish-ssh wrapper is exercised through an SSH command shim;
# no actual remote host is contacted in this local test.
mkdir -p "$tmp/mock-bin"
cat > "$tmp/mock-bin/ssh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
while [[ $# -gt 0 ]]; do
  case "$1" in
    -o|-i) shift 2 ;;
    -T) shift ;;
    *) break ;;
  esac
done
[[ $# == 2 ]] || exit 2
shift # fake destination
bash -c "$1"
MOCK
chmod +x "$tmp/mock-bin/ssh"
cat > "$tmp/mock-bin/ssh-agent" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
case "${1:-}" in
  -s) printf '%s\n' 'SSH_AUTH_SOCK=mock; export SSH_AUTH_SOCK; SSH_AGENT_PID=1234; export SSH_AGENT_PID;' ;;
  -k) exit 0 ;;
  *) exit 2 ;;
esac
MOCK
cat > "$tmp/mock-bin/ssh-add" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ $# == 1 && -s "$1" ]] || exit 2
[[ "${SSH_ASKPASS_REQUIRE:-}" == force && -x "${SSH_ASKPASS:-}" ]] || exit 3
[[ "$("$SSH_ASKPASS")" == "${CI_SSH_ID_RSA_SECRET}" ]] || exit 4
MOCK
chmod +x "$tmp/mock-bin/ssh-agent" "$tmp/mock-bin/ssh-add"
cat > "$tmp/mock-bin/sudo" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == -n && "${2:-}" == install ]] || exit 2
[[ "${MOCK_SUDO_DENY:-0}" != 1 ]] || exit 1
shift
exec "$@"
MOCK
chmod +x "$tmp/mock-bin/sudo"
export PATH="$tmp/mock-bin:$PATH"
export CI_NETWORK_DRIVE_PATH="$tmp/full-wrapper-store"
export CI_SSH_HOST=example.test
export CI_SSH_USER=builder
export CI_SSH_ID_RSA='mock inherited private key'
export CI_SSH_ID_RSA_SECRET='mock inherited passphrase'
export CI_PIPELINE_ID=203
export CI_JOB_ID=204

# Early validation must reject every unset, placeholder or malformed mandatory
# setting without a real SSH connection, release directory or package build.
publisher="$root/build/distribution/publish-ssh.sh"
bash "$publisher" --check-ci-environment
preflight_reject() {
  expected="$1"; shift
  if "$@" >"$tmp/preflight-rejected.log" 2>&1; then
    echo "Preflight accepted invalid CI configuration" >&2
    exit 1
  fi
  grep -Fq "$expected" "$tmp/preflight-rejected.log" || {
    echo "Unexpected preflight diagnostic; expected: $expected" >&2
    cat "$tmp/preflight-rejected.log" >&2
    exit 1
  }
}
for bad in '' null masked_null; do
  preflight_reject "required CI variable not configured: CI_NETWORK_DRIVE_PATH" env CI_NETWORK_DRIVE_PATH="$bad" bash "$publisher" --check-ci-environment
  preflight_reject "required CI variable not configured: CI_SSH_ID_RSA" env CI_SSH_ID_RSA="$bad" bash "$publisher" --check-ci-environment
done
preflight_reject "required CI variable not configured: CI_SSH_HOST" env -u CI_SSH_HOST bash "$publisher" --check-ci-environment
preflight_reject "required CI variable not configured: CI_SSH_USER" env CI_SSH_USER=null bash "$publisher" --check-ci-environment
preflight_reject "required CI variable not configured: CI_PIPELINE_ID" env CI_PIPELINE_ID=masked_null bash "$publisher" --check-ci-environment
preflight_reject "required CI variable not configured: CI_JOB_ID" env -u CI_JOB_ID bash "$publisher" --check-ci-environment
for bad in / relative; do
  preflight_reject "invalid storage root" env CI_NETWORK_DRIVE_PATH="$bad" bash "$publisher" --check-ci-environment
done
preflight_reject "invalid storage root" env CI_NETWORK_DRIVE_PATH="$(printf '/tmp/storage\ninvalid')" bash "$publisher" --check-ci-environment
preflight_reject "invalid inherited SSH target" env CI_SSH_HOST='invalid host' bash "$publisher" --check-ci-environment
preflight_reject "invalid inherited SSH target" env CI_SSH_USER='invalid user' bash "$publisher" --check-ci-environment
preflight_reject "invalid job identifiers" env CI_PIPELINE_ID=abc bash "$publisher" --check-ci-environment
preflight_reject "invalid job identifiers" env CI_JOB_ID=4x bash "$publisher" --check-ci-environment
# The group SSH passphrase remains optional (unencrypted private keys).
env -u CI_SSH_ID_RSA_SECRET bash "$publisher" --check-ci-environment

# Manual storage bootstrap: reuse real SSH wrapper; sudo is simulated locally.
manual_root="$tmp/manual-storage"
mkdir -p "$manual_root/shared"
manual_dir="$manual_root/shared/${product}-build"
env CI_NETWORK_DRIVE_PATH="$manual_root" bash "$publisher" --init-storage "$product"
[[ -d "$manual_dir" && -w "$manual_dir" && "$(stat -c %a "$manual_dir")" == 750 ]]
[[ "$(stat -c %U "$manual_dir")" == "$(id -un)" ]]
[[ ! -d "$manual_dir/products" ]] # no release tree or artifacts created
printf 'keep' > "$manual_dir/existing"
env CI_NETWORK_DRIVE_PATH="$manual_root" bash "$publisher" --init-storage "$product"
[[ "$(cat "$manual_dir/existing")" == keep ]] # idempotent, no recursive changes
preflight_reject "storage root/shared missing or symlinked" env CI_NETWORK_DRIVE_PATH="$tmp/missing-root" bash "$publisher" --init-storage "$product"
ln -s "$manual_dir" "$manual_root/shared/symlink-build"
preflight_reject "project storage directory is a symlink" env CI_NETWORK_DRIVE_PATH="$manual_root" bash "$publisher" --init-storage symlink
preflight_reject "storage initialization requires permitted passwordless sudo" env CI_NETWORK_DRIVE_PATH="$manual_root" MOCK_SUDO_DENY=1 bash "$publisher" --init-storage other-product

bash "$publisher" "$release" "$product" "$version" "$commit"
diff -qr "$release" "$CI_NETWORK_DRIVE_PATH/shared/${product}-build/products/$product/releases/$version"
echo 'distribution release assembly, verification, SSH transfer, idempotence, tamper rejection: PASS'
