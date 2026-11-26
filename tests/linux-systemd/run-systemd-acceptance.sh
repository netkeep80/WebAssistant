#!/usr/bin/env bash
set -Eeuo pipefail
trap 'rc=$?; printf "Ошибка проверки ALT: строка %s, команда %s, код %s\n" "$LINENO" "$BASH_COMMAND" "$rc" >&2; exit "$rc"' ERR

PACKAGE_DIRECTORY="${1:-}"
ALT_IMAGE="${ALT_IMAGE:-registry.altlinux.org/p11/alt:latest}"
ALT_I586_REPOSITORY='p11/branch/x86_64-i586'
CONTAINER_NAME="webassistant-alt-p11-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
TEST_IMAGE="webassistant-alt-p11-systemd:${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
PORT=17654
LOG_DIR="/var/log/webassistant"
DATA_DIR="/var/lib/webassistant"

if [[ -z "$PACKAGE_DIRECTORY" ]]; then
    echo "Использование: $0 <каталог распакованного product package>" >&2
    exit 2
fi
PACKAGE_DIRECTORY="$(cd -- "$PACKAGE_DIRECTORY" && pwd)"

cleanup() {
    set +e
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    docker image rm -f "$TEST_IMAGE" >/dev/null 2>&1 || true
}
trap cleanup EXIT

test -x "$PACKAGE_DIRECTORY/app/WebAssistant"
test -f "$PACKAGE_DIRECTORY/app/appsettings.json"
test -f "$PACKAGE_DIRECTORY/VERSION"
test -x "$PACKAGE_DIRECTORY/install.sh"
test -x "$PACKAGE_DIRECTORY/uninstall.sh"
test -f "$PACKAGE_DIRECTORY/webassist.service"

# Отдельная пакетная проверка p10: это не приёмка физической ALT 10.1.
docker run --pull=always --rm -i --volume "$PACKAGE_DIRECTORY:/webassistant-package:ro" \
    registry.altlinux.org/p10/alt:latest bash -s <<'P10'
