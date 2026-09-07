#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "Запустите install.sh от root." >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source_app="$script_dir/app"
source_service="$script_dir/webassist.service"
install_dir="/opt/webassist"
log_dir="/var/log/webassist"
data_dir="/var/lib/webassist"
service_unit="/etc/systemd/system/webassist.service"
config_file="$install_dir/appsettings.json"
runtime_packages=(libicu74 libgtk+3 libsane sane)

runtime_dependencies_installed() {
    local package
    for package in "${runtime_packages[@]}"; do
        rpm -q "$package" >/dev/null 2>&1 || return 1
    done
    return 0
}

[[ -d "$source_app" ]] || {
    echo "Не найден каталог package app: $source_app" >&2
    exit 1
}
[[ -f "$source_service" ]] || {
    echo "Не найден webassist.service: $source_service" >&2
    exit 1
}

for command_name in rpm systemctl groupadd groupdel useradd userdel usermod getent install; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда: $command_name" >&2
        exit 1
    }
done

if runtime_dependencies_installed; then
    echo "Системные runtime-зависимости WebAssistant уже установлены; apt-rpm не изменяется."
else
    command -v apt-get >/dev/null 2>&1 || {
        echo "Не найдена обязательная системная команда apt-get для установки отсутствующих runtime-зависимостей." >&2
        exit 1
    }

    if ! apt-get update; then
        echo "Не удалось обновить метаданные пакетных репозиториев ALT Linux через apt-rpm. Исправьте конфигурацию или состояние apt-rpm и повторите установку WebAssistant." >&2
        exit 1
    fi

    if ! apt-get install -y "${runtime_packages[@]}"; then
        echo "Не удалось установить runtime-зависимости WebAssistant через apt-rpm: ${runtime_packages[*]}. Исправьте состояние пакетной системы ALT Linux и повторите установку." >&2
        exit 1
    fi
fi

if ! getent group webassist >/dev/null 2>&1; then
    groupadd --system webassist
fi
if ! id webassist >/dev/null 2>&1; then
    nologin_shell="$(command -v nologin || true)"
    [[ -n "$nologin_shell" ]] || nologin_shell="/sbin/nologin"
    useradd --system --gid webassist --home-dir "$data_dir" --no-create-home --shell "$nologin_shell" webassist
fi

for scanner_group in scanner scaner lp; do
    if getent group "$scanner_group" >/dev/null 2>&1; then
        usermod -a -G "$scanner_group" webassist
    fi
done

systemctl stop webassist.service >/dev/null 2>&1 || true

mkdir -p -- "$install_dir" "$log_dir" "$data_dir"
rm -rf -- "$install_dir"/*
cp -a -- "$source_app"/. "$install_dir"/
chmod 0755 -- "$install_dir/WebAssistant"

cat > "$config_file" <<'JSON'
{
  "WebAssistant": {
    "Port": 17654,
    "LogDirectory": "/var/log/webassist",
    "Cors": {
      "Enabled": false,
      "AllowedOrigins": []
    },
    "FileSystem": {
      "RootDirectory": "/var/lib/webassist"
    }
  }
}
JSON

chown -R root:root -- "$install_dir"
chown -R webassist:webassist -- "$log_dir" "$data_dir"
chmod 0750 -- "$log_dir" "$data_dir"

install -m 0644 -- "$source_service" "$service_unit"
systemctl daemon-reload
systemctl enable webassist.service
systemctl restart webassist.service

if ! systemctl is-active --quiet webassist.service; then
    systemctl status webassist.service --no-pager >&2 || true
    journalctl -u webassist.service -n 100 --no-pager >&2 || true
    exit 1
fi

echo "WebAssistant установлен в $install_dir"
echo "Настройки: $config_file"
