#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PRODUCT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
GUIDE="$PRODUCT_ROOT/docs/installation-guide.md"
SCHEMA="$SCRIPT_DIR/evidence.schema.json"
TOOLCHAIN="$SCRIPT_DIR/toolchain.env"
VERSION_FILE="$PRODUCT_ROOT/VERSION"
ARTIFACTS_ROOT="$PRODUCT_ROOT/artifacts"

MODE=""
EVIDENCE=""
OUTPUT_DIR=""

usage() {
  cat <<'EOF'
Usage:
  WEBASSISTANT_SOURCE_SHA=<40-hex-sha> docs/installation-guide/build.sh \
    --mode fixture|final --evidence <manifest.json> --output <directory-under-artifacts>

fixture -> WebAssistant-Installation-Guide-fixture.pdf
final   -> WebAssistant-Installation-Guide.pdf
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:-}"; shift 2 ;;
    --evidence) EVIDENCE="${2:-}"; shift 2 ;;
    --output) OUTPUT_DIR="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ "$MODE" == "fixture" || "$MODE" == "final" ]] || { echo "--mode must be fixture or final" >&2; exit 2; }
[[ -n "$EVIDENCE" ]] || { echo "--evidence is required" >&2; exit 2; }
[[ -n "$OUTPUT_DIR" ]] || { echo "--output is required" >&2; exit 2; }
[[ -n "${WEBASSISTANT_SOURCE_SHA:-}" ]] || { echo "WEBASSISTANT_SOURCE_SHA is required" >&2; exit 2; }
[[ "$WEBASSISTANT_SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]] || { echo "WEBASSISTANT_SOURCE_SHA must be 40 lowercase hexadecimal characters" >&2; exit 2; }
[[ -f "$SCHEMA" ]] || { echo "Missing evidence.schema.json: $SCHEMA" >&2; exit 2; }
[[ -f "$TOOLCHAIN" ]] || { echo "Missing toolchain.env: $TOOLCHAIN" >&2; exit 2; }
[[ -f "$VERSION_FILE" ]] || { echo "Missing VERSION: $VERSION_FILE" >&2; exit 2; }

VERSION="$(tr -d '\r\n' < "$VERSION_FILE")"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid VERSION: $VERSION" >&2; exit 2; }

mkdir -p "$ARTIFACTS_ROOT"
OUTPUT_DIR="$(python3 - "$OUTPUT_DIR" <<'PY'
import os, sys
print(os.path.realpath(sys.argv[1]))
PY
)"
ARTIFACTS_ROOT_REAL="$(python3 - "$ARTIFACTS_ROOT" <<'PY'
import os, sys
print(os.path.realpath(sys.argv[1]))
PY
)"
case "$OUTPUT_DIR/" in
  "$ARTIFACTS_ROOT_REAL/"*) ;;
  *) echo "--output must be inside $ARTIFACTS_ROOT_REAL" >&2; exit 2 ;;
esac
mkdir -p "$OUTPUT_DIR"

# shellcheck disable=SC1090
source "$TOOLCHAIN"

require_tool_version() {
  local name="$1"
  local expected="$2"
  local actual="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "$name version mismatch: expected $expected, got $actual" >&2
    exit 2
  fi
}

command -v pandoc >/dev/null || { echo "pandoc is required" >&2; exit 2; }
command -v weasyprint >/dev/null || { echo "weasyprint is required" >&2; exit 2; }
command -v pdfinfo >/dev/null || { echo "pdfinfo is required" >&2; exit 2; }
command -v pdftoppm >/dev/null || { echo "pdftoppm is required" >&2; exit 2; }

require_tool_version pandoc "$PANDOC_VERSION" "$(pandoc --version | sed -n '1s/^pandoc //p')"
require_tool_version weasyprint "$WEASYPRINT_VERSION" "$(weasyprint --version | sed -n 's/^WeasyPrint version //p')"
require_tool_version poppler "$POPPLER_VERSION" "$(pdfinfo -v 2>&1 | sed -n '1s/^pdfinfo version //p')"
require_tool_version poppler-pdftoppm "$POPPLER_VERSION" "$(pdftoppm -v 2>&1 | sed -n '1s/^pdftoppm version //p')"

if [[ "$MODE" == "final" ]]; then
  PDF_NAME="WebAssistant-Installation-Guide.pdf"
else
  PDF_NAME="WebAssistant-Installation-Guide-fixture.pdf"
fi
PDF_PATH="$OUTPUT_DIR/$PDF_NAME"

TMP_DIR="$(mktemp -d "$OUTPUT_DIR/.installation-guide.XXXXXX")"
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT

EXPANDED_MD="$TMP_DIR/installation-guide.expanded.md"
HTML_PATH="$TMP_DIR/installation-guide.html"
CSS_PATH="$TMP_DIR/installation-guide.css"

python3 "$SCRIPT_DIR/evidence.py" \
  --mode "$MODE" \
  --manifest "$EVIDENCE" \
  --guide "$GUIDE" \
  --output-markdown "$EXPANDED_MD" \
  --version "$VERSION" \
  --source-sha "$WEBASSISTANT_SOURCE_SHA"

cat > "$CSS_PATH" <<'CSS'
@page { size: A4; margin: 18mm 16mm 18mm 16mm; }
body { font-family: "DejaVu Sans", sans-serif; font-size: 10.5pt; line-height: 1.42; color: #111; }
h1 { font-size: 22pt; margin: 0 0 10mm 0; page-break-before: always; }
h1:first-of-type { page-break-before: auto; }
h2 { font-size: 15pt; margin-top: 8mm; page-break-after: avoid; }
code, pre { font-family: "DejaVu Sans Mono", monospace; }
pre { white-space: pre-wrap; overflow-wrap: anywhere; padding: 3mm; background: #f3f3f3; border: 1px solid #ddd; }
table { width: 100%; border-collapse: collapse; margin: 4mm 0; }
th, td { border: 1px solid #bbb; padding: 2mm; vertical-align: top; overflow-wrap: anywhere; }
figure.evidence-figure { margin: 5mm 0 7mm 0; page-break-inside: avoid; }
figure.evidence-figure img { display: block; max-width: 100%; max-height: 150mm; margin: 0 auto; border: 1px solid #bbb; }
figcaption { font-size: 9pt; margin-top: 2mm; text-align: center; color: #333; }
CSS

pandoc "$EXPANDED_MD" \
  --from=gfm+raw_html \
  --to=html5 \
  --standalone \
  --metadata "title=Установка WebAssistant $VERSION" \
  --css "$CSS_PATH" \
  --output "$HTML_PATH"

weasyprint "$HTML_PATH" "$PDF_PATH"

"$SCRIPT_DIR/verify.sh" --mode "$MODE" --pdf "$PDF_PATH" --evidence "$EVIDENCE"
printf '%s\n' "$PDF_PATH"
