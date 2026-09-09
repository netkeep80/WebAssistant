#!/usr/bin/env bash
set -euo pipefail

artifact_root="${1:-}"
version="${2:-}"
source_sha="${3:-}"

fail() {
  echo "verify-installer-assets.sh: $*" >&2
  exit 1
}

[[ -d "$artifact_root" ]] || fail "artifact root does not exist: $artifact_root"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "version must be canonical major.minor.revision"
[[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "sourceSha must be exact 40-hex"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"
command -v stat >/dev/null 2>&1 || fail "stat is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"

verify_one() {
  local artifact="$1"
  local expected_rid="$2"
  local expected_entrypoint="$3"
  local name sha_file provenance_file actual_sha sidecar_sha sidecar_name actual_size

  name="$(basename -- "$artifact")"
  sha_file="${artifact}.sha256"
  provenance_file="${artifact}.provenance.json"

  [[ -f "$artifact" ]] || fail "missing canonical installer artifact: $name"
  [[ -f "$sha_file" ]] || fail "missing sha256 sidecar: $(basename -- "$sha_file")"
  [[ -f "$provenance_file" ]] || fail "missing provenance sidecar: $(basename -- "$provenance_file")"

  actual_sha="$(sha256sum -- "$artifact" | awk '{print $1}')"
  read -r sidecar_sha sidecar_name < "$sha_file" || fail "cannot read sha256 sidecar for $name"
  [[ "$sidecar_sha" =~ ^[0-9a-f]{64}$ ]] || fail "malformed sha256 sidecar for $name"
  [[ "$sidecar_name" == "$name" ]] || fail "sha256 sidecar filename mismatch for $name"
  [[ "$actual_sha" == "$sidecar_sha" ]] || fail "sha256 mismatch for $name"

  actual_size="$(stat -c '%s' -- "$artifact")"
  python3 - "$provenance_file" "$name" "$version" "$source_sha" "$actual_sha" "$actual_size" "$expected_rid" "$expected_entrypoint" <<'PY' || fail "provenance identity mismatch for $name"
import json,re,sys
path,name,version,source_sha,digest,size,rid,entrypoint=sys.argv[1:]
with open(path,encoding="utf-8") as handle:
    p=json.load(handle)
checks={
    "artifact": p.get("artifact")==name,
    "version": p.get("version")==version,
    "sourceSha": p.get("sourceSha")==source_sha,
    "sha256": p.get("sha256")==digest,
    "size": p.get("size")==int(size),
    "rid": p.get("rid")==rid,
    "packageEntrypoint": p.get("packageEntrypoint")==entrypoint,
}
if not isinstance(p.get("sdkVersion"),str) or not p["sdkVersion"]:
    checks["sdkVersion"]=False
if p.get("configMode") not in {"source-appsettings","generated-default"}:
    checks["configMode"]=False
failed=[key for key,value in checks.items() if not value]
if failed:
    print("invalid provenance fields: "+", ".join(failed), file=sys.stderr)
    raise SystemExit(1)
PY

  printf '%s\t%s\n' "$name" "$actual_sha"
}

windows="${artifact_root}/WebAssistant-win-x64-${version}.exe"
linux="${artifact_root}/WebAssistant-linux-x64-${version}.zip"

windows_result="$(verify_one "$windows" "win-x64" "build/windows/package.bat")"
linux_result="$(verify_one "$linux" "linux-x64" "build/linux/package.sh")"

windows_sha="${windows_result#*$'\t'}"
linux_sha="${linux_result#*$'\t'}"

python3 - "$version" "$source_sha" "$windows_sha" "$linux_sha" <<'PY'
import json,sys
print(json.dumps({
    "version": sys.argv[1],
    "sourceSha": sys.argv[2],
    "windowsSha256": sys.argv[3],
    "linuxSha256": sys.argv[4]
}, separators=(",",":")))
PY
