#!/usr/bin/env bash
set -Eeuo pipefail
platform="${1:-}"
case "$platform" in p10|p11) ;; *) exit 2;; esac
evidence="${2:?Укажите каталог свидетельств}"
mkdir -p "$evidence"
evidence="$(cd "$evidence" && pwd)"
[[ -z "$(find "$evidence" -mindepth 1 -maxdepth 1 -print -quit)" ]] || { echo "Каталог свидетельств должен быть пустым." >&2; exit 2; }
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
proxy="$repo/tests/linux-systemd/cache/alt_snapshot_proxy.py"
python="$(command -v python3)"
snapshot="$(mktemp -d)"
work="$(mktemp -d)"
port=18765
container=""
proxy_job=""
proxy_pid=""
relay_job=""
relay_pid=""
cleanup() {
    set +e
    if [[ -n "$relay_pid" ]]; then sudo -n kill "$relay_pid" 2>/dev/null || true; fi
    if [[ -n "$relay_job" ]]; then kill "$relay_job" 2>/dev/null || true; wait "$relay_job" 2>/dev/null || true; fi
    if [[ -n "$proxy_pid" ]]; then sudo -n kill "$proxy_pid" 2>/dev/null || true; fi
    if [[ -n "$proxy_job" ]]; then kill "$proxy_job" 2>/dev/null || true; wait "$proxy_job" 2>/dev/null || true; fi
    if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
    rm -rf -- "$snapshot" "$work"
}
trap cleanup EXIT

image="registry.altlinux.org/$platform/alt:latest"
docker pull "$image"
image_id="$(docker image inspect -f '{{.Id}}' "$image")"
docker image inspect -f '{{json .RepoDigests}}' "$image" | tee "$evidence/base-image.json"
printf '%s\n' "$image_id" > "$evidence/base-image-id.txt"

wait_ready() {
    local job="$1" ready="$2"
    for attempt in {1..300}; do
        [[ ! -f "$ready" ]] || break
        kill -0 "$job"
        sleep 0.1
    done
    [[ -f "$ready" ]]
}

for mode in record replay; do
    start=$SECONDS
    container="$(docker run -d --network none \
        --volume "$repo/webassist/install/linux/runtime-dependencies.sh:/runtime-dependencies.sh:ro" \
        --volume "$evidence:/evidence" --entrypoint /bin/bash "$image_id" -c 'sleep infinity')"
    [[ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$container")" == none ]]
    target="$(docker inspect -f '{{.State.Pid}}' "$container")"
    [[ "$(sudo -n readlink "/proc/$target/ns/net")" != "$(readlink /proc/self/ns/net)" ]]
    [[ "$(sudo -n nsenter --target "$target" --net -- ip -o link show | wc -l)" == 1 ]]
    [[ -z "$(sudo -n nsenter --target "$target" --net -- ip route show)" ]]
    routes6="$(sudo -n nsenter --target "$target" --net -- ip -6 route show)"
    while IFS= read -r route; do
        [[ -z "$route" || "$route" == *" dev lo "* || "$route" == *" dev lo" ]]
    done <<<"$routes6"
    echo "ALT_CACHE_NETWORK_ISOLATION=PASS mode=$mode"
    touch "$work/$mode.probe"
    args=( "$python" "$proxy" --platform "$platform" --mode "$mode"
        --snapshot "$snapshot" --ready-file "$work/$mode.ready"
        --pid-file "$work/$mode.pid" --error-file "$work/$mode.errors"
        --probe-file "$work/$mode.probe" --probe-log "$evidence/$mode-probe.log" )
    if [[ "$mode" == record ]]; then
        "${args[@]}" --unix-socket "$work/record.sock" >"$evidence/$mode-proxy.log" 2>&1 &
        proxy_job=$!
        wait_ready "$proxy_job" "$work/$mode.ready"
        proxy_pid="$(cat "$work/$mode.pid")"
        sudo -n nsenter --target "$target" --net -- "$python" "$proxy" \
            --relay-socket "$work/record.sock" --port "$port" \
            --ready-file "$work/relay.ready" --pid-file "$work/relay.pid" \
            >"$evidence/record-relay.log" 2>&1 &
        relay_job=$!
        wait_ready "$relay_job" "$work/relay.ready"
        relay_pid="$(cat "$work/relay.pid")"
    else
        sudo -n nsenter --target "$target" --net -- "${args[@]}" --port "$port" >"$evidence/$mode-proxy.log" 2>&1 &
        proxy_job=$!
        wait_ready "$proxy_job" "$work/$mode.ready"
        proxy_pid="$(cat "$work/$mode.pid")"
    fi

    # Короткая реальная попытка APT: прокси отвечает 503, ничего не скачивает.
    # Успех допускается только при зарегистрированном запросе настоящего APT.
    timeout 45s docker exec -i -e ALT_PLATFORM="$platform" -e ALT_MODE="$mode" -e ALT_PORT="$port" \
        "$container" bash -s <<'PROBE'
