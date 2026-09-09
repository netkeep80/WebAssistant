#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PRODUCT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
GUIDE="$PRODUCT_ROOT/docs/installation-guide.md"
TOOLCHAIN="$SCRIPT_DIR/toolchain.env"
VERSION_FILE="$PRODUCT_ROOT/VERSION"

MODE=""
PDF=""
EVIDENCE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:-}"; shift 2 ;;
    --pdf) PDF="${2:-}"; shift 2 ;;
    --evidence) EVIDENCE="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ "$MODE" == "fixture" || "$MODE" == "final" ]] || { echo "--mode must be fixture or final" >&2; exit 2; }
[[ -n "$PDF" ]] || { echo "--pdf is required" >&2; exit 2; }
[[ -n "$EVIDENCE" ]] || { echo "--evidence is required" >&2; exit 2; }
[[ -n "${WEBASSISTANT_SOURCE_SHA:-}" ]] || { echo "WEBASSISTANT_SOURCE_SHA is required" >&2; exit 2; }
[[ "$WEBASSISTANT_SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo "WEBASSISTANT_SOURCE_SHA must be 40 lowercase hexadecimal characters" >&2; exit 2; }
[[ -f "$PDF" ]] || { echo "PDF does not exist: $PDF" >&2; exit 2; }

VERSION="$(tr -d '\r\n' < "$VERSION_FILE")"
if [[ "$MODE" == "final" ]]; then
  EXPECTED_NAME="WebAssistant-Installation-Guide.pdf"
else
  EXPECTED_NAME="WebAssistant-Installation-Guide-fixture.pdf"
fi
[[ "$(basename -- "$PDF")" == "$EXPECTED_NAME" ]] || { echo "Unexpected PDF filename: $(basename -- "$PDF")" >&2; exit 2; }

# shellcheck disable=SC1090
source "$TOOLCHAIN"

command -v pdfinfo >/dev/null || { echo "pdfinfo is required" >&2; exit 2; }
command -v pdftoppm >/dev/null || { echo "pdftoppm is required for page render verification" >&2; exit 2; }
command -v pdftotext >/dev/null || { echo "pdftotext is required" >&2; exit 2; }

POPPLER_ACTUAL="$(pdfinfo -v 2>&1 | sed -n '1s/^pdfinfo version //p')"
[[ "$POPPLER_ACTUAL" == "$POPPLER_VERSION" ]] || { echo "poppler version mismatch: expected $POPPLER_VERSION, got $POPPLER_ACTUAL" >&2; exit 2; }

TMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT

python3 "$SCRIPT_DIR/evidence.py" \
  --mode "$MODE" \
  --manifest "$EVIDENCE" \
  --guide "$GUIDE" \
  --output-markdown "$TMP_DIR/revalidated.md" \
  --version "$VERSION" \
  --source-sha "$WEBASSISTANT_SOURCE_SHA"

PAGES="$(pdfinfo "$PDF" | awk '/^Pages:/ {print $2}')"
[[ "$PAGES" =~ ^[0-9]+$ && "$PAGES" -gt 0 ]] || { echo "PDF page count is invalid: $PAGES" >&2; exit 2; }

pdftotext "$PDF" "$TMP_DIR/text.txt"
for expected in \
  "Установка WebAssistant" \
  "Windows" \
  "ALT Linux 10.1" \
  "Windows: идентификатор и SHA-256 установщика" \
  "Windows: запуск установщика и подтверждение UAC" \
  "Windows: WebAssistant в Installed Apps и DisplayVersion" \
  "Windows: служба WebAssistant и /v1/health" \
  "Windows: стандартное удаление WebAssistant" \
  "ALT Linux 10.1: идентификатор и SHA-256 пакета" \
  "ALT Linux 10.1: установка из canonical ZIP" \
  "ALT Linux 10.1: systemd service и /v1/health" \
  "ALT Linux 10.1: перезапуск webassist.service" \
  "ALT Linux 10.1: удаление WebAssistant"
do
  grep -Fq -- "$expected" "$TMP_DIR/text.txt" || { echo "PDF text/caption missing: $expected" >&2; exit 2; }
done

mkdir -p "$TMP_DIR/rendered-pages"
pdftoppm -png -r 110 "$PDF" "$TMP_DIR/rendered-pages/page" >/dev/null 2>&1
mapfile -t RENDERED < <(find "$TMP_DIR/rendered-pages" -type f -name 'page-*.png' -print | sort)
[[ "${#RENDERED[@]}" -eq "$PAGES" ]] || { echo "rendered page count mismatch: expected $PAGES, got ${#RENDERED[@]}" >&2; exit 2; }
for page in "${RENDERED[@]}"; do
  [[ -s "$page" ]] || { echo "rendered page is empty: $page" >&2; exit 2; }
done

printf 'Verified %s page(s): %s\n' "$PAGES" "$PDF"
