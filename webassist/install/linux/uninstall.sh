#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "Запустите uninstall.sh от root." >&2
    exit 1
fi

purge_data=false
if [[ "${1:-}" == "--purge-data" ]]; then
    purge_data=true
elif [[ $# -gt 0 ]]; then
    echo "Использование: $0 [--purge-data]" >&2
    exit 2
fi

systemctl disable --now webassist.service >/dev/null 2>&1 || true
rm -f -- /etc/systemd/system/webassist.service
systemctl daemon-reload
rm -rf -- /opt/webassist

if [[ "$purge_data" == true ]]; then
    rm -rf -- /var/log/webassist /var/lib/webassist
    userdel webassist >/dev/null 2>&1 || true
    groupdel webassist >/dev/null 2>&1 || true
    echo "WebAssistant удалён вместе с журналами и данными."
else
    echo "WebAssistant удалён. /var/log/webassist и /var/lib/webassist сохранены."
fi
