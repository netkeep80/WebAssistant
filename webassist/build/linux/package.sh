#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
product_root="$(cd -- "$script_dir/../.." && pwd)"
project_path="$product_root/src/WebAssistant/WebAssistant.csproj"
source_config_path="$product_root/src/WebAssistant/appsettings.json"
default_config_path="$product_root/build/common/default-appsettings.json"
metadata_defaults_path="$product_root/build/common/product-metadata.defaults.json"
metadata_override_path="$product_root/src/WebAssistant/product-metadata.json"
metadata_resolver_project="$product_root/build/common/ProductMetadataResolver/ProductMetadataResolver.csproj"
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
[[ -f "$metadata_defaults_path" ]] || {
    echo "Отсутствует canonical product metadata defaults: $metadata_defaults_path" >&2
    exit 1
}
[[ -f "$metadata_resolver_project" ]] || {
    echo "Отсутствует ProductMetadataResolver: $metadata_resolver_project" >&2
    exit 1
}

version="$(<"$version_file")"
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "Некорректный VERSION: $version" >&2
    exit 1
}

command -v zip >/dev/null 2>&1 || {
    echo "Для сборки Linux artifact требуется zip." >&2
    exit 1
}
command -v sha256sum >/dev/null 2>&1 || {
    echo "Для сборки Linux artifact требуется sha256sum." >&2
    exit 1
}
command -v base64 >/dev/null 2>&1 || {
    echo "Для обработки product metadata требуется base64." >&2
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
metadata_environment_path="$(mktemp "$output_root/.webassistant-product-metadata.XXXXXX.env")"
rm -f -- "$metadata_environment_path"
staging_root=""

cleanup_build() {
    rm -f -- "$metadata_environment_path"
    if [[ -n "$staging_root" ]]; then
        rm -rf -- "$staging_root"
    fi
}
trap cleanup_build EXIT

"$dotnet_command" run \
    --project "$metadata_resolver_project" \
    --configuration Release \
    -- \
    --defaults "$metadata_defaults_path" \
    --override "$metadata_override_path" \
    --output "$metadata_environment_path"

[[ -f "$metadata_environment_path" ]] || {
    echo "ProductMetadataResolver не создал effective metadata output." >&2
    exit 1
}

declare -A metadata=()
while IFS='=' read -r key value; do
    [[ -n "$key" ]] || continue
    [[ -z "${metadata[$key]+x}" ]] || {
        echo "Duplicate ProductMetadataResolver key: $key" >&2
        exit 1
    }
    metadata["$key"]="$value"
done < "$metadata_environment_path"

required_metadata_keys=(
    metadataMode
    applicationNameBase64
    installerBaseNameBase64
    fileDescriptionBase64
    companyNameBase64
    copyrightBase64
    metadataInputSha256
    effectiveMetadataSha256)
for key in "${required_metadata_keys[@]}"; do
    [[ -n "${metadata[$key]:-}" ]] || {
        echo "ProductMetadataResolver не вернул обязательный key: $key" >&2
        exit 1
    }
done

metadata_mode="${metadata[metadataMode]}"
case "$metadata_mode" in
    defaults|override)
        ;;
    *)
        echo "Некорректный metadataMode: $metadata_mode" >&2
        exit 1
        ;;
esac

decode_metadata_value() {
    printf '%s' "$1" | base64 --decode
}

application_name="$(decode_metadata_value "${metadata[applicationNameBase64]}")"
installer_basename="$(decode_metadata_value "${metadata[installerBaseNameBase64]}")"
file_description="$(decode_metadata_value "${metadata[fileDescriptionBase64]}")"
company_name="$(decode_metadata_value "${metadata[companyNameBase64]}")"
copyright="$(decode_metadata_value "${metadata[copyrightBase64]}")"
metadata_input_sha256="${metadata[metadataInputSha256]}"
effective_metadata_sha256="${metadata[effectiveMetadataSha256]}"

for metadata_hash in "$metadata_input_sha256" "$effective_metadata_sha256"; do
    [[ "$metadata_hash" =~ ^[0-9a-f]{64}$ ]] || {
        echo "ProductMetadataResolver вернул некорректный SHA-256: $metadata_hash" >&2
        exit 1
    }
done

export ProductApplicationName="$application_name"
export ProductDisplayName="$application_name"
export ProductFileDescription="$file_description"
export ProductCompanyName="$company_name"
export ProductCopyright="$copyright"

artifact_name="${installer_basename}-linux-x64-${version}.zip"
artifact_path="$output_root/$artifact_name"
staging_root="$(mktemp -d "$output_root/.webassistant-linux-stage.XXXXXX")"
package_root_name="${artifact_name%.zip}"
package_root="$staging_root/$package_root_name"
app_directory="$package_root/app"
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
{
    while IFS= read -r line || [[ -n "$line" ]]; do
        if [[ "$line" == Description=* ]]; then
            printf 'Description=%s\n' "$file_description"
        else
            printf '%s\n' "$line"
        fi
    done < "$install_root/webassist.service"
} > "$package_root/webassist.service"
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
    "build/linux/package.sh" \
    "$metadata_mode" \
    "$application_name" \
    "$installer_basename" \
    "$metadata_input_sha256" \
    "$effective_metadata_sha256" >/dev/null

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

cleanup_build
trap - EXIT

echo "Linux artifact создан: $artifact_path (version $version, config $config_mode, metadata $metadata_mode)"
