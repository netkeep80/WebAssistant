#!/usr/bin/env bash
set -euo pipefail

artifact_path="${1:-}"
version="${2:-}"
source_sha="${3:-}"
rid="${4:-}"
sdk_version="${5:-}"
config_mode="${6:-}"
package_entrypoint="${7:-}"

for required_value in \
    "$artifact_path" \
    "$version" \
    "$source_sha" \
    "$rid" \
    "$sdk_version" \
    "$config_mode" \
    "$package_entrypoint"; do
    [[ -n "$required_value" ]] || {
        echo "write-provenance.sh: missing required argument" >&2
        exit 2
    }
done

case "$config_mode" in
    source-appsettings|generated-default)
        ;;
    *)
        echo "write-provenance.sh: invalid configMode: $config_mode" >&2
        exit 2
        ;;
esac

[[ -f "$artifact_path" ]] || {
    echo "write-provenance.sh: artifact does not exist: $artifact_path" >&2
    exit 1
}

command -v sha256sum >/dev/null 2>&1 || {
    echo "write-provenance.sh: sha256sum is required" >&2
    exit 1
}
command -v stat >/dev/null 2>&1 || {
    echo "write-provenance.sh: stat is required" >&2
    exit 1
}

artifact="$(basename -- "$artifact_path")"
sha256="$(sha256sum -- "$artifact_path" | awk '{print $1}')"
size="$(stat -c '%s' -- "$artifact_path")"
sha_path="${artifact_path}.sha256"
provenance_path="${artifact_path}.provenance.json"

json_escape() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//$'\n'/\\n}"
    value="${value//$'\r'/\\r}"
    value="${value//$'\t'/\\t}"
    printf '%s' "$value"
}

printf '%s  %s\n' "$sha256" "$artifact" > "$sha_path"

{
    printf '{\n'
    printf '  "artifact": "%s",\n' "$(json_escape "$artifact")"
    printf '  "version": "%s",\n' "$(json_escape "$version")"
    printf '  "sourceSha": "%s",\n' "$(json_escape "$source_sha")"
    printf '  "rid": "%s",\n' "$(json_escape "$rid")"
    printf '  "sha256": "%s",\n' "$(json_escape "$sha256")"
    printf '  "size": %s,\n' "$size"
    printf '  "sdkVersion": "%s",\n' "$(json_escape "$sdk_version")"
    printf '  "configMode": "%s",\n' "$(json_escape "$config_mode")"
    printf '  "packageEntrypoint": "%s"\n' "$(json_escape "$package_entrypoint")"
    printf '}\n'
} > "$provenance_path"

printf '%s\n' "$provenance_path"
