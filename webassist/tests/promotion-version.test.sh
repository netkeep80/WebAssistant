#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
publisher="$root/build/distribution/update-config-repo.sh"
tmp="$(mktemp -d)"; trap 'rm -rf -- "$tmp"' EXIT
mkdir -p "$tmp/seed/sample-product" "$tmp/bare.git"
git init --bare --quiet "$tmp/bare.git"
git -C "$tmp/seed" init --quiet -b main
git -C "$tmp/seed" config user.name test
git -C "$tmp/seed" config user.email test@localhost.invalid
printf '%s\n' 'product config directory' > "$tmp/seed/sample-product/README.md"
git -C "$tmp/seed" add .
git -C "$tmp/seed" commit -qm 'prepare product config directory'
git -C "$tmp/seed" remote add origin "$tmp/bare.git"
git -C "$tmp/seed" push --quiet -u origin main
git --git-dir="$tmp/bare.git" symbolic-ref HEAD refs/heads/main
bash "$publisher" "$tmp/bare.git" sample-product 0.3.90
bash "$publisher" "$tmp/bare.git" sample-product 0.3.90
bash "$publisher" "$tmp/bare.git" sample-product 0.3.91
count="$(git --git-dir="$tmp/bare.git" rev-list --count main)"
[[ $count == 3 ]] || { echo "unexpected commit count $count" >&2; exit 1; }
[[ "$(git --git-dir="$tmp/bare.git" show main:sample-product/release-version.txt)" == 0.3.91 ]]
if bash "$publisher" "$tmp/bare.git" sample-product 0.3.89 >"$tmp/err" 2>&1; then echo 'downgrade accepted' >&2; exit 1; fi
if bash "$publisher" "$tmp/bare.git" other-product 0.3.92 >"$tmp/err" 2>&1; then echo 'missing project accepted' >&2; exit 1; fi
if bash "$publisher" "$tmp/bare.git" sample-product '0.3.92-invalid' >"$tmp/err" 2>&1; then echo 'bad version accepted' >&2; exit 1; fi
[[ "$(git --git-dir="$tmp/bare.git" rev-list --count main)" == 3 ]]
echo 'downstream version commit, idempotence, downgrade guard: PASS'