set -Eeuo pipefail
export LC_ALL=C
for source in /etc/apt/sources.list /etc/apt/sources.list.d/*.list; do
    [[ -f "$source" ]] || continue
    sed -i "\|$ALT_PLATFORM/branch/x86_64-i586[[:space:]]|d" "$source"
done
export APT_CONFIG=/etc/apt/webassistant-snapshot.conf
unset no_proxy NO_PROXY HTTP_PROXY ALL_PROXY all_proxy
export http_proxy="http://127.0.0.1:$ALT_PORT/"
printf 'Acquire::http::Proxy "http://127.0.0.1:%s/";\n' "$ALT_PORT" > "$APT_CONFIG"
apt-config dump | tee "/evidence/$ALT_MODE-apt-config.txt"
grep -F "Acquire::http::Proxy \"http://127.0.0.1:$ALT_PORT/\";" "/evidence/$ALT_MODE-apt-config.txt"
apt-get clean
find /var/lib/apt/lists -maxdepth 1 -type f -delete
rc=0
apt-get -o Acquire::Retries=0 -o Acquire::http::Timeout=5 update || rc=$?
[[ "$rc" == 0 || "$rc" == 100 ]]
echo "ALT_CACHE_APT_PROBE_RETURN=$rc"
PROBE
    [[ -s "$evidence/$mode-probe.log" && ! -s "$work/$mode.errors" ]]
    cat "$evidence/$mode-probe.log"
    echo "ALT_CACHE_APT_PROXY_PROBE=PASS mode=$mode"
    rm -- "$work/$mode.probe"
    docker exec -i -e ALT_PLATFORM="$platform" -e ALT_MODE="$mode" -e ALT_PORT="$port" \
        "$container" bash -s <<'TRANSACTION'
set -Eeuo pipefail
export LC_ALL=C
list_rpms() {
    rpm -qa --qf '%{NAME}\t%{EPOCH}\t%{VERSION}\t%{RELEASE}\t%{ARCH}\n' | sort
}
list_rpms > "/evidence/$ALT_MODE-before.txt"
export APT_CONFIG=/etc/apt/webassistant-snapshot.conf
unset no_proxy NO_PROXY HTTP_PROXY ALL_PROXY all_proxy
export http_proxy="http://127.0.0.1:$ALT_PORT/"
apt-get clean
find /var/lib/apt/lists -maxdepth 1 -type f -delete
apt-get update
if [[ "$ALT_PLATFORM" == p10 ]]; then
    apt-get install -y shadow-utils
else
    apt-get install -y systemd curl iproute2 procps shadow-utils findutils
    apt-get clean
fi
list_rpms > "/evidence/$ALT_MODE-before-product.txt"
if rpm -q sane-airscan; then
    echo "Исходный контейнер уже содержит проверяемый RPM" >&2
    exit 1
fi
mkdir -p /etc/sane.d/dll.d /etc/sane.d/airscan.d
printf '%s\n' '# airscan' > /etc/sane.d/dll.d/airscan
source /runtime-dependencies.sh
ensure_webassistant_runtime_dependencies
[[ -z "$(webassistant_missing_capabilities)" ]]
rpm -q sane-airscan
list_rpms > "/evidence/$ALT_MODE-after.txt"
echo "ALT_CACHE_REAL_RPM_TRANSACTION=PASS mode=$ALT_MODE"
TRANSACTION
    [[ ! -s "$work/$mode.errors" ]]
    if [[ -n "$relay_pid" ]]; then
        sudo -n kill "$relay_pid"
        wait "$relay_job" || true
        relay_pid=""
        relay_job=""
    fi
    sudo -n kill "$proxy_pid"
    wait "$proxy_job" || true
    proxy_job=""
    proxy_pid=""
    docker rm -f "$container" >/dev/null
    container=""
    printf 'ALT_CACHE_TIMING mode=%s platform=%s seconds=%s\n' "$mode" "$platform" "$((SECONDS - start))" | tee -a "$evidence/timing.txt"
    if [[ "$mode" == record ]]; then
        cp "$snapshot/manifest.json" "$evidence/manifest.json"
        python3 - "$snapshot/manifest.json" <<'PY'
import json, sys
from urllib.parse import urlsplit
records = json.load(open(sys.argv[1]))["records"]
paths = [urlsplit(url).path for url, entry in records.items() if entry["status"] == 200]
assert any(path.endswith(".rpm") for path in paths), "Не записаны RPM"
assert any("pkglist" in path for path in paths), "Не записаны индексы"
print(f"ALT_CACHE_CAPTURED objects={len(records)} bytes={sum(e['size'] for e in records.values())}")
PY
    fi
done
for phase in before before-product after; do
    cmp "$evidence/record-$phase.txt" "$evidence/replay-$phase.txt"
done
echo "ALT_CACHE_OFFLINE_EQUIVALENCE=PASS platform=$platform"
