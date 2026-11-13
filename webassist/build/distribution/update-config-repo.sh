#!/usr/bin/env bash
# GitLab-neutral commit of a verified release version to a downstream configuration repository.
set -euo pipefail
fail() { printf 'release-config: %s\n' "$*" >&2; exit 1; }
[[ $# == 3 ]] || fail 'usage: update-config-repo.sh REPOSITORY_URL PRODUCT VERSION'
remote_url=$1; product=$2; version=$3
[[ -n $remote_url && $remote_url != *$'\n'* ]] || fail 'invalid repository URL'
[[ $product =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ && $product != . && $product != .. ]] || fail 'invalid product'
[[ $version =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'invalid version'
command -v git >/dev/null || fail 'git is required'
command -v sort >/dev/null || fail 'sort is required'
umask 077
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
for attempt in 1 2 3; do
  rm -rf -- "$work/repo"
  git clone --quiet "$remote_url" "$work/repo" || fail 'cannot clone downstream configuration repository'
  branch="$(git -C "$work/repo" branch --show-current)"
  [[ -n $branch && $branch != HEAD ]] || fail 'configuration repository default branch is unavailable'
  release_dir="$work/repo/$product"
  if [[ -e $release_dir || -L $release_dir ]]; then
    [[ -d $release_dir && ! -L $release_dir ]] || fail 'product configuration path must be a real directory'
  else
    mkdir -- "$release_dir" || fail 'cannot create product configuration directory'
  fi
  version_file="$release_dir/release-version.txt"
  [[ ! -L $version_file ]] || fail 'version file symlink forbidden'
  if [[ -f $version_file ]]; then
    previous="$(cat "$version_file")"
    [[ $previous =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'invalid existing version descriptor'
    if [[ $previous == "$version" ]]; then
      echo 'configuration already selects the exact release version'
      exit 0
    fi
    [[ $(printf '%s\n%s\n' "$previous" "$version" | sort -V | tail -1) == "$version" ]] || fail 'refusing to downgrade downstream release version'
  fi
  printf '%s\n' "$version" > "$version_file"
  git -C "$work/repo" add -- "$product/release-version.txt"
  git -C "$work/repo" -c user.name='release-ci' -c user.email='release-ci@localhost.invalid' \
    commit --quiet -m "Set $product release to $version" || fail 'cannot create version commit'
  if git -C "$work/repo" push --quiet origin "HEAD:refs/heads/$branch"; then
    echo "downstream configuration committed: $product $version"
    exit 0
  fi
done
fail 'configuration repository advanced concurrently; retries exhausted'
