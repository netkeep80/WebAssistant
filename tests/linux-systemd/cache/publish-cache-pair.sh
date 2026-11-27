#!/usr/bin/env bash
set -Eeuo pipefail
# Защита в глубину: даже прямой вызов скрипта вне доверенного workflow отклонён.
[[ "$GITHUB_REPOSITORY" == netkeep80/WebAssistant && "$GITHUB_REF" == refs/heads/main ]]
[[ "$GITHUB_EVENT_NAME" == schedule || "$GITHUB_EVENT_NAME" == workflow_dispatch ]]
[[ "${ALT_CACHE_PUBLICATION_APPROVED:-}" == true ]]
[[ "$(git rev-parse HEAD)" == "$GITHUB_SHA" ]]
[[ "$GITHUB_RUN_ID" =~ ^[1-9][0-9]*$ && "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]]
assert_current_main() {
    local tip
    tip="$(git ls-remote --exit-code https://github.com/netkeep80/WebAssistant.git refs/heads/main)"
    [[ "$tip" == "$GITHUB_SHA"$'\t'refs/heads/main ]]
}
assert_current_main
root="$(realpath "${1:?Каталог экспортов}")"
script=tests/linux-systemd/cache/cache-publication.py
prefix=ghcr.io/netkeep80/webassistant-alt-cache-
generation="run-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
work="$(mktemp -d)"
container=""
export DOCKER_CONFIG="$work/docker-config"
mkdir -p "$DOCKER_CONFIG"
cleanup() {
    set +e
    [[ -z "$container" ]] || docker rm -f "$container" >/dev/null 2>&1
    docker logout ghcr.io >/dev/null 2>&1
    rm -rf "$work"
}
trap cleanup EXIT
for platform in p10 p11; do
    dir="$root/$platform"
    python3 "$script" verify "$platform" "$GITHUB_SHA" "$dir"
    docker load -i "$dir/cache.tar"
    image="$(jq -er .image_id "$dir/receipt.json")"
    [[ "$(docker image inspect -f '{{.Id}}' "$image")" == "$image" ]]
    [[ "$(docker image inspect -f '{{.Architecture}}' "$image")" == amd64 ]]
    [[ "$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.source"}}' "$image")" == https://github.com/netkeep80/WebAssistant ]]
    # Не выполнять команды внутри образа в задании с правом публикации.
    container="$(docker create --pull=never --network none "$image" /never-execute)"
    docker cp "$container:/wa-ci-snapshot/metadata.json" "$work/$platform-metadata.json"
    docker cp "$container:/wa-ci-snapshot/manifest.json" "$work/$platform-manifest.json"
    docker rm "$container" >/dev/null
    container=""
    cmp "$dir/metadata.json" "$work/$platform-metadata.json"
    cmp "$dir/manifest.json" "$work/$platform-manifest.json"
done
printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "$GITHUB_ACTOR" --password-stdin
unset GHCR_TOKEN
push_digest() {
    local ref="$1" log="$2" digest
    docker push "$ref" | tee "$log" >&2
    digest="$(sed -nE 's/^.*digest: (sha256:[0-9a-f]{64}) size: [0-9]+.*$/\1/p' "$log")"
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]
    printf '%s\n' "$digest"
}
declare -A digests
for platform in p10 p11; do
    image="$(jq -er .image_id "$root/$platform/receipt.json")"
    ref="$prefix$platform"
    docker tag "$image" "$ref:$generation"
    digests[$platform]="$(push_digest "$ref:$generation" "$work/$platform-push.log")"
    docker pull "$ref@${digests[$platform]}"
    [[ "$(docker image inspect -f '{{.Id}}' "$ref@${digests[$platform]}")" == "$image" ]]
    echo "ALT_CACHE_GHCR_IMAGE=PASS platform=$platform ref=$ref@${digests[$platform]}"
done
python3 "$script" pair "$GITHUB_SHA" "$root/p10" "$root/p11" \
    "${digests[p10]}" "${digests[p11]}" "$work/pair.json"
mkdir "$work/pair"
cp "$work/pair.json" "$work/pair/pair.json"
cat > "$work/pair/Dockerfile" <<'DOCKERFILE'
FROM scratch
LABEL org.opencontainers.image.source="https://github.com/netkeep80/WebAssistant"
COPY pair.json /pair.json
DOCKERFILE
pair_ref="${prefix}pair"
docker build --network none --tag "$pair_ref:$generation" "$work/pair"
pair_digest="$(push_digest "$pair_ref:$generation" "$work/pair-push.log")"
docker pull "$pair_ref@$pair_digest"
container="$(docker create --pull=never --network none "$pair_ref@$pair_digest" /never-execute)"
docker cp "$container:/pair.json" "$work/remote-pair.json"
docker rm "$container" >/dev/null
container=""
cmp "$work/pair.json" "$work/remote-pair.json"
# Единственная точка продвижения: обе платформы уже опубликованы и проверены.
# Проверка текущего main непосредственно перед продвижением.
assert_current_main
docker tag "$pair_ref@$pair_digest" "$pair_ref:ready"
ready_digest="$(push_digest "$pair_ref:ready" "$work/ready-push.log")"
[[ "$ready_digest" == "$pair_digest" ]]
printf 'ALT_CACHE_GHCR_PAIR_READY=PASS ref=%s@%s\n' "$pair_ref" "$pair_digest"
cat "$work/pair.json"
