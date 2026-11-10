#!/usr/bin/env bash
set -euo pipefail

fail() { printf 'release: %s\n' "$*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "missing command: $1"; }
for tool in jq sha256sum stat find sort cmp; do require_command "$tool"; done

validate_product() {
  [[ "$1" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ ]] && [[ "$1" != '.' && "$1" != '..' ]] || fail 'invalid product identifier'
}
validate_version() {
  [[ "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'invalid VERSION'
}
validate_source() {
  [[ "$1" =~ ^[0-9a-f]{40}$ ]] || fail 'invalid source commit'
}
verify_release() {
  local root="$1" product="$2" version="$3" commit="$4" manifest zipname zip sha size checksum provenance
  validate_product "$product"
  validate_version "$version"
  validate_source "$commit"
  [[ -d "$root" && ! -L "$root" ]] || fail 'missing release directory'
  manifest="$root/manifest.json"
  [[ -f "$manifest" && ! -L "$manifest" ]] || fail 'missing manifest'

  jq -e --arg p "$product" --arg v "$version" --arg c "$commit" '
    (keys | sort) == ["artifacts","product","schemaVersion","source","version"] and
    .schemaVersion == 1 and .product == $p and .version == $v and
    (.source | keys) == ["commit"] and .source.commit == $c and
    (.artifacts | keys) == ["altlinux-x64"] and
    (.artifacts["altlinux-x64"] | keys | sort) ==
      ["arch","file","format","os","provenance","rid","sha256","sha256File","size"] and
    .artifacts["altlinux-x64"].os == "altlinux" and
    .artifacts["altlinux-x64"].arch == "x64" and
    .artifacts["altlinux-x64"].rid == "linux-x64" and
    .artifacts["altlinux-x64"].format == "zip" and
    (.artifacts["altlinux-x64"].size | type) == "number" and
    .artifacts["altlinux-x64"].size > 0 and
    (.artifacts["altlinux-x64"].sha256 | test("^[0-9a-f]{64}$"))
  ' "$manifest" >/dev/null || fail 'invalid manifest metadata'

  local relative
  relative="$(jq -er '.artifacts["altlinux-x64"].file' "$manifest")"
  [[ "$relative" == altlinux-x64/* && "$relative" != *'/'*'/'* && "$relative" != *'..'* ]] || fail 'invalid artifact relative path'
  zipname="${relative#altlinux-x64/}"
  [[ "$zipname" == *"-linux-x64-${version}.zip" && "$zipname" != "-linux-x64-${version}.zip" ]] || fail 'invalid artifact filename'
  jq -e --arg f "$relative" '
    .artifacts["altlinux-x64"].sha256File == ($f + ".sha256") and
    .artifacts["altlinux-x64"].provenance == ($f + ".provenance.json")
  ' "$manifest" >/dev/null || fail 'invalid evidence relative paths'

  local expected actual
  expected="$(printf '%s\n' 'altlinux-x64' "altlinux-x64/$zipname" "altlinux-x64/$zipname.sha256" "altlinux-x64/$zipname.provenance.json" 'manifest.json' | LC_ALL=C sort)"
  actual="$(cd "$root" && find . -mindepth 1 -printf '%P\n' | LC_ALL=C sort)"
  [[ "$actual" == "$expected" ]] || fail 'release contains missing or extra entries'
  [[ -d "$root/altlinux-x64" && ! -L "$root/altlinux-x64" ]] || fail 'invalid artifact directory'
  zip="$root/$relative"
  checksum="$zip.sha256"
  provenance="$zip.provenance.json"
  [[ -f "$zip" && ! -L "$zip" && -f "$checksum" && ! -L "$checksum" && -f "$provenance" && ! -L "$provenance" ]] || fail 'missing/symlink evidence'
  sha="$(sha256sum "$zip" | cut -d' ' -f1)"
  size="$(stat -c %s "$zip")"
  [[ "$sha" == "$(jq -er '.artifacts["altlinux-x64"].sha256' "$manifest")" ]] || fail 'manifest SHA mismatch'
  [[ "$size" == "$(jq -er '.artifacts["altlinux-x64"].size' "$manifest")" ]] || fail 'manifest size mismatch'
  (cd "$root/altlinux-x64" && sha256sum -c --status "$zipname.sha256") || fail 'checksum file mismatch'
  [[ "$(cat "$checksum")" == "$sha  $zipname" ]] || fail 'noncanonical checksum file'
  jq -e --arg f "$zipname" --arg v "$version" --arg c "$commit" --arg sha "$sha" --argjson size "$size" '
    .artifact == $f and .version == $v and .sourceSha == $c and
    .rid == "linux-x64" and .sha256 == $sha and .size == $size and
    .packageEntrypoint == "build/linux/package.sh"
  ' "$provenance" >/dev/null || fail 'provenance mismatch'
}

case "${1:-}" in
  assemble)
    [[ $# == 6 ]] || fail 'usage: release.sh assemble PACKAGE_DIR OUTPUT_ROOT PRODUCT VERSION SOURCE_SHA'
    package_dir="$2"; output_root="$3"; product="$4"; version="$5"; commit="$6"
    validate_product "$product"; validate_version "$version"; validate_source "$commit"
    [[ -d "$package_dir" ]] || fail 'package directory not found'
    mapfile -d '' -t zips < <(find "$package_dir" -maxdepth 1 -type f -name '*.zip' -print0)
    [[ ${#zips[@]} == 1 ]] || fail 'expected exactly one ZIP'
    zip="${zips[0]}"; zipname="$(basename "$zip")"
    [[ "$zipname" == *"-linux-x64-${version}.zip" ]] || fail 'unexpected artifact version/name'
    [[ -f "$zip.sha256" && -f "$zip.provenance.json" ]] || fail 'missing packaging evidence'
    release="$output_root/products/$product/releases/$version"
    [[ ! -e "$release" && ! -L "$release" ]] || fail 'local release already exists'
    mkdir -p "$release/altlinux-x64"
    cp -- "$zip" "$zip.sha256" "$zip.provenance.json" "$release/altlinux-x64/"
    artifact="$release/altlinux-x64/$zipname"
    sha="$(sha256sum "$artifact" | cut -d' ' -f1)"
    size="$(stat -c %s "$artifact")"
    jq -n --arg p "$product" --arg v "$version" --arg c "$commit" \
      --arg f "altlinux-x64/$zipname" --arg sha "$sha" --argjson size "$size" \
      '{schemaVersion:1,product:$p,version:$v,source:{commit:$c},artifacts:{"altlinux-x64":{os:"altlinux",arch:"x64",rid:"linux-x64",format:"zip",file:$f,size:$size,sha256:$sha,sha256File:($f+".sha256"),provenance:($f+".provenance.json")}}}' \
      > "$release/manifest.json"
    verify_release "$release" "$product" "$version" "$commit"
    printf 'release assembled and verified: products/%s/releases/%s\n' "$product" "$version"
    ;;
  verify)
    [[ $# == 5 ]] || fail 'usage: release.sh verify RELEASE_DIR PRODUCT VERSION SOURCE_SHA'
    verify_release "$2" "$3" "$4" "$5"
    echo 'release verified'
    ;;
  *) fail 'usage: release.sh assemble|verify ...' ;;
esac
