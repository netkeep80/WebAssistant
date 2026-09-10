#!/usr/bin/env bash
set -euo pipefail

webassistant_has_icu() {
    local libraries
    libraries="$(ldconfig -p 2>/dev/null || true)"
    grep -Eq 'libicuuc\.so\.[0-9]+' <<<"$libraries" &&
        grep -Eq 'libicui18n\.so\.[0-9]+' <<<"$libraries" &&
        grep -Eq 'libicudata\.so\.[0-9]+' <<<"$libraries"
}

webassistant_has_gtk3() {
    ldconfig -p 2>/dev/null | grep -q 'libgtk-3\.so\.0'
}

webassistant_has_libsane() {
    ldconfig -p 2>/dev/null | grep -q 'libsane\.so\.1'
}

webassistant_has_scanimage() {
    command -v scanimage >/dev/null 2>&1
}

webassistant_resolve_icu_package() {
    apt-cache pkgnames 2>/dev/null |
        sed -n -E 's/^(libicu([0-9]+))$/\2 \1/p' |
        sort -nr |
        awk 'NR == 1 { print $2 }'
}

webassistant_missing_capabilities() {
    local missing=()
    webassistant_has_icu || missing+=(ICU)
    webassistant_has_gtk3 || missing+=(GTK3)
    webassistant_has_libsane || missing+=(libsane)
    webassistant_has_scanimage || missing+=(scanimage)
    printf '%s\n' "${missing[@]}"
}

ensure_webassistant_runtime_dependencies() {
    command -v ldconfig >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда ldconfig для проверки runtime-зависимостей WebAssistant." >&2
        return 1
    }

    local missing_packages=()
    local icu_package=""

    if ! webassistant_has_icu; then
        command -v apt-cache >/dev/null 2>&1 || {
            echo "ICU runtime отсутствует, а apt-cache не найден для разрешения доступного пакета ICU." >&2
            return 1
        }
        command -v sed >/dev/null 2>&1 || {
            echo "Не найдена команда sed, необходимая для разрешения доступного пакета ICU." >&2
            return 1
        }
        command -v sort >/dev/null 2>&1 || {
            echo "Не найдена команда sort, необходимая для разрешения доступного пакета ICU." >&2
            return 1
        }
        command -v awk >/dev/null 2>&1 || {
            echo "Не найдена команда awk, необходимая для разрешения доступного пакета ICU." >&2
            return 1
        }

        icu_package="$(webassistant_resolve_icu_package)"
        [[ -n "$icu_package" ]] || {
            echo "ICU runtime отсутствует, и в подключённых репозиториях ALT Linux не найден подходящий пакет libicu с числовой major-версией." >&2
            return 1
        }
        missing_packages+=("$icu_package")
    fi

    webassistant_has_gtk3 || missing_packages+=(libgtk+3)
    webassistant_has_libsane || missing_packages+=(libsane)
    webassistant_has_scanimage || missing_packages+=(sane)

    if ((${#missing_packages[@]} > 0)); then
        command -v apt-get >/dev/null 2>&1 || {
            echo "Отсутствуют runtime-зависимости WebAssistant, но apt-get недоступен для их установки: ${missing_packages[*]}." >&2
            return 1
        }

        if ! apt-get update; then
            echo "Не удалось обновить метаданные пакетных репозиториев ALT Linux через apt-rpm." >&2
            return 1
        fi

        if ! apt-get install -y "${missing_packages[@]}"; then
            echo "Не удалось установить отсутствующие runtime-зависимости WebAssistant: ${missing_packages[*]}." >&2
            return 1
        fi
    else
        echo "Системные runtime-возможности WebAssistant уже доступны; apt-rpm не изменяется."
        return 0
    fi

    local still_missing
    still_missing="$(webassistant_missing_capabilities)"
    if [[ -n "$still_missing" ]]; then
        echo "После установки пакетов всё ещё отсутствуют runtime-возможности WebAssistant: $(tr '\n' ' ' <<<"$still_missing" | sed 's/[[:space:]]*$//')." >&2
        return 1
    fi

    echo "Runtime-зависимости WebAssistant доступны."
}
