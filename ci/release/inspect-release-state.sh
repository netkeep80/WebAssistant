#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
source_sha="${2:-}"
verification_mode="${3:-}"
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

fail() {
  echo "inspect-release-state.sh: $*" >&2
  exit 1
}

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "version must be canonical major.minor.revision"
[[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "source SHA must be exact 40-hex"
[[ -z "$verification_mode" || "$verification_mode" == "--require-complete" ]] || fail "unsupported verification mode: $verification_mode"
[[ $# -le 3 ]] || fail "too many arguments"
[[ -n "$repo" ]] || fail "GH_REPO or GITHUB_REPOSITORY is required"
command -v gh >/dev/null 2>&1 || fail "gh CLI is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"

tag="v${version}"
releases_json="$(gh api "repos/${repo}/releases?per_page=100")" || fail "cannot list releases"

release_json="$(printf '%s' "$releases_json" | python3 -c '
import json,sys
tag=sys.argv[1]
items=json.load(sys.stdin)
matches=[r for r in items if r.get("tag_name")==tag]
if len(matches)>1:
    raise SystemExit(3)
print("null" if not matches else json.dumps(matches[0], separators=(",",":")))
' "$tag")" || fail "ambiguous same-version release state"

if [[ "$release_json" == "null" ]]; then
  [[ -z "$verification_mode" ]] || fail "complete same-version Release does not exist"
  if tag_json="$(gh api "repos/${repo}/git/ref/tags/${tag}" 2>/dev/null)"; then
    tag_sha="$(printf '%s' "$tag_json" | python3 -c 'import json,sys; print((json.load(sys.stdin).get("object") or {}).get("sha", ""))')"
    [[ "$tag_sha" == "$source_sha" ]] || fail "existing tag points to a different source SHA"
    python3 - "$tag" "$source_sha" <<'PY'
import json,sys
print(json.dumps({"state":"tag-only","tag":sys.argv[1],"sourceSha":sys.argv[2]}, separators=(",",":")))
PY
    exit 0
  fi
  python3 - "$tag" "$source_sha" <<'PY'
import json,sys
print(json.dumps({"state":"absent","tag":sys.argv[1],"sourceSha":sys.argv[2]}, separators=(",",":")))
PY
  exit 0
fi

release_id="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')"
draft="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(str(bool(json.load(sys.stdin).get("draft"))).lower())')"
prerelease="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(str(bool(json.load(sys.stdin).get("prerelease"))).lower())')"
target="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("target_commitish", ""))')"
body="$(printf '%s' "$release_json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("body") or "")')"
[[ "$prerelease" == "false" ]] || fail "same-version Release must not be prerelease"
[[ "$target" == "$source_sha" ]] || fail "same-version Release target does not match frozen source SHA"

tag_json="$(gh api "repos/${repo}/git/ref/tags/${tag}")" || fail "same-version Release is missing exact tag ref"
tag_sha="$(printf '%s' "$tag_json" | python3 -c 'import json,sys; print((json.load(sys.stdin).get("object") or {}).get("sha", ""))')"
[[ "$tag_sha" == "$source_sha" ]] || fail "release tag points to a different source SHA"

assets_json="$(gh api "repos/${repo}/releases/${release_id}/assets?per_page=100")" || fail "cannot inspect release assets"
state="published"
[[ "$draft" == "true" ]] && state="draft"

if [[ -z "$verification_mode" ]]; then
  python3 - "$state" "$release_id" "$tag" "$source_sha" "$body" "$assets_json" <<'PY'
import json,sys
state,release_id,tag,source,body,assets=sys.argv[1:]
print(json.dumps({
  "state":state,
  "releaseId":int(release_id),
  "tag":tag,
  "sourceSha":source,
  "body":body,
  "assets":json.loads(assets)
}, separators=(",",":")))
PY
  exit 0
fi

candidate_run_id="$(python3 - "$body" "$source_sha" "$version" <<'PY'
import json,sys
body,source,version=sys.argv[1:]
try:
    value=json.loads(body)
except Exception:
    raise SystemExit(2)
if not isinstance(value,dict):
    raise SystemExit(2)
if value.get("schema")!="webassistant-release-candidate/v1":
    raise SystemExit(2)
if value.get("sourceSha")!=source or value.get("version")!=version:
    raise SystemExit(2)
if value.get("state")!="installers-accepted-staged":
    raise SystemExit(2)
run=value.get("candidateRunId")
if not isinstance(run,int) or run < 1:
    raise SystemExit(2)
print(run)
PY
)" || fail "candidate release metadata is missing or inconsistent"

run_json="$(gh api "repos/${repo}/actions/runs/${candidate_run_id}")" || fail "cannot inspect candidate run ${candidate_run_id}"
printf '%s' "$run_json" | python3 - "$source_sha" <<'PY' || fail "candidate run is not a successful exact frozen-main Release candidate run"
import json,sys
source=sys.argv[1]
run=json.load(sys.stdin)
checks=(
    run.get("path")==".github/workflows/release-candidate.yml",
    run.get("event")=="workflow_dispatch",
    run.get("head_sha")==source,
    run.get("status")=="completed",
    run.get("conclusion")=="success",
)
if not all(checks):
    raise SystemExit(1)
PY

asset_contract="$(printf '%s' "$assets_json" | python3 - "$version" "$state" <<'PY'
import json,re,sys
version,state=sys.argv[1:]
assets=json.load(sys.stdin)
expected=[
 f"WebAssistant-win-x64-{version}.exe",
 f"WebAssistant-win-x64-{version}.exe.sha256",
 f"WebAssistant-win-x64-{version}.exe.provenance.json",
 f"WebAssistant-linux-x64-{version}.zip",
 f"WebAssistant-linux-x64-{version}.zip.sha256",
 f"WebAssistant-linux-x64-{version}.zip.provenance.json",
]
pdf="WebAssistant-Installation-Guide.pdf"
by_name={}
for asset in assets:
    name=asset.get("name")
    digest=asset.get("digest")
    if not isinstance(name,str) or name in by_name:
        print("duplicate or malformed public asset", file=sys.stderr); raise SystemExit(1)
    if name not in expected and name!=pdf:
        print(f"unexpected public asset: {name}", file=sys.stderr); raise SystemExit(1)
    if not isinstance(digest,str) or not re.fullmatch(r"sha256:[0-9a-f]{64}",digest):
        print(f"malformed remote asset digest: {name}", file=sys.stderr); raise SystemExit(1)
    by_name[name]=digest
missing=[name for name in expected if name not in by_name]
if missing:
    print("required installer asset set is incomplete: "+", ".join(missing), file=sys.stderr); raise SystemExit(1)
if state=="published" and pdf not in by_name:
    print("published Release asset set is incomplete: PDF asset missing", file=sys.stderr); raise SystemExit(1)
print(json.dumps({"pdfPresent":pdf in by_name,"assets":by_name},separators=(",",":")))
PY
)" || fail "same-version Release asset contract is incomplete or inconsistent"

tmp_dir="$(mktemp -d)"
cleanup() { rm -rf "$tmp_dir"; }
trap cleanup EXIT
gh release download "$tag" --repo "$repo" --dir "$tmp_dir" >/dev/null || fail "cannot download staged Release assets"
verify_json="$(bash "${script_dir}/verify-installer-assets.sh" "$tmp_dir" "$version" "$source_sha")" || fail "downloaded installer assets failed exact identity verification"
windows_sha="$(printf '%s' "$verify_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["windowsSha256"])')"
linux_sha="$(printf '%s' "$verify_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["linuxSha256"])')"

