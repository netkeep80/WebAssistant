#!/usr/bin/env bash
set -euo pipefail

source_sha=""
evidence=""
pdf=""
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
script_dir="${repo_root}/ci/release"
guide_verify="${repo_root}/webassist/docs/installation-guide/verify.sh"
version_file="${repo_root}/webassist/VERSION"

fail() {
  echo "finalize-release.sh: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    --evidence) evidence="${2:-}"; shift 2 ;;
    --pdf) pdf="${2:-}"; shift 2 ;;
    *) fail "unknown argument: $1" ;;
  esac
done

[[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "--source-sha must be exact 40-hex"
[[ -n "$evidence" ]] || fail "--evidence is required"
[[ -n "$pdf" ]] || fail "--pdf is required"
[[ -n "$repo" ]] || fail "GH_REPO or GITHUB_REPOSITORY is required"
command -v gh >/dev/null 2>&1 || fail "gh CLI is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"
[[ -f "$version_file" ]] || fail "webassist/VERSION is missing"
version="$(tr -d '\r\n' < "$version_file")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "webassist/VERSION is malformed"
tag="v${version}"

# Must remain the first external eligibility check. If main moved, no local or
# remote release evidence is allowed to resurrect the stale frozen candidate.
main_json="$(gh api "repos/${repo}/branches/main")" || fail "cannot read current main"
main_sha="$(printf '%s' "$main_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["commit"]["sha"])')" || fail "cannot parse current main"
[[ "$main_sha" == "$source_sha" ]] || fail "current main no longer equals frozen source SHA"

state_json="$(bash "${script_dir}/inspect-release-state.sh" "$version" "$source_sha" --require-complete)" || fail "same-version Release failed complete identity verification"
state="$(printf '%s' "$state_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')"

# Idempotent rerun after publication: the remote seven-asset identity and the
# successful candidate run were already proven by --require-complete. Never
# require local evidence and never mutate/rebuild a published same-version Release.
if [[ "$state" == "published" ]]; then
  python3 - "$tag" "$source_sha" "$version" <<'PY'
import json,sys
print(json.dumps({"state":"published-noop","tag":sys.argv[1],"sourceSha":sys.argv[2],"version":sys.argv[3]}, separators=(",",":")))
PY
  exit 0
fi
[[ "$state" == "draft" ]] || fail "finalizer requires an existing exact Draft Release"

[[ -f "$evidence" ]] || fail "final evidence manifest does not exist: $evidence"
[[ -f "$pdf" ]] || fail "WebAssistant-Installation-Guide.pdf does not exist: $pdf"
[[ "$(basename -- "$pdf")" == "WebAssistant-Installation-Guide.pdf" ]] || fail "PDF must be named WebAssistant-Installation-Guide.pdf"
[[ -f "$guide_verify" ]] || fail "docs/installation-guide/verify.sh is missing"

windows_sha="$(printf '%s' "$state_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["windowsSha256"])')"
linux_sha="$(printf '%s' "$state_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["linuxSha256"])')"

manifest_identity="$(python3 - "$evidence" <<'PY'
import json,sys
with open(sys.argv[1],encoding="utf-8") as handle:
    value=json.load(handle)
try:
    print("\t".join([
        value["schema"], value["kind"], value["sourceSha"], value["version"],
        value["artifacts"]["windows"]["sha256"], value["artifacts"]["linux"]["sha256"]
    ]))
except Exception:
    raise SystemExit(1)
PY
)" || fail "final evidence manifest identity is malformed"
IFS=$'\t' read -r manifest_schema manifest_kind manifest_source manifest_version manifest_windows_sha manifest_linux_sha <<< "$manifest_identity"
[[ "$manifest_schema" == "webassistant-installation-evidence/v1" && "$manifest_kind" == "final" ]] || fail "final evidence manifest must use final v1 evidence schema"
[[ "$manifest_source" == "$source_sha" && "$manifest_version" == "$version" ]] || fail "final evidence manifest source/version does not match frozen candidate"
[[ "$manifest_windows_sha" == "$windows_sha" && "$manifest_linux_sha" == "$linux_sha" ]] || fail "manifest installer hash does not match staged accepted installer bytes"

WEBASSISTANT_SOURCE_SHA="$source_sha" bash "$guide_verify" --mode final --pdf "$pdf" --evidence "$evidence" >/dev/null || fail "final installation-guide evidence/PDF verification failed"
pdf_sha="$(sha256sum -- "$pdf" | awk '{print $1}')"

existing_pdf_digest="$(printf '%s' "$state_json" | python3 -c '
import json,sys
matches=[a.get("digest","") for a in json.load(sys.stdin).get("assets",[]) if a.get("name")=="WebAssistant-Installation-Guide.pdf"]
if len(matches)>1: raise SystemExit(2)
print(matches[0] if matches else "")
')" || fail "ambiguous PDF asset state"

if [[ -n "$existing_pdf_digest" ]]; then
  [[ "$existing_pdf_digest" == "sha256:${pdf_sha}" ]] || fail "PDF asset digest conflicts with verified final PDF"
else
  gh release upload "$tag" "$pdf" --repo "$repo" || fail "cannot upload verified final PDF"
fi

post_pdf_json="$(bash "${script_dir}/inspect-release-state.sh" "$version" "$source_sha" --require-complete)" || fail "Draft Release failed identity verification after PDF staging"
post_pdf_state="$(printf '%s' "$post_pdf_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')"
[[ "$post_pdf_state" == "draft" ]] || fail "Release must remain draft until final publication gate"
post_pdf_sha="$(printf '%s' "$post_pdf_json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("pdfSha256") or "")')"
[[ "$post_pdf_sha" == "$pdf_sha" ]] || fail "PDF asset digest does not match verified final PDF after staging"
asset_count="$(printf '%s' "$post_pdf_json" | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("assets",[])))')"
[[ "$asset_count" == "7" ]] || fail "official Draft Release must contain exactly seven public assets before publication"

gh release edit "$tag" --repo "$repo" --draft=false >/dev/null || fail "cannot publish existing verified Draft Release"

published_json="$(bash "${script_dir}/inspect-release-state.sh" "$version" "$source_sha" --require-complete)" || fail "published Release failed post-publication identity verification"
published_state="$(printf '%s' "$published_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')"
[[ "$published_state" == "published" ]] || fail "Release did not become published"
published_count="$(printf '%s' "$published_json" | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("assets",[])))')"
[[ "$published_count" == "7" ]] || fail "published Release must contain exactly seven public assets"
published_pdf_sha="$(printf '%s' "$published_json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("pdfSha256") or "")')"
[[ "$published_pdf_sha" == "$pdf_sha" ]] || fail "published PDF digest changed after publication"

python3 - "$tag" "$source_sha" "$version" "$pdf_sha" <<'PY'
import json,sys
print(json.dumps({
  "state":"published",
  "tag":sys.argv[1],
  "sourceSha":sys.argv[2],
  "version":sys.argv[3],
  "pdfSha256":sys.argv[4]
}, separators=(",",":")))
PY
