#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
publisher="$repo_root/webassist/build/distribution/update-config-repo.sh"
tmp="$(mktemp -d)"; trap 'rm -rf -- "$tmp"' EXIT

mkdir -p "$tmp/seed" "$tmp/bare.git"
git init --bare --quiet "$tmp/bare.git"
git -C "$tmp/seed" init --quiet -b main
git -C "$tmp/seed" config user.name test
git -C "$tmp/seed" config user.email test@localhost.invalid
printf '%s\n' 'configuration repository root' > "$tmp/seed/README.md"
printf '%s\n' 'reserved path' > "$tmp/seed/blocked-product"
ln -s README.md "$tmp/seed/blocked-link"
git -C "$tmp/seed" add .
git -C "$tmp/seed" commit -qm 'prepare configuration repository'
git -C "$tmp/seed" remote add origin "$tmp/bare.git"
git -C "$tmp/seed" push --quiet -u origin main
git --git-dir="$tmp/bare.git" symbolic-ref HEAD refs/heads/main

# Первый publish сам создаёт каталог продукта.
bash "$publisher" "$tmp/bare.git" sample-product 0.3.90
bash "$publisher" "$tmp/bare.git" sample-product 0.3.90
bash "$publisher" "$tmp/bare.git" sample-product 0.3.91

# Новый продукт в том же конфигурационном репозитории также не требует ручного mkdir.
bash "$publisher" "$tmp/bare.git" other-product 0.3.92

count="$(git --git-dir="$tmp/bare.git" rev-list --count main)"
[[ $count == 4 ]] || { echo "unexpected commit count $count" >&2; exit 1; }
[[ "$(git --git-dir="$tmp/bare.git" show main:sample-product/release-version.txt)" == 0.3.91 ]]
[[ "$(git --git-dir="$tmp/bare.git" show main:other-product/release-version.txt)" == 0.3.92 ]]

if bash "$publisher" "$tmp/bare.git" sample-product 0.3.89 >"$tmp/err" 2>&1; then
  echo 'downgrade accepted' >&2
  exit 1
fi
if bash "$publisher" "$tmp/bare.git" blocked-product 0.3.93 >"$tmp/err" 2>&1; then
  echo 'file collision accepted as product directory' >&2
  exit 1
fi
if bash "$publisher" "$tmp/bare.git" blocked-link 0.3.93 >"$tmp/err" 2>&1; then
  echo 'symlink collision accepted as product directory' >&2
  exit 1
fi
if bash "$publisher" "$tmp/bare.git" sample-product '0.3.92-invalid' >"$tmp/err" 2>&1; then
  echo 'bad version accepted' >&2
  exit 1
fi

[[ "$(git --git-dir="$tmp/bare.git" rev-list --count main)" == 4 ]]
echo 'downstream version commit, idempotence, downgrade guard: PASS'
