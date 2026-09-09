#!/usr/bin/env bash
set -euo pipefail

source_sha="${1:-}"
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"

fail() {
  echo "resolve-accepted-main.sh: $*" >&2
  exit 1
}

[[ "$source_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "source SHA must be exact 40-hex"
[[ -n "$repo" ]] || fail "GH_REPO or GITHUB_REPOSITORY is required"
command -v gh >/dev/null 2>&1 || fail "gh CLI is required"
command -v python3 >/dev/null 2>&1 || fail "python3 is required"

main_json="$(gh api "repos/${repo}/branches/main")" || fail "cannot read current main"
main_sha="$(printf '%s' "$main_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["commit"]["sha"])')" || fail "cannot parse current main"
[[ "$main_sha" == "$source_sha" ]] || fail "source SHA is not current main"

pulls_json="$(gh api "repos/${repo}/commits/${source_sha}/pulls")" || fail "cannot resolve merged PR association"
pr_json="$(printf '%s' "$pulls_json" | python3 -c '
import json,sys
source=sys.argv[1]
items=json.load(sys.stdin)
matches=[p for p in items if p.get("merged_at") and p.get("merge_commit_sha")==source and (p.get("base") or {}).get("ref")=="main"]
if len(matches)!=1:
    raise SystemExit(3)
p=matches[0]
head=p.get("head") or {}
base=p.get("base") or {}
required=[p.get("number"), head.get("sha"), head.get("ref"), base.get("sha"), p.get("created_at"), p.get("merged_at")]
if any(value is None or value=="" for value in required):
    raise SystemExit(4)
print(json.dumps({
    "number":p["number"],
    "head":head["sha"],
    "headRef":head["ref"],
    "base":base["sha"],
    "createdAt":p["created_at"],
    "mergedAt":p["merged_at"]
}, separators=(",",":")))
' "$source_sha")" || fail "expected exactly one complete exact merged PR for current main"

pr_number="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["number"])')"
pr_head="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["head"])')"
pr_head_ref="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["headRef"])')"
base_sha="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["base"])')"
pr_created_at="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["createdAt"])')"
pr_merged_at="$(printf '%s' "$pr_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["mergedAt"])')"
[[ "$pr_head" =~ ^[0-9a-fA-F]{40}$ ]] || fail "merged PR head SHA is malformed"
[[ "$base_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "merged PR base SHA is malformed"
[[ -n "$pr_head_ref" ]] || fail "merged PR head ref is missing"

runs_json="$(gh api "repos/${repo}/actions/runs?head_sha=${pr_head}&event=pull_request&per_page=100")" || fail "cannot read PR workflow evidence"

candidate_run_ids() {
  local workflow_path="$1"
  printf '%s' "$runs_json" | python3 -c '
import json,sys
from datetime import datetime
path,head,head_ref,pr_created_raw,pr_merged_raw=sys.argv[1:6]

def timestamp(value):
    if not isinstance(value,str) or not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z","+00:00"))
    except ValueError:
        return None

pr_created=timestamp(pr_created_raw)
pr_merged=timestamp(pr_merged_raw)
if pr_created is None or pr_merged is None or pr_created > pr_merged:
    raise SystemExit(2)

runs=json.load(sys.stdin).get("workflow_runs",[])
ids=[]
for run in runs:
    trigger=run.get("event")
    if trigger is None:
        trigger=run.get("event_name")
    run_created=timestamp(run.get("created_at"))
    if (run.get("path")==path and trigger=="pull_request" and
        run.get("head_sha")==head and run.get("head_branch")==head_ref and
        run_created is not None and pr_created <= run_created <= pr_merged and
        run.get("status")=="completed" and run.get("conclusion")=="success" and
        isinstance(run.get("id"),int)):
        ids.append(run["id"])
for run_id in sorted(set(ids), reverse=True):
    print(run_id)
' "$workflow_path" "$pr_head" "$pr_head_ref" "$pr_created_at" "$pr_merged_at"
}

job_is_green() {
  local run_id="$1"
  local job_name="$2"
  local jobs_json
  jobs_json="$(gh api "repos/${repo}/actions/runs/${run_id}/jobs?per_page=100")" || return 1
  printf '%s' "$jobs_json" | python3 -c '
import json,sys
name=sys.argv[1]
jobs=json.load(sys.stdin).get("jobs",[])
matches=[j for j in jobs if j.get("name")==name]
if len(matches)!=1:
    raise SystemExit(1)
j=matches[0]
if j.get("status")!="completed" or j.get("conclusion")!="success":
    raise SystemExit(1)
' "$job_name"
}

select_green_run() {
  local workflow_path="$1"
  local job_name="$2"
  local run_id
  while IFS= read -r run_id; do
    [[ -n "$run_id" ]] || continue
    if job_is_green "$run_id" "$job_name"; then
      printf '%s\n' "$run_id"
      return 0
    fi
  done < <(candidate_run_ids "$workflow_path")
  return 1
}

ci_run_id="$(select_green_run ".github/workflows/ci.yml" "ci-required")" || fail "ci-required job evidence is missing or unsuccessful"
repo_guard_run_id="$(select_green_run ".github/workflows/repo-guard.yml" "repo-guard")" || fail "repo-guard job evidence is missing or unsuccessful"

read_version_at() {
  local ref="$1"
  gh api "repos/${repo}/contents/webassist/VERSION?ref=${ref}" |
    python3 -c '
import base64,json,sys
obj=json.load(sys.stdin)
value=base64.b64decode(obj["content"]).decode("utf-8").strip()
print(value)
'
}

version="$(read_version_at "$source_sha")" || fail "cannot read webassist/VERSION at source"
base_version="$(read_version_at "$base_sha")" || fail "cannot read webassist/VERSION at accepted transition base"

python3 - "$version" "$base_version" <<'PY' || fail "VERSION must be valid semver and strictly greater than accepted transition base VERSION"
import re,sys
pattern=re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
cur,base=sys.argv[1],sys.argv[2]
m1,m2=pattern.fullmatch(cur),pattern.fullmatch(base)
if not m1 or not m2:
    raise SystemExit(1)
if tuple(map(int,m1.groups())) <= tuple(map(int,m2.groups())):
    raise SystemExit(1)
PY

python3 - "$source_sha" "$pr_head" "$pr_head_ref" "$version" "$pr_number" "$base_sha" "$base_version" "$ci_run_id" "$repo_guard_run_id" <<'PY'
import json,sys
print(json.dumps({
    "sourceSha": sys.argv[1],
    "prHeadSha": sys.argv[2],
    "prHeadRef": sys.argv[3],
    "version": sys.argv[4],
    "acceptedPr": int(sys.argv[5]),
    "baseSha": sys.argv[6],
    "baseVersion": sys.argv[7],
    "ciRunId": int(sys.argv[8]),
    "repoGuardRunId": int(sys.argv[9])
}, separators=(",",":")))
PY
