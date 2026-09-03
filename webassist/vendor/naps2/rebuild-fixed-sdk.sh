#!/usr/bin/env bash
set -euo pipefail

UPSTREAM_REPOSITORY="https://github.com/cyanfish/naps2.git"
UPSTREAM_COMMIT="450cba65aaffe6387041050a573051a64cd80fe9"
PACKAGE_ID="WebAssistant.NAPS2.Sdk"
PACKAGE_VERSION="1.3.0-webassistant.1.450cba65"
PACKAGE_FILE="$PACKAGE_ID.$PACKAGE_VERSION.nupkg"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
output_dir="$product_root/vendor/nuget"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

for command_name in git dotnet python3; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "Required command is unavailable: $command_name" >&2
        exit 1
    }
done

git -C "$work_dir" init -q
git -C "$work_dir" remote add origin "$UPSTREAM_REPOSITORY"
git -C "$work_dir" fetch -q --depth=1 origin "$UPSTREAM_COMMIT"
git -C "$work_dir" checkout -q --detach FETCH_HEAD

actual_commit="$(git -C "$work_dir" rev-parse HEAD)"
[[ "$actual_commit" == "$UPSTREAM_COMMIT" ]] || {
    echo "Unexpected upstream commit: $actual_commit" >&2
    exit 1
}

python3 - "$work_dir" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
targets = root / "NAPS2.Setup/targets/SdkPackageTargets.targets"
text = targets.read_text(encoding="utf-8-sig")
expected = "        <PackageVersion>1.3.0</PackageVersion>"
replacement = (
    expected + "\n"
    "        <PackageId Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "WebAssistant.NAPS2.Sdk</PackageId>\n"
    "        <PackageVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "1.3.0-webassistant.1.450cba65</PackageVersion>"
)
if text.count(expected) != 1:
    raise SystemExit("unexpected upstream package target layout")
targets.write_text(text.replace(expected, replacement), encoding="utf-8")
PY

project="$work_dir/NAPS2.Sdk/NAPS2.Sdk.csproj"
mkdir -p -- "$output_dir"
rm -f -- "$output_dir/$PACKAGE_FILE"

dotnet build "$project" \
    --configuration Release \
    --property:TargetFrameworks=net10.0 \
    --property:GeneratePackageOnBuild=false

dotnet pack "$project" \
    --configuration Release \
    --no-build \
    --property:TargetFrameworks=net10.0 \
    --property:PackageOutputPath="$output_dir"

[[ -f "$output_dir/$PACKAGE_FILE" ]] || {
    echo "Expected package was not produced: $output_dir/$PACKAGE_FILE" >&2
    exit 1
}

echo "Rebuilt: $output_dir/$PACKAGE_FILE"