pdf_sha=""
printf '%s' "$asset_contract" | python3 - "$tmp_dir" <<'PY' || fail "remote asset digest does not match downloaded exact bytes"
import hashlib,json,sys
from pathlib import Path
root=Path(sys.argv[1])
contract=json.load(sys.stdin)
for name,remote in contract["assets"].items():
    path=root/name
    if not path.is_file():
        print(f"downloaded asset missing: {name}",file=sys.stderr); raise SystemExit(1)
    actual="sha256:"+hashlib.sha256(path.read_bytes()).hexdigest()
    if actual!=remote:
        label="PDF asset digest" if name=="WebAssistant-Installation-Guide.pdf" else "remote asset digest"
        print(f"{label} mismatch: {name}",file=sys.stderr); raise SystemExit(1)
PY

if [[ "$(printf '%s' "$asset_contract" | python3 -c 'import json,sys; print(str(json.load(sys.stdin)["pdfPresent"]).lower())')" == "true" ]]; then
  pdf_sha="$(sha256sum -- "$tmp_dir/WebAssistant-Installation-Guide.pdf" | awk '{print $1}')"
fi

python3 - "$state" "$release_id" "$tag" "$source_sha" "$body" "$assets_json" "$candidate_run_id" "$windows_sha" "$linux_sha" "$pdf_sha" <<'PY'
import json,sys
state,release_id,tag,source,body,assets,run,windows_sha,linux_sha,pdf_sha=sys.argv[1:]
print(json.dumps({
  "state":state,
  "releaseId":int(release_id),
  "tag":tag,
  "sourceSha":source,
  "body":body,
  "assets":json.loads(assets),
  "complete":True,
  "candidateRunId":int(run),
  "windowsSha256":windows_sha,
  "linuxSha256":linux_sha,
  "pdfSha256":pdf_sha or None
}, separators=(",",":")))
PY
