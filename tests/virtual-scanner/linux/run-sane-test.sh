#!/usr/bin/env bash
set -euo pipefail

out_dir="${1:-${RUNNER_TEMP:-/tmp}/webassistant-sane-test}"
mkdir -p -- "$out_dir"

scanimage -V | tee "$out_dir/version.txt"
scanimage -d test -T 2>&1 | tee "$out_dir/sane-api-test.txt"

scanimage -d test --format=pnm > "$out_dir/single-page.pnm"
test -s "$out_dir/single-page.pnm"
head -n 1 "$out_dir/single-page.pnm" | grep -Eq '^P[1-6]$'

scanimage -d test --help 2>&1 | tee "$out_dir/options.txt" >/dev/null
grep -Fq 'Automatic Document Feeder' "$out_dir/options.txt"

rm -f -- "$out_dir"/adf-*.pnm
scanimage \
  -d test \
  --source 'Automatic Document Feeder' \
  --format=pnm \
  --batch="$out_dir/adf-%02d.pnm" \
  --batch-count=3

mapfile -t adf_files < <(find "$out_dir" -maxdepth 1 -type f -name 'adf-*.pnm' | sort)
test "${#adf_files[@]}" -eq 3

for file in "${adf_files[@]}"; do
  test -s "$file"
  head -n 1 "$file" | grep -Eq '^P[1-6]$'
done

printf 'single_page=PASS\nadf_pages=%s\nresult=PASS\n' "${#adf_files[@]}" | tee "$out_dir/result.txt"
