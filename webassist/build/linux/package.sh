#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
project_path="$product_root/src/WebAssistant/WebAssistant.csproj"
source_config_path="$product_root/src/WebAssistant/appsettings.json"
default_config_path="$product_root/build/common/default-appsettings.json"
version_file="$product_root/VERSION"
install_root="$product_root/install/linux"
output_directory="${1:-$product_root/artifacts/linux-x64}"
package_root="$(realpath -m -- "$output_directory")"
app_directory="$package_root/app"

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

version="$(<"$version_file")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "Некорректный VERSION: $version" >&2
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

rm -rf -- "$package_root"
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
cp -- "$install_root/uninstall.sh" "$package_root/uninstall.sh"
cp -- "$install_root/webassist.service" "$package_root/webassist.service"
chmod +x -- "$package_root/install.sh" "$package_root/uninstall.sh"

[[ -x "$app_directory/WebAssistant" ]] || {
    echo "В package отсутствует исполняемый файл WebAssistant." >&2
    exit 1
}

echo "Linux package создан: $package_root (version $version, config $config_mode)"
