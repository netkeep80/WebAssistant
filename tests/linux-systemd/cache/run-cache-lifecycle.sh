#!/usr/bin/env bash
set -Eeuo pipefail
platform="${1:-}"
case "$platform" in p10|p11) ;; *) exit 2;; esac
artifact="$(realpath "${2:?Укажите ZIP}")"
evidence="${3:?Укажите каталог свидетельств}"
mkdir -p "$evidence"
evidence="$(realpath "$evidence")"
[[ -z "$(find "$evidence" -mindepth 1 -maxdepth 1 -print -quit)" ]]
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo"
proxy="$repo/tests/linux-systemd/cache/alt_snapshot_proxy.py"
python="$(command -v python3)"
work="$(mktemp -d)"
snapshot="$work/snapshot"
replay="$work/replay"
mkdir "$snapshot" "$replay"
config="$work/apt.conf"
printf '%s\n' 'Acquire::http::Proxy "http://127.0.0.1:18765/";' > "$config"
sidecar=""
pack=""
probe_container=""
proxy_pid=""
proxy_job=""
relay_pid=""
relay_job=""
tag="webassistant-alt-cache-local:$platform-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
stop_processes() {
    if [[ -n "$relay_pid" ]]; then sudo -n kill "$relay_pid"; wait "$relay_job" || true; fi
    if [[ -n "$proxy_pid" ]]; then sudo -n kill "$proxy_pid"; wait "$proxy_job" || true; fi
    relay_pid="" relay_job="" proxy_pid="" proxy_job=""
}
cleanup() {
    set +e
    for pid in "$relay_pid" "$proxy_pid"; do [[ -z "$pid" ]] || sudo -n kill "$pid" 2>/dev/null; done
    for job in "$relay_job" "$proxy_job"; do [[ -z "$job" ]] || kill "$job" 2>/dev/null; done
    for id in "$probe_container" "$pack" "$sidecar"; do [[ -z "$id" ]] || docker rm -f "$id" >/dev/null 2>&1; done
    docker image rm "$tag" >/dev/null 2>&1
    rm -rf "$work"
}
trap cleanup EXIT
wait_ready() {
    for attempt in {1..300}; do
        [[ ! -f "$2" ]] || return 0
        kill -0 "$1"
        sleep 0.1
    done
    return 1
}
base_ref="registry.altlinux.org/$platform/alt:latest"
docker pull "$base_ref"
base_id="$(docker image inspect -f '{{.Id}}' "$base_ref")"
docker image inspect -f '{{json .RepoDigests}}' "$base_ref" > "$evidence/base-repo-digests.json"
docker image inspect -f '{{.Architecture}}' "$base_id" | grep -x amd64
printf '%s\n' "$base_id" > "$evidence/base-image-id.txt"
source_sha="$(git rev-parse HEAD)"
python3 - "$artifact.provenance.json" "$source_sha" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8-sig") as f:
    assert json.load(f)["sourceSha"] == sys.argv[2]
PY
rpm_inventory() {
    docker run --rm --pull=never --network none --entrypoint rpm "$1" \
        -qa --qf '%{NAME}\t%{EPOCH}\t%{VERSION}\t%{RELEASE}\t%{ARCH}\n' | LC_ALL=C sort
}
rpm_inventory "$base_id" > "$evidence/base-rpms.txt"
if docker run --rm --pull=never --network none --entrypoint rpm "$base_id" -q sane-airscan; then
    echo "Базовый образ уже содержит sane-airscan" >&2
    exit 1
fi

