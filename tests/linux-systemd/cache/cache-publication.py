#!/usr/bin/env python3
"""Доверенная передача снимков между заданиями одного запуска."""
import argparse
import hashlib
import json
import os
import re
import time
from pathlib import Path

PLATFORMS = ("p10", "p11")
PREFIX = "ghcr.io/netkeep80/webassistant-alt-cache-"
RECIPE = (
    "tests/linux-systemd/cache/alt_snapshot_proxy.py",
    "tests/linux-systemd/cache/run-cache-lifecycle.sh",
    "tests/linux-systemd/run-systemd-acceptance.sh",
    "tests/linux-systemd/run-installer-acceptance.sh",
    "webassist/install/linux/runtime-dependencies.sh",
)
MAX_AGE = 36 * 3600


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def write(path, data):
    Path(path).write_text(json.dumps(data, sort_keys=True, indent=2) + "\n", encoding="utf-8")


def digest(value):
    return isinstance(value, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value) is not None


def run_identity():
    run = os.environ["GITHUB_RUN_ID"]
    attempt = os.environ["GITHUB_RUN_ATTEMPT"]
    require(re.fullmatch(r"[1-9][0-9]*", run) and re.fullmatch(r"[1-9][0-9]*", attempt),
            "Некорректный идентификатор запуска")
    return run, attempt


def recipe():
    return {path: sha(path) for path in RECIPE}


def check_metadata(meta, platform, source, now):
    require(set(meta) == {"schema", "platform", "architecture", "base_image_id",
                         "base_repo_digests", "source_sha", "captured_at",
                         "manifest_sha256", "recipe"}, "Схема metadata")
    require(meta["schema"] == 1 and meta["platform"] == platform and
            meta["architecture"] == "amd64" and meta["source_sha"] == source,
            "Идентичность metadata")
    require(digest(meta["base_image_id"]), "Неверный base image ID")
    refs = meta["base_repo_digests"]
    prefix = f"registry.altlinux.org/{platform}/alt@"
    require(isinstance(refs, list) and any(isinstance(ref, str) and ref.startswith(prefix)
            and digest(ref[len(prefix):]) for ref in refs), "Нет исходного ALT digest")
    require(type(meta["captured_at"]) is int and 0 <= now - meta["captured_at"] <= MAX_AGE,
            "Просроченный снимок или неверное время")
    require(re.fullmatch(r"[0-9a-f]{64}", meta["manifest_sha256"]), "Неверный manifest SHA")
    require(meta["recipe"] == recipe(), "Рецепт подготовки изменился")


def receipt(platform, source, output, evidence):
    run, attempt = run_identity()
    meta = read(evidence / "metadata.json")
    check_metadata(meta, platform, source, int(time.time()))
    require(sha(evidence / "manifest.json") == meta["manifest_sha256"], "Manifest SHA")
    require(sha(output / "cache.tar") ==
            (evidence / "local-image-tar-sha256.txt").read_text().strip(), "Tar изменён")
    require((evidence / "base-rpms.txt").read_bytes() == (evidence / "cache-rpms.txt").read_bytes(),
            "Изменён исходный RPM inventory")
    inventory = sha(evidence / "record-inventory.txt")
    require(inventory == sha(evidence / "replay-inventory.txt"), "Lifecycle RPM inventory")
    image_id = (evidence / "cache-image-id.txt").read_text().strip()
    require(digest(image_id), "Некорректный cache image ID")
    for name in ("metadata.json", "manifest.json"):
        (output / name).write_bytes((evidence / name).read_bytes())
    write(output / "receipt.json", dict(schema=1, platform=platform, source_sha=source,
          run_id=run, run_attempt=attempt, image_id=image_id, tar_sha256=sha(output / "cache.tar"),
          metadata_sha256=sha(output / "metadata.json"), manifest_sha256=meta["manifest_sha256"],
          inventory_sha256=inventory, lifecycle="PASS"))


def verify(platform, source, directory):
    run, attempt = run_identity()
    require(platform in PLATFORMS and re.fullmatch(r"[0-9a-f]{40}", source), "Неверная идентичность")
    require({p.name for p in directory.iterdir()} ==
            {"cache.tar", "metadata.json", "manifest.json", "receipt.json"}, "Лишние/отсутствующие файлы")
    require(all(p.is_file() and not p.is_symlink() for p in directory.iterdir()), "Недопустимый файл")
    r = read(directory / "receipt.json")
    require(set(r) == {"schema", "platform", "source_sha", "run_id", "run_attempt", "image_id",
                     "tar_sha256", "metadata_sha256", "manifest_sha256", "inventory_sha256",
                     "lifecycle"}, "Схема receipt")
    require((r["schema"], r["platform"], r["source_sha"], r["run_id"], r["run_attempt"],
             r["lifecycle"]) == (1, platform, source, run, attempt, "PASS"), "Чужой запуск/результат")
    require(digest(r["image_id"]), "Неверный image ID")
    for key in ("tar_sha256", "metadata_sha256", "manifest_sha256", "inventory_sha256"):
        require(isinstance(r[key], str) and re.fullmatch(r"[0-9a-f]{64}", r[key]), "Неверный SHA")
    for name, key in (("cache.tar", "tar_sha256"), ("metadata.json", "metadata_sha256"),
                      ("manifest.json", "manifest_sha256")):
        require(sha(directory / name) == r[key], f"Изменён {name}")
    meta = read(directory / "metadata.json")
    check_metadata(meta, platform, source, int(time.time()))
    require(meta["manifest_sha256"] == r["manifest_sha256"], "Manifest не соответствует metadata")
    return r, meta


def pair(source, p10, p11, d10, d11):
    run, attempt = run_identity()
    records = {}
    for platform, directory, remote in zip(PLATFORMS, (p10, p11), (d10, d11)):
        r, meta = verify(platform, source, directory)
        require(digest(remote), "Некорректный удалённый digest")
        records[platform] = dict(ref=f"{PREFIX}{platform}@{remote}", image_id=r["image_id"],
                                 metadata_sha256=r["metadata_sha256"],
                                 manifest_sha256=r["manifest_sha256"],
                                 captured_at=meta["captured_at"], recipe=meta["recipe"],
                                 base_image_id=meta["base_image_id"],
                                 base_repo_digests=meta["base_repo_digests"])
    return dict(schema=1, source_sha=source, run_id=run, run_attempt=attempt,
                expires_at=min(r["captured_at"] for r in records.values()) + MAX_AGE,
                platforms=records)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="action", required=True)
    for name in ("receipt", "verify"):
        p = sub.add_parser(name)
        p.add_argument("platform", choices=PLATFORMS)
        p.add_argument("source")
        p.add_argument("directory", type=Path)
        if name == "receipt":
            p.add_argument("evidence", type=Path)
    p = sub.add_parser("pair")
    p.add_argument("source")
    p.add_argument("p10", type=Path)
    p.add_argument("p11", type=Path)
    p.add_argument("d10")
    p.add_argument("d11")
    p.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.action == "receipt":
        receipt(args.platform, args.source, args.directory, args.evidence)
    elif args.action == "verify":
        verify(args.platform, args.source, args.directory)
    else:
        write(args.output, pair(args.source, args.p10, args.p11, args.d10, args.d11))
