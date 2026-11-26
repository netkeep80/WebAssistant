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
    local libraries
    libraries="$(ldconfig -p 2>/dev/null || true)"
    grep -q 'libgtk-3\.so\.0' <<<"$libraries"
}

webassistant_has_libsane() {
    local libraries
    libraries="$(ldconfig -p 2>/dev/null || true)"
    grep -q 'libsane\.so\.1' <<<"$libraries"
}

webassistant_has_scanimage() {
    command -v scanimage >/dev/null 2>&1
}

# Ищем библиотеку только среди файлов установленного пакета.
webassistant_airscan_library() {
    rpm -q sane-airscan >/dev/null 2>&1 || return 1
    local files library
    files="$(rpm -ql sane-airscan)" || {
        echo "Не удалось получить список файлов RPM sane-airscan." >&2
        return 1
    }
    while IFS= read -r library; do
        if [[ "$library" == /* && "${library##*/}" == libsane-airscan.so.1 && -f "$library" && -r "$library" ]]; then
            printf '%s\n' "$library"
            return 0
        fi
    done <<<"$files"
    echo "В установленном RPM sane-airscan нет доступной библиотеки libsane-airscan.so.1." >&2
    return 1
}

# Проверяем разрешение зависимостей и перемещений ELF без вызова SANE.
webassistant_airscan_linkable() {
    local library="$1" report status=0
    report="$(LC_ALL=C ldd -r "$library" 2>&1)" || status=$?
    if (( status != 0 )) || grep -Eqi 'not found|undefined symbol|not a dynamic executable|statically linked|wrong ELF class' <<<"$report"; then
        printf 'Проверка ldd -r не пройдена: %s (код %s).\n%s\n' "$library" "$status" "$report" >&2
        return 1
    fi
}

# Python используется только установщиком для прямого dlopen/dlsym.
webassistant_has_loader_probe() {
    command -v python3 >/dev/null 2>&1 && python3 -I -c 'import ctypes' >/dev/null 2>&1
}

# Не вызываем функции SANE: проверяем загрузку с немедленным разрешением
# символов и наличие точек входа AirScan в отдельном непривилегированном процессе.
webassistant_airscan_loadable() {
    local library="$1" user="${2:-}" uid python report status=0
    local runner=()
    python="$(command -v python3)" || {
        echo "Не найден python3 для проверки загрузки sane-airscan." >&2
        return 1
    }
    if [[ -n "$user" ]]; then
        uid="$(id -u "$user")" || {
            echo "Не удалось определить UID учётной записи $user." >&2
            return 1
        }
        if [[ "$uid" == 0 ]]; then
            echo "Проверка загрузки sane-airscan от root запрещена." >&2
            return 1
        fi
        command -v runuser >/dev/null 2>&1 || {
            echo "Не найдена команда runuser для проверки от имени $user." >&2
            return 1
        }
        runner=(runuser -u "$user" --)
    else
        uid="$(id -u)"
    fi
    printf 'Проверка загрузки sane-airscan: пользователь=%s UID=%s библиотека=%s Python=%s\n' "${user:-текущий}" "$uid" "$library" "$python" >&2
    report="$("${runner[@]}" env -u LD_PRELOAD -u LD_LIBRARY_PATH -u LD_AUDIT -u LD_DEBUG -u LD_DEBUG_OUTPUT LC_ALL=C \
        "$python" -I -c '
import ctypes, os, sys
path, uid = sys.argv[1:]
if os.geteuid() != int(uid):
    sys.exit("UID не совпадает: фактический=%d ожидаемый=%s" % (os.geteuid(), uid))
print("UID=%d GID=%d groups=%s library=%s" % (os.geteuid(), os.getegid(), os.getgroups(), path), flush=True)
try:
    lib = ctypes.CDLL(path, mode=os.RTLD_NOW | os.RTLD_LOCAL)
except OSError as exc:
    sys.exit("dlopen: " + str(exc))
for name in ("init", "exit", "get_devices", "open", "close",
             "get_option_descriptor", "control_option", "get_parameters",
             "start", "read", "cancel", "set_io_mode", "get_select_fd"):
    symbol = "sane_airscan_" + name
    try:
        getattr(lib, symbol)
    except AttributeError as exc:
        sys.exit("dlsym " + symbol + ": " + str(exc))
print("dlopen(RTLD_NOW)/dlsym: PASS")
' "$library" "$uid" 2>&1)" || status=$?
    if (( status != 0 )); then
        printf 'Проверка загрузки sane-airscan завершилась ошибкой (код %s):\n%s\n' "$status" "$report" >&2
        return 1
    fi
    printf '%s\n' "$report"
}

webassistant_has_airscan() {
    command -v airscan-discover >/dev/null 2>&1 || return 1
    local library
    library="$(webassistant_airscan_library)" || return 1
    webassistant_airscan_linkable "$library"
}

# Не интерпретируем синтаксис регистрации SANE и права от имени root.
# Неизвестная копия RPM может изменить действующую регистрацию, в том
# числе после прерывания прежней установки. Не перемещаем её автоматически.
webassistant_require_no_airscan_rpmnew() {
    local generated="$1/dll.d/airscan.rpmnew"
    if [[ -e "$generated" || -L "$generated" ]]; then
        echo "Неразрешённый файл $generated. Согласуйте регистрацию AirScan с администратором и сохраните копию вне dll.d перед повторной установкой. Установщик не перемещает ранее существовавшие файлы." >&2
        return 1
    fi
}

# Свидетельство находится рядом с каталогом SANE, но вне области чтения SANE.
# Его наличие (даже при повреждении содержимого) запрещает повторную установку.
webassistant_require_no_airscan_pending() {
    if [[ -e "$1" || -L "$1" ]]; then
        echo "Незавершённая пакетная операция: $1. Администратор должен проверить исходную регистрацию и RPM-копии, разрешить конфликт и удалить свидетельство перед повтором." >&2
        return 1
    fi
}

webassistant_begin_airscan_transaction() {
    local pending="$1" before="$2"
    if ! mkdir -m 0700 -- "$pending" ||
       ! (umask 077; printf '%s\n' "$before" > "$pending/registration-before") ||
       ! sync -f "$pending"; then
        echo "Не удалось устойчиво записать свидетельство $pending; пакетная операция запрещена." >&2
        return 1
    fi
}

webassistant_finish_airscan_transaction() {
    local pending="$1"
    # Сначала закрепляем проверенное состояние файлов и архив RPM, затем снимаем блокировку.
    if ! sync -f "$pending" ||
       ! rm -- "$pending/registration-before" ||
       ! rmdir -- "$pending"; then
        echo "Не удалось завершить свидетельство $pending; требуется проверка администратором." >&2
        return 1
    fi
}

webassistant_airscan_registration_snapshot() {
    local config="$1/dll.d/airscan"
    if [[ -e "$config" || -L "$config" ]]; then
        if [[ ! -f "$config" || -L "$config" ]]; then
            echo "Необычный тип административского файла $config; требуется проверка администратором." >&2
            return 1
        fi
        stat -c '%a:%u:%g' -- "$config" && sha256sum -- "$config"
    fi
}

# Состояния: исходный файл сохранён; новая копия RPM; архивирована.
# При отказе mv копия остаётся видимой и блокирует следующий запуск.
# Условия: нет параллельной пакетной транзакции или изменения администратором.
webassistant_preserve_airscan_registration() {
    local root="$1" before="$2" after generated archive
    generated="$root/dll.d/airscan.rpmnew"
    if [[ -n "$before" ]]; then
        after="$(webassistant_airscan_registration_snapshot "$root")" || return 1
        if [[ "$before" != "$after" ]]; then
            echo "Пакетная транзакция изменила содержимое или права исходного dll.d/airscan; требуется проверка администратором." >&2
            return 1
        fi
        if [[ -e "$generated" || -L "$generated" ]]; then
            if [[ ! -f "$generated" || -L "$generated" ]]; then
                echo "Неожиданный тип нового файла $generated; требуется вмешательство администратора." >&2
                return 1
            fi
            archive="$(mktemp -d "$root/dll.d/.webassistant-airscan-rpmnew.XXXXXX")" || return 1
            if ! mv -- "$generated" "$archive/airscan.rpmnew"; then
                echo "Не удалось сохранить новый файл RPM: $generated. Повторная установка заблокирована до разрешения конфликта." >&2
                return 1
            fi
            echo "Новый файл RPM сохранён без изменений: $archive/airscan.rpmnew; исходная регистрация сохранена."
        fi
    fi
    webassistant_require_no_airscan_rpmnew "$root"
}

webassistant_resolve_icu_package() {
    local package
    local best_package=""
    local best_major=-1

    while IFS= read -r package; do
        if [[ "$package" =~ ^libicu([0-9]+)$ ]]; then
            local major="${BASH_REMATCH[1]}"
            if (( major > best_major )); then
                best_major="$major"
                best_package="$package"
            fi
        fi
    done < <(apt-cache pkgnames 2>/dev/null || true)

    [[ -n "$best_package" ]] || return 1
    printf '%s\n' "$best_package"
}

webassistant_missing_capabilities() {
    local missing=()
    webassistant_has_icu || missing+=(ICU)
    webassistant_has_gtk3 || missing+=(GTK3)
    webassistant_has_libsane || missing+=(libsane)
    webassistant_has_scanimage || missing+=(scanimage)
    webassistant_has_airscan || missing+=(sane-airscan)
    webassistant_has_loader_probe || missing+=(python3/ctypes)
    command -v runuser >/dev/null 2>&1 || missing+=(runuser)
    ((${#missing[@]} == 0)) || printf '%s\n' "${missing[@]}"
}

ensure_webassistant_runtime_dependencies() {
    command -v ldconfig >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда ldconfig для проверки зависимостей среды выполнения WebAssistant." >&2
        return 1
    }

    command -v ldd >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда ldd для проверки sane-airscan." >&2
        return 1
    }

    command -v rpm >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда rpm для проверки sane-airscan." >&2
        return 1
    }

    local missing_packages=()
    local icu_package=""
    local sane_config_dir="${1:-/etc/sane.d}" registration_before install_status=0
    local pending="$sane_config_dir.webassistant-airscan-pending"
    webassistant_require_no_airscan_rpmnew "$sane_config_dir" || return 1
    webassistant_require_no_airscan_pending "$pending" || return 1

    if ! webassistant_has_icu; then
        command -v apt-cache >/dev/null 2>&1 || {
            echo "Среда выполнения ICU отсутствует, а apt-cache не найден для разрешения доступного пакета ICU." >&2
            return 1
        }

        icu_package="$(webassistant_resolve_icu_package)" || {
            echo "Среда выполнения ICU отсутствует, и в подключённых репозиториях ALT Linux не найден подходящий пакет libicu с числовой основной версией." >&2
            return 1
        }
        missing_packages+=("$icu_package")
    fi

    webassistant_has_gtk3 || missing_packages+=(libgtk+3)
    webassistant_has_libsane || missing_packages+=(libsane)
    webassistant_has_scanimage || missing_packages+=(sane)
    webassistant_has_loader_probe || missing_packages+=(python3)
    command -v runuser >/dev/null 2>&1 || missing_packages+=(util-linux)
    if ! webassistant_has_airscan; then
        if rpm -q sane-airscan >/dev/null 2>&1; then
            echo "Пакет sane-airscan установлен, но его библиотека, динамические зависимости или airscan-discover недоступны. Проверьте целостность пакета и восстановите его штатными средствами ALT Linux." >&2
            return 1
        fi
        missing_packages+=(sane-airscan)

    fi

    if ((${#missing_packages[@]} > 0)); then
        command -v apt-get >/dev/null 2>&1 || {
            echo "Отсутствуют зависимости среды выполнения WebAssistant, но apt-get недоступен для их установки: ${missing_packages[*]}." >&2
            return 1
        }

        registration_before="$(webassistant_airscan_registration_snapshot "$sane_config_dir")" || return 1
        webassistant_begin_airscan_transaction "$pending" "$registration_before" || return 1
        if apt-get update; then
            apt-get install -y "${missing_packages[@]}" || install_status=$?
        else
            install_status=$?
            echo "Не удалось обновить метаданные пакетных репозиториев ALT Linux через apt-rpm; требуемые пакеты: ${missing_packages[*]}." >&2
        fi
        webassistant_preserve_airscan_registration "$sane_config_dir" "$registration_before" || return 1
        webassistant_finish_airscan_transaction "$pending" || return 1
        if (( install_status != 0 )); then
            echo "Не удалось установить отсутствующие зависимости среды выполнения WebAssistant: ${missing_packages[*]}." >&2
            return 1
        fi
    else
        echo "Системные возможности среды выполнения WebAssistant уже доступны; apt-rpm не изменяется."
        return 0
    fi

    local still_missing=()
    mapfile -t still_missing < <(webassistant_missing_capabilities)
    if ((${#still_missing[@]} > 0)); then
        echo "После установки пакетов всё ещё отсутствуют возможности среды выполнения WebAssistant: ${still_missing[*]}." >&2
        return 1
    fi

    echo "Зависимости среды выполнения WebAssistant доступны."
}
