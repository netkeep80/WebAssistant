#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
project_path="$product_root/src/WebAssistant/WebAssistant.csproj"
source_config_path="$product_root/src/WebAssistant/appsettings.json"
default_config_path="$product_root/build/common/default-appsettings.json"
provenance_writer="$product_root/build/common/write-provenance.sh"
version_file="$product_root/VERSION"
install_root="$product_root/install/linux"
output_directory="${1:-$product_root/artifacts/linux-x64}"
output_root="$(realpath -m -- "$output_directory")"

explicit_dotnet_root="${WEBASSISTANT_DOTNET_ROOT:-}"
bundled_dotnet_root="$product_root/toolchain/dotnet/linux-x64"
bootstrap_allowed="${WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP:-1}"
default_cache_root="${XDG_CACHE_HOME:-${HOME:-/tmp}/.cache}"
bootstrap_dotnet_root="${WEBASSISTANT_DOTNET_INSTALL_DIR:-$default_cache_root/webassistant/dotnet}"
dotnet_command=""
dotnet_source=""

[[ -f "$version_file" ]] || {
    echo "Отсутствует canonical VERSION: $version_file" >&2
    exit 1
}
[[ -f "$provenance_writer" ]] || {
    echo "Отсутствует provenance writer: $provenance_writer" >&2
    exit 1
}

version="$(<"$version_file")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "Некорректный VERSION: $version" >&2
    exit 1
}

artifact_name="WebAssistant-linux-x64-${version}.zip"
artifact_path="$output_root/$artifact_name"

command -v zip >/dev/null 2>&1 || {
    echo "Для сборки Linux artifact требуется zip." >&2
    exit 1
}
command -v sha256sum >/dev/null 2>&1 || {
    echo "Для сборки Linux artifact требуется sha256sum." >&2
    exit 1
}

dotnet_has_sdk_10() {
    local candidate="$1"
    [[ -x "$candidate" ]] && "$candidate" --list-sdks 2>/dev/null | grep -Eq '^10\.'
}

select_dotnet_root() {
    local root="$1"
    local source="$2"
    local candidate="$root/dotnet"

    if dotnet_has_sdk_10 "$candidate"; then
        dotnet_command="$candidate"
        dotnet_source="$source"
        export DOTNET_ROOT="$root"
        return 0
    fi

    return 1
}

if [[ -n "$explicit_dotnet_root" ]]; then
    select_dotnet_root "$explicit_dotnet_root" "WEBASSISTANT_DOTNET_ROOT" || {
        echo "WEBASSISTANT_DOTNET_ROOT не содержит работоспособный .NET SDK 10: $explicit_dotnet_root" >&2
        exit 1
    }
elif [[ -e "$bundled_dotnet_root/dotnet" ]]; then
    select_dotnet_root "$bundled_dotnet_root" "bundled offline SDK" || {
        echo "Bundled toolchain существует, но не содержит работоспособный .NET SDK 10: $bundled_dotnet_root" >&2
        exit 1
    }
fi

if [[ -z "$dotnet_command" ]] && command -v dotnet >/dev/null 2>&1; then
    system_dotnet="$(command -v dotnet)"
    if dotnet_has_sdk_10 "$system_dotnet"; then
        dotnet_command="$system_dotnet"
        dotnet_source="system SDK"
    fi
fi

if [[ -z "$dotnet_command" ]]; then
    case "$bootstrap_allowed" in
        1|true|TRUE|yes|YES)
            ;;
        0|false|FALSE|no|NO|"")
            echo "Не найден .NET SDK 10." >&2
            echo "Поместите SDK в $bundled_dotnet_root, задайте WEBASSISTANT_DOTNET_ROOT=/path/to/dotnet-root или разрешите online bootstrap: WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=1." >&2
            exit 1
            ;;
        *)
            echo "Некорректное значение WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP: $bootstrap_allowed (ожидается 0/1)." >&2
            exit 1
            ;;
    esac

    command -v curl >/dev/null 2>&1 || {
        echo "Online bootstrap разрешён, но curl недоступен." >&2
        exit 1
    }

    mkdir -p -- "$bootstrap_dotnet_root"
    install_script="$(mktemp "${TMPDIR:-/tmp}/webassistant-dotnet-install.XXXXXX.sh")"
    cleanup_install_script() {
        rm -f -- "$install_script"
    }
    trap cleanup_install_script EXIT

    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$install_script"
    bash "$install_script" --channel 10.0 --install-dir "$bootstrap_dotnet_root" --no-path

    select_dotnet_root "$bootstrap_dotnet_root" "online bootstrap" || {
        echo "После online bootstrap .NET SDK 10 по-прежнему недоступен: $bootstrap_dotnet_root" >&2
        exit 1
    }

    cleanup_install_script
    trap - EXIT
