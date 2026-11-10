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
"$release_tool" assemble "$package" "$tmp/out" "$product" "$version" "$commit"
release="$tmp/out/products/$product/releases/$version"
"$release_tool" verify "$release" "$product" "$version" "$commit"
fail_expected() {
  if "$@" >"$tmp/rejected.log" 2>&1; then
    echo "Expected command to reject invalid input: $*" >&2
    exit 1
  fi
}
fail_expected "$release_tool" assemble "$package" "$tmp/out" "$product" "$version" "$commit"
fail_expected "$release_tool" verify "$release" "$product" "$version" deadbeef
cp -a "$release" "$tmp/tampered"
printf 'tampering' >> "$tmp/tampered/altlinux-x64/$zipname"
fail_expected "$release_tool" verify "$tmp/tampered" "$product" "$version" "$commit"
cp -a "$release" "$tmp/extra"
printf x > "$tmp/extra/unknown"
fail_expected "$release_tool" verify "$tmp/extra" "$product" "$version" "$commit"

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
echo 'distribution release assembly, verification, publication, idempotence, tamper rejection: PASS'
