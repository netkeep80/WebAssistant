#!/usr/bin/env bash
set -euo pipefail

artifact_root="${1:-}"
version="${2:-}"
source_sha="${3:-}"
candidate_run_id="${4:-}"
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

fail() {
  echo "stage-draft-release.sh: $*" >&2
  exit 1
}

[[ -n "$repo" ]] || fail "GH_REPO or GITHUB_REPOSITORY is required"
[[ "$candidate_run_id" =~ ^[1-9][0-9]*$ ]] || fail "candidate workflow run id must be a positive integer"
command -v gh >/dev/null 2>&1 || fail "gh CLI is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"

bash "${script_dir}/verify-installer-assets.sh" "$artifact_root" "$version" "$source_sha" >/dev/null || fail "local installer identity verification failed"

tag="v${version}"
assets=(
  "${artifact_root}/WebAssistant-win-x64-${version}.exe"
  "${artifact_root}/WebAssistant-win-x64-${version}.exe.sha256"
  "${artifact_root}/WebAssistant-win-x64-${version}.exe.provenance.json"
  "${artifact_root}/WebAssistant-linux-x64-${version}.zip"
  "${artifact_root}/WebAssistant-linux-x64-${version}.zip.sha256"
  "${artifact_root}/WebAssistant-linux-x64-${version}.zip.provenance.json"
)
for asset in "${assets[@]}"; do
  [[ -f "$asset" ]] || fail "missing required staged asset: $(basename -- "$asset")"
done

verify_remote_assets() {
  local state_json="$1"
  local require_complete="$2"
  local seen=0
  while IFS=$'\t' read -r remote_name remote_digest; do
    [[ -n "$remote_name" ]] || continue
    local_match=""
    for asset in "${assets[@]}"; do
      if [[ "$(basename -- "$asset")" == "$remote_name" ]]; then
        local_match="$asset"
        break
      fi
    done
    [[ -n "$local_match" ]] || fail "draft contains unexpected public asset: $remote_name"
    local_digest="$(sha256sum -- "$local_match" | awk '{print $1}')"
    [[ "$remote_digest" == "sha256:${local_digest}" ]] || fail "remote asset digest conflicts with accepted bytes: $remote_name"
    seen=$((seen + 1))
  done < <(printf '%s' "$state_json" | python3 -c '
import json,sys
for asset in json.load(sys.stdin).get("assets",[]):
    print((asset.get("name") or "") + "\t" + (asset.get("digest") or ""))
')
  if [[ "$require_complete" == "true" && "$seen" -ne 6 ]]; then
    fail "remote staged installer asset set is incomplete: expected 6, got $seen"
  fi
}

state_json="$(bash "${script_dir}/inspect-release-state.sh" "$version" "$source_sha")" || fail "cannot establish same-version release state"
state="$(printf '%s' "$state_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')"

if [[ "$state" == "published" ]]; then
  fail "same-version Release is already published; candidate staging must use the published no-build preflight path"
fi
if [[ "$state" == "draft" ]]; then
  verify_remote_assets "$state_json" false
fi

metadata="$(python3 - "$source_sha" "$version" "$candidate_run_id" <<'PY'
import json,sys
print(json.dumps({
  "schema":"webassistant-release-candidate/v1",
  "sourceSha":sys.argv[1],
  "version":sys.argv[2],
  "candidateRunId":int(sys.argv[3]),
  "state":"installers-accepted-staged"
}, separators=(",",":")))
PY
)"

if [[ "$state" == "absent" ]]; then
  gh api --method POST "repos/${repo}/git/refs" -f "ref=refs/tags/${tag}" -f "sha=${source_sha}" >/dev/null || fail "cannot create exact release tag"
  gh api --method POST "repos/${repo}/releases" \
    -f "tag_name=${tag}" \
    -f "target_commitish=${source_sha}" \
    -f "name=${tag}" \
    -f "body=${metadata}" \
    -F draft=true \
    -F prerelease=false >/dev/null || fail "cannot create unpublished Draft Release"
elif [[ "$state" == "tag-only" ]]; then
  gh api --method POST "repos/${repo}/releases" \
    -f "tag_name=${tag}" \
    -f "target_commitish=${source_sha}" \
    -f "name=${tag}" \
    -f "body=${metadata}" \
    -F draft=true \
    -F prerelease=false >/dev/null || fail "cannot create Draft Release for existing exact tag"
fi

existing_names="$(printf '%s' "$state_json" | python3 -c '
import json,sys
for asset in json.load(sys.stdin).get("assets",[]): print(asset.get("name", ""))
' 2>/dev/null || true)"
missing=()
for asset in "${assets[@]}"; do
  name="$(basename -- "$asset")"
  if ! grep -Fqx -- "$name" <<< "$existing_names"; then
    missing+=("$asset")
  fi
done
if [[ ${#missing[@]} -gt 0 ]]; then
  gh release upload "$tag" "${missing[@]}" --repo "$repo" || fail "cannot upload accepted installer assets"
fi

post_json="$(bash "${script_dir}/inspect-release-state.sh" "$version" "$source_sha")" || fail "cannot re-inspect Draft Release after upload"
post_state="$(printf '%s' "$post_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')"
[[ "$post_state" == "draft" ]] || fail "post-upload Release state must remain unpublished draft"
verify_remote_assets "$post_json" true

python3 - "$tag" "$source_sha" "$version" "$candidate_run_id" <<'PY'
import json,sys
print(json.dumps({
  "state":"draft-staged",
  "tag":sys.argv[1],
  "sourceSha":sys.argv[2],
  "version":sys.argv[3],
  "candidateRunId":int(sys.argv[4])
}, separators=(",",":")))
PY