fi

echo "Используется .NET SDK 10: $dotnet_source ($dotnet_command)"

mkdir -p -- "$output_root"
staging_root="$(mktemp -d "$output_root/.webassistant-linux-stage.XXXXXX")"
package_root_name="${artifact_name%.zip}"
package_root="$staging_root/$package_root_name"
app_directory="$package_root/app"

cleanup_staging() {
    rm -rf -- "$staging_root"
}
trap cleanup_staging EXIT
mkdir -p -- "$app_directory"

"$dotnet_command" publish "$project_path" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:ProductVersion="$version" \
    --output "$app_directory"

package_config_path="$app_directory/appsettings.json"
if [[ -f "$source_config_path" ]]; then
    cp -- "$source_config_path" "$package_config_path"
    config_mode="source-appsettings"
else
    [[ -f "$default_config_path" ]] || {
        echo "Отсутствует repository-owned safe default config: $default_config_path" >&2
        exit 1
    }
    cp -- "$default_config_path" "$package_config_path"
    config_mode="generated-default"
fi

cp -- "$version_file" "$package_root/VERSION"
cp -- "$install_root/install.sh" "$package_root/install.sh"
cp -- "$install_root/runtime-dependencies.sh" "$package_root/runtime-dependencies.sh"
cp -- "$install_root/uninstall.sh" "$package_root/uninstall.sh"
cp -- "$install_root/webassist.service" "$package_root/webassist.service"
chmod +x -- "$package_root/install.sh" "$package_root/runtime-dependencies.sh" "$package_root/uninstall.sh"

[[ -x "$app_directory/WebAssistant" ]] || {
    echo "В package отсутствует исполняемый файл WebAssistant." >&2
    exit 1
}
[[ -f "$package_config_path" ]] || {
    echo "В package отсутствует appsettings.json." >&2
    exit 1
}
[[ -f "$package_root/runtime-dependencies.sh" ]] || {
    echo "В package отсутствует runtime-dependencies.sh." >&2
    exit 1
}

rm -f -- "$artifact_path" "${artifact_path}.sha256" "${artifact_path}.provenance.json"
(
    cd -- "$staging_root"
    zip -q -r "$artifact_path" "$package_root_name"
)

[[ -f "$artifact_path" ]] || {
    echo "Не создан canonical Linux artifact: $artifact_path" >&2
    exit 1
}

sdk_version="$("$dotnet_command" --version 2>/dev/null || true)"
[[ -n "$sdk_version" ]] || sdk_version="unknown"
source_sha="${WEBASSISTANT_SOURCE_SHA:-unknown}"
bash "$provenance_writer" \
    "$artifact_path" \
    "$version" \
    "$source_sha" \
    "linux-x64" \
    "$sdk_version" \
    "$config_mode" \
    "build/linux/package.sh" >/dev/null

[[ -f "${artifact_path}.sha256" ]] || {
    echo "Не создан SHA-256 evidence: ${artifact_path}.sha256" >&2
    exit 1
}
[[ -f "${artifact_path}.provenance.json" ]] || {
    echo "Не создан provenance evidence: ${artifact_path}.provenance.json" >&2
    exit 1
}

recorded_sha="$(awk 'NR == 1 { print $1 }' "${artifact_path}.sha256")"
final_sha="$(sha256sum -- "$artifact_path" | awk '{print $1}')"
[[ "$recorded_sha" == "$final_sha" ]] || {
    echo "Artifact bytes изменились после фиксации SHA-256." >&2
    exit 1
}

cleanup_staging
trap - EXIT

echo "Linux artifact создан: $artifact_path (version $version, config $config_mode)"
