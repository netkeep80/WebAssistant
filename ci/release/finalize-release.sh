#!/usr/bin/env bash
set -euo pipefail

source_sha=""
evidence=""
pdf=""
repo="${GH_REPO:-${GITHUB_REPOSITORY:-}}"
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
guide_verify="${repo_root}/webassist/docs/installation-guide/verify.sh"

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

# This check is deliberately first: once main advances, the frozen candidate is
# no longer eligible for official publication, regardless of local evidence.
main_json="$(gh api "repos/${repo}/branches/main")" || fail "cannot read current main"
main_sha="$(printf '%s' "$main_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["commit"]["sha"])')" || fail "cannot parse current main"
[[ "$main_sha" == "$source_sha" ]] || fail "current main no longer equals frozen source SHA"

[[ -f "$evidence" ]] || fail "final evidence manifest does not exist: $evidence"
[[ -f "$pdf" ]] || fail "WebAssistant-Installation-Guide.pdf does not exist: $pdf"
[[ "$(basename -- "$pdf")" == "WebAssistant-Installation-Guide.pdf" ]] || fail "PDF must be named WebAssistant-Installation-Guide.pdf"
[[ -x "$guide_verify" || -f "$guide_verify" ]] || fail "docs/installation-guide/verify.sh is missing"

# Full draft inspection, staged-installer identity verification, PDF upload and
# draft publication are added by the next TDD slice. Until then this boundary
# fails closed after proving that the frozen source has not moved.
fail "final publication boundary is not complete yet"
