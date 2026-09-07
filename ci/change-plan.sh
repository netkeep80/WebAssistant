#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo 'usage: ci/change-plan.sh --base <sha> --head <sha> | --paths [path ...]' >&2
  exit 2
}

paths=()

if [[ $# -ge 1 && "$1" == '--paths' ]]; then
  shift
  paths=("$@")
elif [[ $# -eq 4 && "$1" == '--base' && "$3" == '--head' ]]; then
  base_sha="$2"
  head_sha="$4"

  git cat-file -e "${base_sha}^{commit}" 2>/dev/null || {
    echo "change-plan: base commit is unavailable: $base_sha" >&2
    exit 1
  }
  git cat-file -e "${head_sha}^{commit}" 2>/dev/null || {
    echo "change-plan: head commit is unavailable: $head_sha" >&2
    exit 1
  }

  diff_file="$(mktemp)"
  trap 'rm -f -- "$diff_file"' EXIT
  git diff --name-only -z --diff-filter=ACDMRTUXB "$base_sha" "$head_sha" > "$diff_file"
  while IFS= read -r -d '' path; do
    paths+=("$path")
  done < "$diff_file"
else
  usage
fi

# VERSION обязателен в каждой принимаемой транзакции. Когда рядом есть
# содержательное изменение, он не должен искусственно расширять change plan.
# VERSION-only transition, напротив, трактуется fail-closed как full.
original_count=${#paths[@]}
meaningful_paths=()
for path in "${paths[@]}"; do
  if [[ "$path" == 'webassist/VERSION' && $original_count -gt 1 ]]; then
    continue
  fi
  meaningful_paths+=("$path")
done
paths=("${meaningful_paths[@]}")

core=true
linux_systemd=false
windows_service=false
virtual_linux=false
virtual_windows=false
smoke_linux=false
smoke_windows=false
full_cross_platform=false
saw_linux=false
saw_windows=false

require_full() {
  linux_systemd=true
  windows_service=true
  virtual_linux=true
  virtual_windows=true
  smoke_linux=true
  smoke_windows=true
  full_cross_platform=true
}

if [[ ${#paths[@]} -eq 0 ]]; then
  require_full
fi

for path in "${paths[@]}"; do
  case "$path" in
    README.md|webassist/README.md|webassist/docs/*|docs/*)
      # Documentation is validated by core/repository tests only.
      ;;

    webassist/src/WebAssistant/Scanning/WindowsScanAdapter.cs|\
    webassist/build/windows/*|\
    webassist/install/windows/*|\
    tests/windows-service/*|\
    tests/virtual-scanner/windows/*|\
    tests/core/WindowsScanAdapterTests.cs)
      saw_windows=true
      windows_service=true
      virtual_windows=true
      smoke_windows=true
      ;;

    webassist/src/WebAssistant/Scanning/LinuxScanAdapter.cs|\
    webassist/build/linux/*|\
    webassist/install/linux/*|\
    tests/linux-systemd/*|\
    tests/virtual-scanner/linux/*|\
    tests/core/LinuxScanAdapterTests.cs|\
    tests/core/LinuxVirtualScanAdapterTests.cs)
      saw_linux=true
      linux_systemd=true
      virtual_linux=true
      smoke_linux=true
      ;;

    webassist/VERSION)
      require_full
      break
      ;;

    # Общий product surface, governance/contract, CI infrastructure,
    # shared tests и любой неизвестный путь всегда расширяются fail-closed.
    webassist/src/*|\
    webassist/vendor/*|\
    webassist/NuGet.Config|\
    webassist/.gitlab-ci.yml|\
    contracts/*|\
    repo-policy.json|\
    .github/*|\
    ci/*|\
    tests/core/*|\
    webassist/*|\
    *)
      require_full
      break
      ;;
  esac
done

if [[ "$saw_linux" == true && "$saw_windows" == true ]]; then
  require_full
fi

if [[ "$virtual_linux" == true || "$virtual_windows" == true ]]; then
  virtual_scanner=true
else
  virtual_scanner=false
fi

printf 'core=%s\n' "$core"
printf 'linux_systemd=%s\n' "$linux_systemd"
printf 'windows_service=%s\n' "$windows_service"
printf 'virtual_linux=%s\n' "$virtual_linux"
printf 'virtual_windows=%s\n' "$virtual_windows"
printf 'virtual_scanner=%s\n' "$virtual_scanner"
printf 'smoke_linux=%s\n' "$smoke_linux"
printf 'smoke_windows=%s\n' "$smoke_windows"
printf 'full_cross_platform=%s\n' "$full_cross_platform"
