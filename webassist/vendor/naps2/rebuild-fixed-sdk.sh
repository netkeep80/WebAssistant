#!/usr/bin/env bash
set -euo pipefail

UPSTREAM_REPOSITORY="https://github.com/cyanfish/naps2.git"
UPSTREAM_COMMIT="450cba65aaffe6387041050a573051a64cd80fe9"
PACKAGE_ID="WebAssistant.NAPS2.Sdk"
PACKAGE_VERSION="1.3.0-webassistant.2.450cba65"
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


def replace_exact(path: pathlib.Path, expected: str, replacement: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    if text.count(expected) != 1:
        raise SystemExit(f"unexpected upstream layout in {path}: expected one exact match")
    path.write_text(text.replace(expected, replacement), encoding="utf-8")


targets = root / "NAPS2.Setup/targets/SdkPackageTargets.targets"
replace_exact(
    targets,
    "        <PackageVersion>1.3.0</PackageVersion>",
    "        <PackageVersion>1.3.0</PackageVersion>\n"
    "        <PackageId Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "WebAssistant.NAPS2.Sdk</PackageId>\n"
    "        <PackageVersion Condition=\"'$(MSBuildProjectName)' == 'NAPS2.Sdk'\">"
    "1.3.0-webassistant.2.450cba65</PackageVersion>",
)

paper_source_caps = root / "NAPS2.Sdk/Scan/PaperSourceCaps.cs"
replace_exact(
    paper_source_caps,
    "    public bool CanCheckIfFeederHasPaper { get; init; }\n}",
    "    public bool CanCheckIfFeederHasPaper { get; init; }\n\n"
    "    /// <summary>\n"
    "    /// Whether paper is currently present in the feeder when that state can be read.\n"
    "    /// Null means the current state is unavailable or unknown.\n"
    "    /// </summary>\n"
    "    public bool? FeederHasPaper { get; init; }\n}",
)

wia_driver = root / "NAPS2.Sdk/Scan/Internal/Wia/WiaScanDriver.cs"
replace_exact(
    wia_driver,
    "                        SupportsDuplex = device.SupportsDuplex(),\n"
    "                        CanCheckIfFeederHasPaper = true\n",
    "                        SupportsDuplex = device.SupportsDuplex(),\n"
    "                        CanCheckIfFeederHasPaper = true,\n"
    "                        FeederHasPaper = device.SupportsFeeder() ? TryGetFeederHasPaper(device) : null\n",
)
replace_exact(
    wia_driver,
    "    private PerSourceCaps GetItemCaps(WiaDevice device, WiaItem item, bool flatbed)\n",
    "    private static bool? TryGetFeederHasPaper(WiaDevice device)\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            return device.FeederReady();\n"
    "        }\n"
    "        catch (WiaException)\n"
    "        {\n"
    "            return null;\n"
    "        }\n"
    "    }\n\n"
    "    private PerSourceCaps GetItemCaps(WiaDevice device, WiaItem item, bool flatbed)\n",
)

twain_driver = root / "NAPS2.Sdk/Scan/Internal/Twain/LocalTwainController.cs"
replace_exact(
    twain_driver,
    "                            CanCheckIfFeederHasPaper =\n"
    "                                ds.Capabilities.CapAutomaticSenseMedium.IsSupported ||\n"
    "                                ds.Capabilities.CapFeederLoaded.IsSupported\n",
    "                            CanCheckIfFeederHasPaper =\n"
    "                                ds.Capabilities.CapAutomaticSenseMedium.IsSupported ||\n"
    "                                ds.Capabilities.CapFeederLoaded.IsSupported,\n"
    "                            FeederHasPaper = TryGetFeederHasPaper(ds)\n",
)
replace_exact(
    twain_driver,
    "    private PerSourceCaps GetPerSourceCaps(DataSource ds)\n",
    "    private bool? TryGetFeederHasPaper(DataSource ds)\n"
    "    {\n"
    "        var feederLoaded = ds.Capabilities.CapFeederLoaded;\n"
    "        if (!feederLoaded.IsSupported)\n"
    "        {\n"
    "            return null;\n"
    "        }\n\n"
    "        try\n"
    "        {\n"
    "            return feederLoaded.GetCurrent() == BoolType.True;\n"
    "        }\n"
    "        catch (Exception e)\n"
    "        {\n"
    "            _logger.LogDebug(e, \"Could not read TWAIN feeder-loaded state\");\n"
    "            return null;\n"
    "        }\n"
    "    }\n\n"
    "    private PerSourceCaps GetPerSourceCaps(DataSource ds)\n",
)
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
