#!/usr/bin/env bash
set -euo pipefail

check_result() {
  local name="$1"
  local required="$2"
  local result="$3"

  case "$required" in
    true|false) ;;
    *)
      echo "ci-required: invalid requirement flag for $name: $required" >&2
      return 1
      ;;
  esac

  case "$result" in
    success|skipped|failure|cancelled) ;;
    *)
      echo "ci-required: unknown result for $name: $result" >&2
      return 1
      ;;
  esac

  case "$required:$result" in
    true:success|false:success|false:skipped)
      return 0
      ;;
    *)
      echo "ci-required: $name required=$required result=$result" >&2
      return 1
      ;;
  esac
}

run_self_test() {
  local failures=0

  run_case() {
    local name="$1"
    local required="$2"
    local result="$3"
    local expected="$4"
    local actual

    if check_result "$name" "$required" "$result" >/dev/null 2>&1; then
      actual=0
    else
      actual=1
    fi

    if [[ "$actual" != "$expected" ]]; then
      echo "self-test failed: $name required=$required result=$result expected=$expected actual=$actual" >&2
      failures=1
    fi
  }

  run_case required-success true success 0
  run_case required-skipped true skipped 1
  run_case required-failure true failure 1
  run_case required-cancelled true cancelled 1
  run_case optional-success false success 0
  run_case optional-skipped false skipped 0
  run_case optional-failure false failure 1
  run_case optional-cancelled false cancelled 1
  run_case optional-unknown false unknown 1
  run_case malformed-required invalid success 1

  if [[ "$failures" -ne 0 ]]; then
    return 1
  fi

  echo "ci-required evaluator self-test: PASS"
}

if [[ "${1:-}" == "--self-test" ]]; then
  run_self_test
  exit $?
fi

: "${CORE_REQUIRED:?CORE_REQUIRED is required}"
: "${CORE_RESULT:?CORE_RESULT is required}"
: "${LINUX_SYSTEMD_REQUIRED:?LINUX_SYSTEMD_REQUIRED is required}"
: "${LINUX_SYSTEMD_RESULT:?LINUX_SYSTEMD_RESULT is required}"
: "${WINDOWS_SERVICE_REQUIRED:?WINDOWS_SERVICE_REQUIRED is required}"
: "${WINDOWS_SERVICE_RESULT:?WINDOWS_SERVICE_RESULT is required}"
: "${VIRTUAL_SCANNER_REQUIRED:?VIRTUAL_SCANNER_REQUIRED is required}"
: "${VIRTUAL_SCANNER_RESULT:?VIRTUAL_SCANNER_RESULT is required}"

failures=0

check_result core "$CORE_REQUIRED" "$CORE_RESULT" || failures=1
check_result linux-systemd "$LINUX_SYSTEMD_REQUIRED" "$LINUX_SYSTEMD_RESULT" || failures=1
check_result windows-service "$WINDOWS_SERVICE_REQUIRED" "$WINDOWS_SERVICE_RESULT" || failures=1
check_result virtual-scanner "$VIRTUAL_SCANNER_REQUIRED" "$VIRTUAL_SCANNER_RESULT" || failures=1

if [[ "$failures" -ne 0 ]]; then
  echo "ci-required: FAIL" >&2
  exit 1
fi

echo "ci-required: PASS"
