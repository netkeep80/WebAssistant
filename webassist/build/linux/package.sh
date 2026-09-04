#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
project_path="$product_root/src/WebAssistant/WebAssistant.csproj"
version_file="$product_root/VERSION"
install_root="$product_root/install/linux"
output_directory="${1:-$product_root/artifacts/linux-x64}"
package_root="$(realpath -m -- "$output_directory")"
app_directory="$package_root/app"

[[ -f "$version_file" ]] || {
    echo "Отсутствует canonical VERSION: $version_file" >&2
    exit 1
}

version="$(<"$version_file")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "Некорректный VERSION: $version" >&2
    exit 1
}

has_dotnet_10_sdk() {
    command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -Eq '^10\.'
}

ensure_dotnet_10_sdk() {
    if has_dotnet_10_sdk; then
        return
    fi

    command -v apt-get >/dev/null 2>&1 || {
        echo "Не найден .NET SDK 10 и apt-get недоступен." >&2
        exit 1
    }

    if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
        apt-get update
        apt-get install -y dotnet-sdk-10.0
    elif command -v sudo >/dev/null 2>&1; then
        sudo apt-get update
        sudo apt-get install -y dotnet-sdk-10.0
    else
        echo "Для установки dotnet-sdk-10.0 требуются root-права или sudo." >&2
        exit 1
    fi

    has_dotnet_10_sdk || {
        echo "После установки .NET SDK 10 по-прежнему недоступен." >&2
        exit 1
    }
}

ensure_dotnet_10_sdk

rm -rf -- "$package_root"
mkdir -p -- "$app_directory"

dotnet publish "$project_path" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:ProductVersion="$version" \
    --output "$app_directory"

cp -- "$version_file" "$package_root/VERSION"
cp -- "$install_root/install.sh" "$package_root/install.sh"
cp -- "$install_root/uninstall.sh" "$package_root/uninstall.sh"
cp -- "$install_root/webassist.service" "$package_root/webassist.service"
chmod +x -- "$package_root/install.sh" "$package_root/uninstall.sh"

[[ -x "$app_directory/WebAssistant" ]] || {
    echo "В package отсутствует исполняемый файл WebAssistant." >&2
    exit 1
}

echo "Linux package создан: $package_root (version $version)"
