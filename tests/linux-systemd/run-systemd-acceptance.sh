#!/usr/bin/env bash
set -euo pipefail

PACKAGE_DIRECTORY="${1:-}"
PRODUCT_ROOT="${2:-}"
ALT_IMAGE="${ALT_IMAGE:-registry.altlinux.org/p11/alt:latest}"
CONTAINER_NAME="webassistant-alt-p11-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
TEST_IMAGE="webassistant-alt-p11-systemd:${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
PORT=17654
LOG_DIR="/var/log/webassist"
DATA_DIR="/var/lib/webassist"

if [[ -z "$PACKAGE_DIRECTORY" ]]; then
    echo "Использование: $0 <каталог product package> [product root]" >&2
    exit 2
fi

if [[ -z "$PRODUCT_ROOT" ]]; then
    repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
    PRODUCT_ROOT="$repository_root/webassist"
fi
PRODUCT_ROOT="$(cd -- "$PRODUCT_ROOT" && pwd)"
package_script="$PRODUCT_ROOT/build/linux/package.sh"

cleanup() {
    set +e
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    docker image rm -f "$TEST_IMAGE" >/dev/null 2>&1 || true
}
trap cleanup EXIT

original_pwd="$PWD"
cd "${RUNNER_TEMP:-/tmp}"
"$package_script" "$PACKAGE_DIRECTORY"
cd "$original_pwd"

test -x "$PACKAGE_DIRECTORY/app/WebAssistant"
test -x "$PACKAGE_DIRECTORY/install.sh"
test -x "$PACKAGE_DIRECTORY/uninstall.sh"
test -f "$PACKAGE_DIRECTORY/webassist.service"

build_context="$(mktemp -d)"
trap 'rm -rf -- "$build_context"; cleanup' EXIT
cat >"$build_context/Dockerfile" <<EOF
FROM ${ALT_IMAGE}
RUN apt-get update \
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

if ! docker exec "$CONTAINER_NAME" /webassistant-package/install.sh; then
    docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
    docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 200 --no-pager >&2 || true
    exit 1
fi

docker exec "$CONTAINER_NAME" rpm -q libicu74
docker exec "$CONTAINER_NAME" rpm -q libsane
docker exec "$CONTAINER_NAME" systemctl is-enabled --quiet webassist.service
docker exec "$CONTAINER_NAME" systemctl is-active --quiet webassist.service

service_user="$(docker exec "$CONTAINER_NAME" systemctl show webassist.service --property=User --value)"
[[ "$service_user" == "webassist" ]] || { echo "Неожиданный service user: $service_user" >&2; exit 1; }

wait_for_health() {
    for attempt in {1..30}; do
        if docker exec "$CONTAINER_NAME" curl --fail --silent --show-error \
            "http://127.0.0.1:${PORT}/v1/health" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    docker exec "$CONTAINER_NAME" systemctl status webassist.service --no-pager >&2 || true
    docker exec "$CONTAINER_NAME" journalctl -u webassist.service -n 100 --no-pager >&2 || true
    return 1
}

assert_daily_log() {
    docker exec "$CONTAINER_NAME" test -d "$LOG_DIR"
    docker exec "$CONTAINER_NAME" sh -lc \
        "find '$LOG_DIR' -maxdepth 0 -user webassist -group webassist -print -quit | grep -q ."
    for attempt in {1..30}; do
        if docker exec "$CONTAINER_NAME" sh -lc \
            "find '$LOG_DIR' -maxdepth 1 -type f -name 'webassistant-*.log' -print -quit | grep -q ."; then return 0; fi
        sleep 0.1
    done
    return 1
}

assert_loopback_only() {
    listeners="$(docker exec "$CONTAINER_NAME" sh -lc "ss -H -ltn 'sport = :${PORT}' || true")"
    grep -Eq "127\\.0\\.0\\.1:${PORT}([[:space:]]|$)" <<<"$listeners"
    ! grep -Eq "(0\\.0\\.0\\.0|\\[::\\]|\\*:):?${PORT}([[:space:]]|$)" <<<"$listeners"
}

assert_no_listener() {
    for attempt in {1..30}; do
        if ! docker exec "$CONTAINER_NAME" sh -lc "ss -H -ltn 'sport = :${PORT}'" | grep -q .; then return 0; fi
        sleep 0.25
    done
    return 1
}

wait_for_health
assert_daily_log
assert_loopback_only

scan_status="$(docker exec "$CONTAINER_NAME" sh -lc \
    "curl --silent --show-error --output /tmp/webassistant-scan-response --write-out '%{http_code}' --request POST http://127.0.0.1:${PORT}/v1/scan")"
[[ "$scan_status" != "200" ]] || { echo "Без scanner backend получен успешный scan." >&2; exit 1; }
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

docker restart "$CONTAINER_NAME" >/dev/null
for attempt in {1..30}; do
    if docker exec "$CONTAINER_NAME" systemctl show --property=Version >/dev/null 2>&1; then break; fi
    sleep 1
done
wait_for_health
docker exec "$CONTAINER_NAME" systemctl is-enabled --quiet webassist.service
docker exec "$CONTAINER_NAME" systemctl is-active --quiet webassist.service
assert_loopback_only

docker exec "$CONTAINER_NAME" /webassistant-package/uninstall.sh

! docker exec "$CONTAINER_NAME" systemctl cat webassist.service >/dev/null 2>&1
docker exec "$CONTAINER_NAME" test ! -e /opt/webassist
docker exec "$CONTAINER_NAME" test ! -e /etc/systemd/system/webassist.service
docker exec "$CONTAINER_NAME" test -d "$LOG_DIR"
docker exec "$CONTAINER_NAME" test -d "$DATA_DIR"
! docker exec "$CONTAINER_NAME" getent passwd webassist >/dev/null
! docker exec "$CONTAINER_NAME" getent group webassist >/dev/null
assert_no_listener

echo "alt_p11_systemd_acceptance=PASS"
