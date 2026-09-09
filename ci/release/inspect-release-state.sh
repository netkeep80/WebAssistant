#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
source_sha="${2:-}"
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"

fail() {
  echo "inspect-release-state.sh: $*" >&2
  exit 1
}

[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "version must be canonical major.minor.revision"
[[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "source SHA must be exact 40-hex"
[[ -n "$repo" ]] || fail "GH_REPO or GITHUB_REPOSITORY is required"
command -v gh >/dev/null 2>&1 || fail "gh CLI is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"

tag="v${version}"
releases_json="$(gh api "repos/${repo}/releases?per_page=100")" || fail "cannot list releases"

release_json="$(printf '%s' "$releases_json" | python3 -c '
import json,sys
version,tag=sys.argv[1],sys.argv[2]
items=json.load(sys.stdin)
matches=[r for r in items if r.get("tag_name")==tag]
if len(matches)>1:
    raise SystemExit(3)
if not matches:
    print("null")
else:
    print(json.dumps(matches[0], separators=(",",":")))
' "$version" "$tag")" || fail "ambiguous same-version release state"

if [[ "$release_json" == "null" ]]; then
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