set -Eeuo pipefail
trap 'rc=$?; printf "Ошибка проверки p10: строка %s, команда %s, код %s\n" "$LINENO" "$BASH_COMMAND" "$rc" >&2; exit "$rc"' ERR
for source in /etc/apt/sources.list /etc/apt/sources.list.d/*.list; do
    [[ -f "$source" ]] || continue
    sed -i '\|p10/branch/x86_64-i586[[:space:]]|d' "$source"
done
apt-get update
apt-get install -y shadow-utils
if rpm -q sane-airscan; then
    echo "Для проверки первой установки p10 нужен контейнер без sane-airscan." >&2
    exit 1
fi
mkdir -p /etc/sane.d/dll.d /etc/sane.d/airscan.d
printf '%s\n' '"airscan"' > /etc/sane.d/dll.d/airscan
chmod 0600 /etc/sane.d/dll.d/airscan
registration_mode_before="$(stat -c '%a:%u:%g' /etc/sane.d/dll.d/airscan)"
printf '%s\n' '[devices]' '"Fixture" = http://192.0.2.1/eSCL, eSCL' '[options]' 'discovery = disable' > /etc/sane.d/airscan.conf
printf '%s\n' '# Дополнительные административные настройки' > /etc/sane.d/airscan.d/99-webassistant-test.conf
sha256sum /etc/sane.d/dll.d/airscan /etc/sane.d/airscan.conf /etc/sane.d/airscan.d/99-webassistant-test.conf > /tmp/sane-before.sha256
source /webassistant-package/runtime-dependencies.sh
echo "p10: исходное наличие средств проверки"
command -v runuser || echo "p10_initial_runuser=missing"
if webassistant_has_loader_probe; then
    echo "p10_initial_ctypes=present"
else
    echo "p10_initial_ctypes=missing"
fi
ensure_webassistant_runtime_dependencies
rpm -q sane-airscan
rpm -q --scripts sane-airscan
rpm -q --queryformat '[%{FILENAMES}\t%{FILEFLAGS:fflags}\n]' sane-airscan
echo "p10: создание учётной записи webassist"
useradd --system --no-create-home webassist
id webassist
echo "p10: определение пути библиотеки"
library="$(webassistant_airscan_library)"
printf 'p10: библиотека=%s\n' "$library"
ls -l -- "$library"
echo "p10: проверка динамических зависимостей"
ldd -r "$library"
echo "p10: проверка runuser"
command -v runuser
rpm -qf "$(command -v runuser)" || true
runuser -u webassist -- id
echo "p10: прямая проверка dlopen/dlsym от имени webassist"
webassistant_airscan_loadable "$library" webassist
echo "p10: проверка неизменности административных настроек"
sha256sum -c /tmp/sane-before.sha256
webassistant_require_no_airscan_rpmnew /etc/sane.d
[[ "$(stat -c '%a:%u:%g' /etc/sane.d/dll.d/airscan)" == "$registration_mode_before" ]]
if runuser -u webassist -- test -r /etc/sane.d/dll.d/airscan; then
    echo "Закрытая административная регистрация стала доступна службе." >&2
    exit 1
fi
# Контроль фактического поведения SANE, не только содержимого файлов.
native="$(runuser -u webassist -- env SANE_CONFIG_DIR=/etc/sane.d scanimage -f '%d%n')"
printf 'p10: штатные идентификаторы:\n%s\n' "$native"
if grep -q '^airscan:' <<<"$native"; then
    echo "AirScan фактически активирован вопреки исходной конфигурации p10." >&2
    exit 1
fi
archives=(/etc/sane.d/dll.d/.webassistant-airscan-rpmnew.*/airscan.rpmnew)
[[ ${#archives[@]} == 1 && -f "${archives[0]}" ]]
probe="$(mktemp -d)"
chmod 0755 "$probe"
mkdir -m 0755 "$probe/dll.d"
printf '%s\n' "# Нет зарегистрированных драйверов" > "$probe/dll.conf"
cp /etc/sane.d/airscan.conf "$probe/airscan.conf"
cp -p /etc/sane.d/dll.d/airscan "$probe/dll.d/airscan"
# Воспроизводим побочный эффект RPM только в отдельной копии.
install -m 0644 "${archives[0]}" "$probe/dll.d/airscan.rpmnew"
native="$(runuser -u webassist -- env SANE_CONFIG_DIR="$probe" SANE_DEBUG_DLL=5 scanimage -f '%d%n')"
[[ "$native" == 'airscan:e0:Fixture' ]] || { echo "Не воспроизведена регистрация из rpmnew: $native" >&2; exit 1; }
rm -- "$probe/dll.d/airscan.rpmnew"
empty_native="$(runuser -u webassist -- env SANE_CONFIG_DIR="$probe" scanimage -f '%d%n')"
[[ -z "$empty_native" ]]
# Та же строка в кавычках должна стать действующей, когда доступна службе.
chmod 0644 "$probe/dll.d/airscan"
quoted_native="$(runuser -u webassist -- env SANE_CONFIG_DIR="$probe" scanimage -f '%d%n')"
[[ "$quoted_native" == 'airscan:e0:Fixture' ]] || { echo "Не подтверждена регистрация в кавычках: $quoted_native" >&2; exit 1; }
rm -rf -- "$probe"
echo "alt_p10_rpmnew_semantics=PASS"
mkdir -p /tmp/webassistant-fail-apt
printf '%s\n' '#!/bin/sh' 'exit 97' > /tmp/webassistant-fail-apt/apt-get
chmod +x /tmp/webassistant-fail-apt/apt-get
PATH="/tmp/webassistant-fail-apt:$PATH" ensure_webassistant_runtime_dependencies
sha256sum -c /tmp/sane-before.sha256
echo "alt_p10_airscan_package_acceptance=PASS"

# Повторно используем тот же реальный пакет и его точные байты регистрации.
# Контейнер одноразовый; исходный сценарий сохранности уже завершён.
package_version="$(rpm -q sane-airscan)"
cp -- "${archives[0]}" /tmp/airscan-package-original
rpm -e sane-airscan
if rpm -q sane-airscan; then
    echo "Пакет sane-airscan не удалён перед испытанием." >&2
    exit 1
fi
install -m 0600 /tmp/airscan-package-original /etc/sane.d/dll.d/airscan
before="$(webassistant_airscan_registration_snapshot /etc/sane.d)"
pending=/etc/sane.d.webassistant-airscan-pending
if ensure_webassistant_runtime_dependencies; then
    [[ "$(webassistant_airscan_registration_snapshot /etc/sane.d)" == "$before" ]]
    [[ ! -e "$pending" ]]
    echo "p10: RPM сохранил права при совпадающем содержимом"
else
    [[ "$(rpm -q sane-airscan)" == "$package_version" ]]
    [[ "$(webassistant_airscan_registration_snapshot /etc/sane.d)" != "$before" ]]
    [[ -f "$pending/registration-before" ]]
    [[ "$(cat "$pending/registration-before")" == "$before" ]]
    if PATH="/tmp/webassistant-fail-apt:$PATH" ensure_webassistant_runtime_dependencies; then
        echo "Повтор после изменения регистрации ошибочно успешен." >&2
        exit 1
    fi
    # Явное разрешение конфликта администратором в испытательном контейнере.
    install -m 0600 /tmp/airscan-package-original /etc/sane.d/dll.d/airscan
    [[ "$(webassistant_airscan_registration_snapshot /etc/sane.d)" == "$before" ]]
    webassistant_require_no_airscan_rpmnew /etc/sane.d
    rm -- "$pending/registration-before"
    rmdir -- "$pending"
    echo "p10: изменение прав обнаружено, повтор заблокирован, конфликт разрешён"
fi
[[ "$(rpm -q sane-airscan)" == "$package_version" ]]
PATH="/tmp/webassistant-fail-apt:$PATH" ensure_webassistant_runtime_dependencies
echo "alt_p10_identical_content_permissions=PASS"

# Чистая регистрация: нет ни пакета, ни исходного файла регистрации.
rpm -e sane-airscan
if rpm -q sane-airscan; then
    echo "Пакет sane-airscan не удалён перед испытанием." >&2
    exit 1
fi
rm -f -- /etc/sane.d/dll.d/airscan
webassistant_require_no_airscan_rpmnew /etc/sane.d
ensure_webassistant_runtime_dependencies
[[ "$(rpm -q sane-airscan)" == "$package_version" ]]
cmp -- /tmp/airscan-package-original /etc/sane.d/dll.d/airscan
runuser -u webassist -- test -r /etc/sane.d/dll.d/airscan
webassistant_airscan_loadable "$(webassistant_airscan_library)" webassist
PATH="/tmp/webassistant-fail-apt:$PATH" ensure_webassistant_runtime_dependencies
[[ ! -e "$pending" ]]
echo "alt_p10_clean_registration=PASS"
P10

build_context="$(mktemp -d)"
trap 'rm -rf -- "$build_context"; cleanup' EXIT
cat >"$build_context/Dockerfile" <<EOF
FROM ${ALT_IMAGE}
RUN set -eu; \
    for source in /etc/apt/sources.list /etc/apt/sources.list.d/*.list; do \
        [ -f "\$source" ] || continue; \
        sed -i "\|${ALT_I586_REPOSITORY}[[:space:]]|d" "\$source"; \
    done; \
    apt-get update \
    && apt-get install -y systemd curl iproute2 procps shadow-utils findutils \
    && apt-get clean
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
EOF

docker build --pull --tag "$TEST_IMAGE" "$build_context"
rm -rf -- "$build_context"

docker run --detach \
    --name "$CONTAINER_NAME" \
    --privileged \
    --cgroupns=host \
    --volume /sys/fs/cgroup:/sys/fs/cgroup:rw \
    "$TEST_IMAGE"

for attempt in {1..30}; do
    if docker exec "$CONTAINER_NAME" systemctl show --property=Version >/dev/null 2>&1; then break; fi
    [[ "$attempt" -lt 30 ]] || { docker logs "$CONTAINER_NAME" >&2 || true; exit 1; }
    sleep 1
done

docker exec "$CONTAINER_NAME" mkdir -p /webassistant-package
docker cp "$PACKAGE_DIRECTORY/." "$CONTAINER_NAME:/webassistant-package/"

# Проверяем первую установку RPM поверх заранее заданной административной конфигурации.
if docker exec "$CONTAINER_NAME" rpm -q sane-airscan; then
    echo "Для проверки первой установки нужен контейнер без sane-airscan." >&2
    exit 1
fi
docker exec -i "$CONTAINER_NAME" bash -s <<'SEED'
set -euo pipefail
mkdir -p /etc/sane.d/dll.d /etc/sane.d/airscan.d
printf '%s\n' '# airscan' > /etc/sane.d/dll.d/airscan
printf '%s\n' '[devices]' '"Fixture" = http://192.0.2.1/eSCL, eSCL' '[options]' 'discovery = disable' > /etc/sane.d/airscan.conf
printf '%s\n' '# Дополнительные административные настройки' > /etc/sane.d/airscan.d/99-webassistant-test.conf
SEED

# Свидетельство хранится у управляющего процесса, вне перезапускаемого контейнера.
sane_before="$(docker exec "$CONTAINER_NAME" sha256sum /etc/sane.d/dll.d/airscan /etc/sane.d/airscan.conf /etc/sane.d/airscan.d/99-webassistant-test.conf)"
[[ "$(wc -l <<<"$sane_before")" == 3 ]]
assert_sane_configuration_preserved() {
    printf '%s\n' "$sane_before" | docker exec -i "$CONTAINER_NAME" sha256sum -c -
    docker exec "$CONTAINER_NAME" bash -c '
        source /webassistant-package/runtime-dependencies.sh
        webassistant_require_no_airscan_rpmnew /etc/sane.d
        archives=(/etc/sane.d/dll.d/.webassistant-airscan-rpmnew.*/airscan.rpmnew)
        [[ ${#archives[@]} == 1 && -f "${archives[0]}" ]]
    '
}

if ! docker exec "$CONTAINER_NAME" /webassistant-package/install.sh; then
    docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
    docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 200 --no-pager >&2 || true
    exit 1
fi

docker exec "$CONTAINER_NAME" rpm -q libicu74
docker exec "$CONTAINER_NAME" rpm -q libgtk+3
docker exec "$CONTAINER_NAME" rpm -q libsane
docker exec "$CONTAINER_NAME" rpm -q sane
docker exec "$CONTAINER_NAME" rpm -q sane-airscan
docker exec "$CONTAINER_NAME" sh -c 'command -v airscan-discover >/dev/null'
echo "p11: метаданные RPM sane-airscan и сохранность настроек"
docker exec "$CONTAINER_NAME" rpm -q --scripts sane-airscan
docker exec "$CONTAINER_NAME" rpm -q --queryformat '[%{FILENAMES}\t%{FILEFLAGS:fflags}\n]' sane-airscan
assert_sane_configuration_preserved
docker exec "$CONTAINER_NAME" bash -c '
    source /webassistant-package/runtime-dependencies.sh
    library="$(webassistant_airscan_library)"
    webassistant_airscan_linkable "$library"
    webassistant_airscan_loadable "$library" webassist
'


# Регрессия #135: когда обязательные RPM уже установлены, неисправная поверхность
# apt-rpm не должна блокировать повторную установку. Подменный apt-get падает при вызове.
docker exec "$CONTAINER_NAME" sh -lc \
    'mkdir -p /tmp/webassistant-fail-apt && printf "%s\n" "#!/bin/sh" "echo apt-get-called >&2" "exit 97" > /tmp/webassistant-fail-apt/apt-get && chmod +x /tmp/webassistant-fail-apt/apt-get'
if ! docker exec \
    --env PATH="/tmp/webassistant-fail-apt:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" \
    "$CONTAINER_NAME" /webassistant-package/install.sh; then
    echo "Повторная установка при уже установленных runtime-зависимостях ошибочно зависит от apt-rpm." >&2
    docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
    exit 1
fi
docker exec "$CONTAINER_NAME" rm -rf /tmp/webassistant-fail-apt
assert_sane_configuration_preserved

assert_service_enabled_active() {
    if ! docker exec "$CONTAINER_NAME" systemctl is-enabled --quiet webassist.service; then
        echo "WebAssistant service не enabled после installer lifecycle step." >&2
        docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
        return 1
    fi
    if ! docker exec "$CONTAINER_NAME" systemctl is-active --quiet webassist.service; then
        echo "WebAssistant service не active после installer lifecycle step." >&2
        docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
        docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 100 --no-pager >&2 || true
        return 1
    fi
}

assert_service_enabled_active

service_user="$(docker exec "$CONTAINER_NAME" systemctl show webassist.service --property=User --value)"
[[ "$service_user" == "webassist" ]] || { echo "Неожиданный service user: $service_user" >&2; exit 1; }

wait_for_health() {
    for attempt in {1..30}; do
        if docker exec "$CONTAINER_NAME" curl --fail --silent --show-error \
            "http://127.0.0.1:${PORT}/v1/health" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    echo "WebAssistant health endpoint не стал доступен на 127.0.0.1:${PORT}." >&2
    docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
    docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 100 --no-pager >&2 || true
    return 1
}

assert_daily_log() {
    if ! docker exec "$CONTAINER_NAME" test -d "$LOG_DIR"; then
        echo "Не создан log directory $LOG_DIR." >&2
        return 1
    fi
    if ! docker exec "$CONTAINER_NAME" sh -lc \
        "find '$LOG_DIR' -maxdepth 0 -user webassist -group webassist -print -quit | grep -q ."; then
        echo "Log directory $LOG_DIR имеет неверного owner/group." >&2
        docker exec "$CONTAINER_NAME" ls -ld "$LOG_DIR" >&2 || true
        return 1
    fi
    for attempt in {1..30}; do
        if docker exec "$CONTAINER_NAME" sh -lc \
            "find '$LOG_DIR' -maxdepth 1 -type f -name 'webassistant-*.log' -print -quit | grep -q ."; then return 0; fi
        sleep 0.1
    done
    echo "После успешного health request не появился webassistant-*.log в $LOG_DIR." >&2
    docker exec "$CONTAINER_NAME" ls -la "$LOG_DIR" >&2 || true
    docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 100 --no-pager >&2 || true
    return 1
}

assert_loopback_only() {
    listeners="$(docker exec "$CONTAINER_NAME" sh -lc "ss -H -ltn 'sport = :${PORT}' || true")"
    if ! grep -Eq "127\\.0\\.0\\.1:${PORT}([[:space:]]|$)" <<<"$listeners"; then
        echo "Не найден loopback listener WebAssistant на 127.0.0.1:${PORT}: $listeners" >&2
        return 1
    fi
    if grep -Eq "(0\\.0\\.0\\.0|\\[::\\]|\\*:):?${PORT}([[:space:]]|$)" <<<"$listeners"; then
        echo "WebAssistant слушает не только loopback на порту ${PORT}: $listeners" >&2
        return 1
    fi
}

assert_no_listener() {
    for attempt in {1..30}; do
        if ! docker exec "$CONTAINER_NAME" sh -lc "ss -H -ltn 'sport = :${PORT}'" | grep -q .; then return 0; fi
        sleep 0.25
    done
    echo "Listener WebAssistant на порту ${PORT} остался после остановки: $(docker exec "$CONTAINER_NAME" sh -lc "ss -H -ltn 'sport = :${PORT}' || true")" >&2
    return 1
}

wait_for_health
assert_daily_log
assert_loopback_only

# Штатный список отражает зарегистрированные конечные точки, а не наличие физического МФУ.
assert_scanner_listing() {
    local phase="$1" status
    status="$(docker exec "$CONTAINER_NAME" curl --silent --show-error --output /tmp/webassistant-scanners.json --write-out '%{http_code}' "http://127.0.0.1:${PORT}/v1/scanners")"
    printf 'Перечисление сканеров (%s): HTTP %s\n' "$phase" "$status"
    docker exec "$CONTAINER_NAME" cat /tmp/webassistant-scanners.json
    [[ "$status" == 200 ]] || { echo "Ожидался HTTP 200 при перечислении ($phase)." >&2; return 1; }
    docker exec "$CONTAINER_NAME" cat /tmp/webassistant-scanners.json | python3 -c '
import json, re, sys
phase = sys.argv[1]
def require(condition, message):
    if not condition:
        sys.exit(phase + ": " + message)
data = json.load(sys.stdin)
require(isinstance(data, dict) and set(data) == {"scanners", "warnings"}, "Некорректная структура ответа")
scanners, warnings = data["scanners"], data["warnings"]
require(isinstance(scanners, list) and isinstance(warnings, list), "Ожидались два массива")
ids = set()
for item in scanners:
    require(isinstance(item, dict) and set(item) == {"scannerId", "name", "backend"}, "Нарушен контракт идентичности")
    identifier = item["scannerId"]
    require(isinstance(identifier, str) and re.fullmatch(r"wa2-sane-[A-Za-z0-9_-]{16}", identifier) is not None, "Некорректный scannerId SANE")
    require(item["backend"] == "sane" and isinstance(item["name"], str) and bool(item["name"].strip()), "Некорректное имя или подсистема")
    require(identifier not in ids, "Повторяющийся scannerId")
    ids.add(identifier)
for warning in warnings:
    require(isinstance(warning, dict) and set(warning) == {"backend", "code"}, "Некорректное предупреждение")
    require(warning["backend"] == "sane" and isinstance(warning["code"], str) and bool(warning["code"].strip()), "Некорректный код предупреждения")
fixture_id = "wa2-sane-kB7oUFx_zHPkiRHt"
if phase in ("default", "restored"):
    require(fixture_id not in ids, "Отключённый тестовый AirScan не должен обнаруживаться")
elif phase == "isolated-positive":
    require(ids == {fixture_id} and warnings == [], "Ожидалась только явно зарегистрированная тестовая конечная точка")
elif phase == "isolated-empty":
    require(scanners == [] and warnings == [], "Изолированное перечисление должно вернуть пустые массивы")
print("scanner_listing_" + phase + "=PASS count=" + str(len(scanners)))
' "$phase"
}

echo "p11: штатное перечисление SANE от имени службы (диагностика)"
native_status=0
docker exec "$CONTAINER_NAME" runuser -u webassist -- scanimage -L || native_status=$?
printf 'p11: код завершения scanimage -L = %s\n' "$native_status"
assert_scanner_listing default
# Подтверждаем, что штатное перечисление не активировало отключённый AirScan.
native_ids="$(docker exec "$CONTAINER_NAME" runuser -u webassist -- env SANE_CONFIG_DIR=/etc/sane.d scanimage -f '%d%n')"
if grep -q '^airscan:' <<<"$native_ids"; then
    echo "AirScan фактически активирован вопреки исходной конфигурации p11." >&2
    exit 1
fi

# Воспроизводим регистрацию из rpmnew в отдельной конфигурации процесса SANE,
# затем проверяем пустой список после удаления копии файла.
# Административные файлы /etc/sane.d не изменяются.
# Меняется только окружение тестового процесса через временный модуль systemd;
# исходные файлы /etc/sane.d не редактируются.
docker exec "$CONTAINER_NAME" bash -c '
    set -eu
    mkdir -p /run/webassistant-sane-empty/dll.d /run/systemd/system/webassist.service.d
    chmod 0755 /run/webassistant-sane-empty /run/webassistant-sane-empty/dll.d
    printf "%s\n" "# Нет зарегистрированных драйверов" > /run/webassistant-sane-empty/dll.conf
    cp /etc/sane.d/airscan.conf /run/webassistant-sane-empty/airscan.conf
    cp /etc/sane.d/dll.d/airscan /run/webassistant-sane-empty/dll.d/airscan
    archives=(/etc/sane.d/dll.d/.webassistant-airscan-rpmnew.*/airscan.rpmnew)
    [[ ${#archives[@]} == 1 && -f "${archives[0]}" ]]
    install -m 0644 "${archives[0]}" /run/webassistant-sane-empty/dll.d/airscan.rpmnew
    printf "%s\n" "[Service]" "Environment=SANE_CONFIG_DIR=/run/webassistant-sane-empty" > /run/systemd/system/webassist.service.d/99-test-empty-sane.conf
'
positive_native="$(docker exec "$CONTAINER_NAME" runuser -u webassist -- env SANE_CONFIG_DIR=/run/webassistant-sane-empty SANE_DEBUG_DLL=5 scanimage -f '%d%n')"
[[ "$positive_native" == 'airscan:e0:Fixture' ]] || { echo "Не воспроизведена регистрация из rpmnew: $positive_native" >&2; exit 1; }
docker exec "$CONTAINER_NAME" systemctl daemon-reload
docker exec "$CONTAINER_NAME" systemctl restart webassist.service
wait_for_health
assert_scanner_listing isolated-positive
docker exec "$CONTAINER_NAME" rm -- /run/webassistant-sane-empty/dll.d/airscan.rpmnew
empty_native="$(docker exec "$CONTAINER_NAME" runuser -u webassist -- env SANE_CONFIG_DIR=/run/webassistant-sane-empty scanimage -f '%d%n')"
printf 'p11: изолированное перечисление SANE: %s\n' "$empty_native"
[[ -z "$empty_native" ]] || { echo "Изолированная конфигурация SANE не пуста." >&2; exit 1; }
docker exec "$CONTAINER_NAME" systemctl daemon-reload
docker exec "$CONTAINER_NAME" systemctl restart webassist.service
wait_for_health
assert_scanner_listing isolated-empty

# Восстанавливаем штатное окружение до продолжения жизненного цикла.
docker exec "$CONTAINER_NAME" rm -f /run/systemd/system/webassist.service.d/99-test-empty-sane.conf
docker exec "$CONTAINER_NAME" rm -rf /run/webassistant-sane-empty
docker exec "$CONTAINER_NAME" systemctl daemon-reload
docker exec "$CONTAINER_NAME" systemctl restart webassist.service
wait_for_health
assert_scanner_listing restored
assert_sane_configuration_preserved

scan_status="$(docker exec "$CONTAINER_NAME" sh -lc \
    "curl --silent --show-error --output /tmp/webassistant-scan-response --write-out '%{http_code}' --request POST http://127.0.0.1:${PORT}/v1/scan")"
[[ "$scan_status" != "200" ]] || { echo "Запрос сканирования без обязательных параметров ошибочно успешен." >&2; exit 1; }
if head -c 5 < <(docker exec "$CONTAINER_NAME" cat /tmp/webassistant-scan-response) | grep -q '^%PDF-'; then
    echo "Failure response ошибочно содержит PDF." >&2
    exit 1
fi
docker exec "$CONTAINER_NAME" rm -f /tmp/webassistant-scan-response

docker exec "$CONTAINER_NAME" systemctl stop webassist.service
assert_no_listener

docker exec "$CONTAINER_NAME" systemctl start webassist.service
wait_for_health
assert_daily_log

docker exec "$CONTAINER_NAME" systemctl restart webassist.service
wait_for_health
assert_loopback_only

# Свидетельство прерванной операции должно пережить перезапуск контейнера.
docker exec "$CONTAINER_NAME" bash -c '
    source /webassistant-package/runtime-dependencies.sh
    before="$(webassistant_airscan_registration_snapshot /etc/sane.d)" || exit 1
    webassistant_begin_airscan_transaction /etc/sane.d.webassistant-airscan-pending "$before"
'
docker restart "$CONTAINER_NAME" >/dev/null
for attempt in {1..30}; do
    if docker exec "$CONTAINER_NAME" systemctl show --property=Version >/dev/null 2>&1; then break; fi
    sleep 1
done
wait_for_health
assert_service_enabled_active
assert_loopback_only
assert_sane_configuration_preserved
assert_scanner_listing restored
docker exec "$CONTAINER_NAME" bash -c '
    set -eu
    source /webassistant-package/runtime-dependencies.sh
    pending=/etc/sane.d.webassistant-airscan-pending
    [[ -f "$pending/registration-before" ]]
    [[ "$(cat "$pending/registration-before")" == "$(webassistant_airscan_registration_snapshot /etc/sane.d)" ]]
    if ensure_webassistant_runtime_dependencies; then
        echo "Повтор после перезапуска с незавершённым свидетельством ошибочно успешен." >&2
        exit 1
    fi
    # Явное разрешение в контролируемом испытании: состояние точно совпало.
    webassistant_require_no_airscan_rpmnew /etc/sane.d
    rm -- "$pending/registration-before"
    rmdir -- "$pending"
'
echo "alt_p11_pending_reboot=PASS"

docker exec "$CONTAINER_NAME" /webassistant-package/uninstall.sh

if docker exec "$CONTAINER_NAME" systemctl cat webassist.service >/dev/null 2>&1; then
    echo "После удаления остался модуль службы." >&2
    exit 1
fi
docker exec "$CONTAINER_NAME" test ! -e /opt/webassist
docker exec "$CONTAINER_NAME" test ! -e /etc/systemd/system/webassist.service
docker exec "$CONTAINER_NAME" test -d "$LOG_DIR"
docker exec "$CONTAINER_NAME" test -d "$DATA_DIR"
if docker exec "$CONTAINER_NAME" getent passwd webassist >/dev/null || docker exec "$CONTAINER_NAME" getent group webassist >/dev/null; then
    echo "После удаления осталась учётная запись или группа службы." >&2
    exit 1
fi
assert_no_listener
assert_sane_configuration_preserved

echo "alt_p11_systemd_acceptance=PASS"
