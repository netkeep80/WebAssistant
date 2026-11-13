#!/usr/bin/env bash
# CI wrapper. Downstream repository identity and credentials are external CI variables.
set -euo pipefail
fail(){ printf 'promotion: %s\n' "$*" >&2; exit 1; }
check_environment() {
  local name value
  for name in CI_RELEASE_CONFIG_REPO CI_PROJECT_NAMESPACE CI_SERVER_URL CI_GITLAB_CICD_AGENT_USER CI_GITLAB_CICD_AGENT_SECRET; do
    value="$(printenv "$name" 2>/dev/null || true)"
    [[ -n $value && $value != null && $value != masked_null ]] || fail "missing CI variable: $name"
  done
  [[ $CI_RELEASE_CONFIG_REPO =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail 'invalid configuration repository name'
  [[ $CI_PROJECT_NAMESPACE =~ ^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$ ]] || fail 'invalid project namespace'
  [[ $CI_GITLAB_CICD_AGENT_USER =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail 'invalid Git username'
  if [[ $CI_SERVER_URL =~ ^http://[A-Za-z0-9._:-]+$ ]]; then
    allow_http="$(printenv CI_RELEASE_CONFIG_ALLOW_HTTP 2>/dev/null || true)"
    [[ $allow_http == 1 ]] || fail 'plaintext GitLab HTTP requires explicit private opt-in; use HTTPS'
  else
    [[ $CI_SERVER_URL =~ ^https://[A-Za-z0-9._:-]+$ ]] || fail 'invalid GitLab URL'
  fi
  command -v git >/dev/null || fail 'git is required in builder image'
}
if [[ $# == 1 && $1 == --check-ci-environment ]]; then
  check_environment
  exit 0
fi
[[ $# == 2 ]] || fail 'usage: publish-config-version.sh PRODUCT VERSION | --check-ci-environment'
product=$1
version=$2
check_environment
umask 077
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
cat > "$tmp/askpass" <<'ASKPASS'
#!/bin/sh
case "$1" in
  *Username*) printf '%s' "$CI_GITLAB_CICD_AGENT_USER" ;;
  *Password*) printf '%s' "$CI_GITLAB_CICD_AGENT_SECRET" ;;
  *) exit 1 ;;
esac
ASKPASS
chmod 700 "$tmp/askpass"
export CI_GITLAB_CICD_AGENT_USER CI_GITLAB_CICD_AGENT_SECRET
export GIT_ASKPASS="$tmp/askpass" GIT_TERMINAL_PROMPT=0
repo_url="$CI_SERVER_URL/$CI_PROJECT_NAMESPACE/$CI_RELEASE_CONFIG_REPO.git"
bash "$(dirname "$0")/update-config-repo.sh" "$repo_url" "$product" "$version"
