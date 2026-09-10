#!/usr/bin/env bash
set -euo pipefail

artifact_path="${1:-}"
sha256_path="${2:-}"
provenance_path="${3:-}"

if [[ -z "$artifact_path" || -z "$sha256_path" || -z "$provenance_path" ]]; then
    echo "Использование: $0 <zip artifact> <sha256 evidence> <provenance json>" >&2
    exit 2
fi

for required_file in "$artifact_path" "$sha256_path" "$provenance_path"; do
    [[ -f "$required_file" ]] || {
        echo "Отсутствует installer evidence file: $required_file" >&2
        exit 1
    }
done

command -v sha256sum >/dev/null 2>&1 || {
    echo "sha256sum is required" >&2
    exit 1
}
command -v stat >/dev/null 2>&1 || {
    echo "stat is required" >&2
    exit 1
}
command -v unzip >/dev/null 2>&1 || {
    echo "unzip is required" >&2
    exit 1
}
command -v python3 >/dev/null 2>&1 || {
    echo "python3 is required to validate provenance" >&2
    exit 1
}

artifact_name="$(basename -- "$artifact_path")"
if [[ "$artifact_name" =~ ^WebAssistant-linux-x64-([0-9]+\.[0-9]+\.[0-9]+)\.zip$ ]]; then
    version="${BASH_REMATCH[1]}"
else
    echo "Некорректное имя Linux installer artifact: $artifact_name" >&2
    exit 1
fi

recorded_sha256="$(awk 'NR == 1 { print $1 }' "$sha256_path")"
recorded_artifact="$(awk 'NR == 1 { print $2 }' "$sha256_path")"
computed_sha256="$(sha256sum -- "$artifact_path" | awk '{print $1}')"
artifact_size="$(stat -c '%s' -- "$artifact_path")"

[[ "$recorded_sha256" == "$computed_sha256" ]] || {
    echo "SHA-256 evidence does not match artifact bytes." >&2
    exit 1
}
[[ "$recorded_artifact" == "$artifact_name" ]] || {
    echo "SHA-256 evidence names another artifact: $recorded_artifact" >&2
    exit 1
}

python3 - \
    "$provenance_path" \
    "$artifact_name" \
    "$version" \
    "$computed_sha256" \
    "$artifact_size" <<'PY'
import json
import sys

path, artifact, version, sha256, size = sys.argv[1:]
with open(path, "r", encoding="utf-8-sig") as handle:
    evidence = json.load(handle)

required = {
    "artifact",
    "version",
    "sourceSha",
    "rid",
    "sha256",
    "size",
    "sdkVersion",
    "configMode",
    "packageEntrypoint",
}
missing = sorted(required.difference(evidence))
if missing:
    raise SystemExit(f"provenance missing fields: {', '.join(missing)}")
if evidence["artifact"] != artifact:
    raise SystemExit("provenance artifact mismatch")
if evidence["version"] != version:
    raise SystemExit("provenance version mismatch")
if evidence["sha256"] != sha256:
    raise SystemExit("provenance sha256 mismatch")
if int(evidence["size"]) != int(size):
    raise SystemExit("provenance size mismatch")
if evidence["rid"] != "linux-x64":
    raise SystemExit("provenance rid mismatch")
if evidence["configMode"] not in {"source-appsettings", "generated-default"}:
    raise SystemExit("provenance configMode is outside the closed enum")
for field in ("sourceSha", "sdkVersion", "packageEntrypoint"):
    if not isinstance(evidence[field], str) or not evidence[field]:
        raise SystemExit(f"provenance {field} must be a non-empty string")
PY

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
extract_root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/webassistant-linux-artifact.XXXXXX")"
payload_root="$extract_root/${artifact_name%.zip}"
cleanup() {
    rm -rf -- "$extract_root"
}
trap cleanup EXIT

unzip -q "$artifact_path" -d "$extract_root"

test -x "$payload_root/app/WebAssistant"
test -f "$payload_root/app/appsettings.json"
test -f "$payload_root/VERSION"
test -x "$payload_root/install.sh"
test -x "$payload_root/uninstall.sh"
test -f "$payload_root/webassist.service"

inside_version="$(tr -d '\r\n' < "$payload_root/VERSION")"
[[ "$inside_version" == "$version" ]] || {
    echo "VERSION inside ZIP differs from artifact filename." >&2
    exit 1
}

bash "$script_dir/run-systemd-acceptance.sh" "$payload_root"

echo "linux_installer_artifact_acceptance=PASS"