# Один netns на фазу: отдельный sidecar переживает перезапуск systemd-контейнера.
for mode in record replay; do
    data="$snapshot"
    image="$base_id"
    if [[ "$mode" == replay ]]; then data="$replay"; image="$cache_id"; fi
    sidecar="$(docker run -d --pull=never --network none --entrypoint /bin/bash "$base_id" -c 'sleep infinity')"
    [[ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$sidecar")" == none ]]
    target="$(docker inspect -f '{{.State.Pid}}' "$sidecar")"
    [[ "$(sudo -n readlink "/proc/$target/ns/net")" != "$(readlink /proc/self/ns/net)" ]]
    [[ "$(sudo -n nsenter --target "$target" --net -- ip -o link show | wc -l)" == 1 ]]
    [[ -z "$(sudo -n nsenter --target "$target" --net -- ip route show)" ]]
    routes6="$(sudo -n nsenter --target "$target" --net -- ip -6 route show)"
    while IFS= read -r route; do
        [[ -z "$route" || "$route" == *" dev lo "* || "$route" == *" dev lo" ]]
    done <<<"$routes6"
    echo "ALT_CACHE_LIFECYCLE_ISOLATION=PASS mode=$mode"
    touch "$work/$mode.probe"
    args=("$python" "$proxy" --platform "$platform" --mode "$mode"
        --snapshot "$data" --ready-file "$work/$mode.ready" --pid-file "$work/$mode.pid"
        --error-file "$work/$mode.errors" --probe-file "$work/$mode.probe"
        --probe-log "$evidence/$mode-probe.log")
    if [[ "$mode" == record ]]; then
        "${args[@]}" --unix-socket "$work/record.sock" >"$evidence/$mode-proxy.log" 2>&1 &
        proxy_job=$!
        wait_ready "$proxy_job" "$work/$mode.ready"
        proxy_pid="$(cat "$work/$mode.pid")"
        sudo -n nsenter --target "$target" --net -- "$python" "$proxy" \
            --relay-socket "$work/record.sock" --port 18765 \
            --ready-file "$work/relay.ready" --pid-file "$work/relay.pid" \
            >"$evidence/record-relay.log" 2>&1 &
        relay_job=$!
        wait_ready "$relay_job" "$work/relay.ready"
        relay_pid="$(cat "$work/relay.pid")"
    else
        sudo -n nsenter --target "$target" --net -- "${args[@]}" --port 18765 >"$evidence/$mode-proxy.log" 2>&1 &
        proxy_job=$!
        wait_ready "$proxy_job" "$work/$mode.ready"
        proxy_pid="$(cat "$work/$mode.pid")"
    fi

    probe_container="$(docker create --pull=never --network "container:$sidecar" \
        -e APT_CONFIG=/etc/apt/webassistant-snapshot.conf -e http_proxy=http://127.0.0.1:18765/ \
        -e ALT_PLATFORM="$platform" --volume "$config:/etc/apt/webassistant-snapshot.conf:ro" \
        --entrypoint /bin/bash "$image" -c '
        set -eu
        for source in /etc/apt/sources.list /etc/apt/sources.list.d/*.list; do
            [ -f "$source" ] || continue
            sed -i "\|$ALT_PLATFORM/branch/x86_64-i586[[:space:]]|d" "$source"
        done
        apt-config dump
        apt-get clean
        find /var/lib/apt/lists -maxdepth 1 -type f -delete
        rc=0
        apt-get -o Acquire::Retries=0 -o Acquire::http::Timeout=5 update || rc=$?
        [ "$rc" = 0 ] || [ "$rc" = 100 ]
    ')"
    timeout 45s docker start -a "$probe_container" | tee "$evidence/$mode-apt-config.log"
    [[ "$(docker inspect -f '{{.State.ExitCode}}' "$probe_container")" == 0 ]]
    docker rm "$probe_container" >/dev/null
    probe_container=""
    grep -F 'Acquire::http::Proxy "http://127.0.0.1:18765/";' "$evidence/$mode-apt-config.log"
    [[ -s "$evidence/$mode-probe.log" && ! -s "$work/$mode.errors" ]]
    rm "$work/$mode.probe"
    echo "ALT_CACHE_LIFECYCLE_PROBE=PASS mode=$mode"

    start=$SECONDS
    ALT_ACCEPTANCE_SCOPE="$platform" ALT_CACHE_NETNS="$sidecar" ALT_CACHE_BASE_IMAGE="$image" \
        ALT_CACHE_CONFIG="$config" bash tests/linux-systemd/run-installer-acceptance.sh \
        "$artifact" "$artifact.sha256" "$artifact.provenance.json" 2>&1 | tee "$evidence/$mode-lifecycle.log"
    [[ ! -s "$work/$mode.errors" ]]
    grep -Fx "linux_installer_artifact_scope_acceptance=PASS scope=$platform" "$evidence/$mode-lifecycle.log"
    if [[ "$platform" == p11 ]]; then
        grep -Fx "ALT_CACHE_P11_SHARED_NETNS=PASS stage=initial" "$evidence/$mode-lifecycle.log"
        grep -Fx "ALT_CACHE_P11_SHARED_NETNS=PASS stage=reboot" "$evidence/$mode-lifecycle.log"
    fi
    grep '^ALT_CACHE_RPM ' "$evidence/$mode-lifecycle.log" > "$evidence/$mode-inventory.txt"
    for stage in before-product after-product final; do
        grep -q "^ALT_CACHE_RPM $stage " "$evidence/$mode-inventory.txt"
    done
    printf 'ALT_CACHE_LIFECYCLE_TIMING mode=%s platform=%s seconds=%s\n' "$mode" "$platform" "$((SECONDS-start))" | tee -a "$evidence/timing.txt"
    stop_processes
    docker rm -f "$sidecar" >/dev/null
    sidecar=""
    if [[ "$mode" == record ]]; then
        cp "$snapshot/manifest.json" "$evidence/manifest.json"
        python3 - "$snapshot" "$platform" "$base_id" "$source_sha" "$evidence/base-repo-digests.json" <<'PY'
import hashlib, json, sys, time
from pathlib import Path
root, platform, base, source, digests = sys.argv[1:]
root = Path(root)
raw = (root / "manifest.json").read_bytes()
records = json.loads(raw)["records"]
assert any(url.endswith(".rpm") and e["status"] == 200 for url, e in records.items())
assert any("pkglist" in url and e["status"] == 200 for url, e in records.items())
recipe = {}
for name in ("tests/linux-systemd/cache/alt_snapshot_proxy.py",
             "tests/linux-systemd/cache/run-cache-lifecycle.sh",
             "tests/linux-systemd/run-systemd-acceptance.sh",
             "tests/linux-systemd/run-installer-acceptance.sh",
             "webassist/install/linux/runtime-dependencies.sh"):
    recipe[name] = hashlib.sha256(Path(name).read_bytes()).hexdigest()
metadata = dict(schema=1, platform=platform, architecture="amd64", base_image_id=base,
                base_repo_digests=json.loads(Path(digests).read_text()), source_sha=source,
                captured_at=int(time.time()), manifest_sha256=hashlib.sha256(raw).hexdigest(),
                recipe=recipe)
(root / "metadata.json").write_text(json.dumps(metadata, sort_keys=True, indent=2) + "\n")
print(f"ALT_CACHE_LIFECYCLE_CAPTURED objects={len(records)} bytes={sum(e['size'] for e in records.values())}")
PY
        cp "$snapshot/metadata.json" "$evidence/metadata.json"
        # Упаковываем только данные снимка поверх чистого официального образа.
        pack="$(docker create --pull=never --network none "$base_id" /bin/true)"
        docker cp "$snapshot" "$pack:/wa-ci-snapshot"
        docker commit "$pack" "$tag" >/dev/null
        docker rm "$pack" >/dev/null
        pack=""
        cache_id="$(docker image inspect -f '{{.Id}}' "$tag")"
        printf '%s\n' "$cache_id" > "$evidence/cache-image-id.txt"
        docker image inspect -f '{{json .RootFS.Layers}}' "$base_id" > "$evidence/base-layers.json"
        docker image inspect -f '{{json .RootFS.Layers}}' "$cache_id" > "$evidence/cache-layers.json"
        python3 - "$evidence" <<'PY'
import json, sys
from pathlib import Path
p = Path(sys.argv[1])
a, b = [json.loads((p / f"{name}-layers.json").read_text()) for name in ("base", "cache")]
assert b[:-1] == a
PY
        docker save -o "$work/cache.tar" "$tag"
        sha256sum "$work/cache.tar" | cut -d ' ' -f 1 > "$evidence/local-image-tar-sha256.txt"
        stat -c '%s' "$work/cache.tar" > "$evidence/local-image-tar-size.txt"
        docker image rm "$tag" >/dev/null
        docker load -i "$work/cache.tar"
        [[ "$(docker image inspect -f '{{.Id}}' "$tag")" == "$cache_id" ]]
        rpm_inventory "$cache_id" > "$evidence/cache-rpms.txt"
        cmp "$evidence/base-rpms.txt" "$evidence/cache-rpms.txt"
        pack="$(docker create --pull=never --network none "$cache_id" /bin/true)"
        docker cp "$pack:/wa-ci-snapshot/." "$replay/"
        docker rm "$pack" >/dev/null
        pack=""
        cmp "$snapshot/manifest.json" "$replay/manifest.json"
        cmp "$snapshot/metadata.json" "$replay/metadata.json"
        diff -qr "$snapshot/objects" "$replay/objects"
        echo "ALT_CACHE_LOCAL_IMAGE_ROUNDTRIP=PASS"
    fi
done
cmp "$evidence/record-inventory.txt" "$evidence/replay-inventory.txt"
echo "ALT_CACHE_FULL_LIFECYCLE_EQUIVALENCE=PASS platform=$platform"
