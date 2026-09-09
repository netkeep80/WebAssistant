#!/usr/bin/env python3
import argparse
import hashlib
import html
import json
import re
import sys
from pathlib import Path

SCHEMA = "webassistant-installation-evidence/v1"
SOURCE_RE = re.compile(r"^[0-9a-f]{40}$")
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")

CAPTURES = {
    "WIN_ARTIFACT_IDENTITY": ("windows", "Windows: идентификатор и SHA-256 установщика"),
    "WIN_INSTALL_UAC": ("windows", "Windows: запуск установщика и подтверждение UAC"),
    "WIN_INSTALLED_APPS": ("windows", "Windows: WebAssistant в Installed Apps и DisplayVersion"),
    "WIN_SERVICE_HEALTH": ("windows", "Windows: служба WebAssistant и /v1/health"),
    "WIN_UNINSTALL": ("windows", "Windows: стандартное удаление WebAssistant"),
    "ALT_ARTIFACT_IDENTITY": ("alt-linux", "ALT Linux 10.1: идентификатор и SHA-256 пакета"),
    "ALT_INSTALL": ("alt-linux", "ALT Linux 10.1: установка из canonical ZIP"),
    "ALT_SYSTEMD_HEALTH": ("alt-linux", "ALT Linux 10.1: systemd service и /v1/health"),
    "ALT_RESTART": ("alt-linux", "ALT Linux 10.1: перезапуск webassist.service"),
    "ALT_UNINSTALL": ("alt-linux", "ALT Linux 10.1: удаление WebAssistant"),
}

TOP_KEYS = {"schema", "kind", "sourceSha", "version", "artifacts", "altTarget", "captures"}
ARTIFACT_KEYS = {"windows", "linux"}
ARTIFACT_ID_KEYS = {"filename", "sha256"}
ALT_KEYS = {"osName", "osVersion", "completed"}
CAPTURE_KEYS = {"slot", "platform", "path", "sha256", "kind"}


def fail(message: str) -> None:
    print(f"installation evidence: {message}", file=sys.stderr)
    raise SystemExit(2)


def require_object(value, name: str) -> dict:
    if not isinstance(value, dict):
        fail(f"{name} must be an object")
    return value


def require_exact_keys(value: dict, allowed: set[str], required: set[str], name: str) -> None:
    unknown = set(value) - allowed
    missing = required - set(value)
    if unknown:
        fail(f"{name} contains unknown properties: {', '.join(sorted(unknown))}")
    if missing:
        fail(f"{name} is missing required properties: {', '.join(sorted(missing))}")


def require_string(value, name: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{name} must be a non-empty string")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def capture_path(manifest_dir: Path, relative: str) -> Path:
    raw = Path(relative)
    if raw.is_absolute():
        fail(f"capture path must be relative: {relative}")
    candidate = (manifest_dir / raw).resolve()
    try:
        candidate.relative_to(manifest_dir.resolve())
    except ValueError:
        fail(f"capture path escapes evidence directory: {relative}")
    return candidate


def figure_markdown(slot: str, path: Path) -> str:
    _, caption = CAPTURES[slot]
    uri = path.resolve().as_uri()
    return (
        f'<figure class="evidence-figure" data-slot="{html.escape(slot)}">\n'
        f'  <img src="{html.escape(uri)}" alt="{html.escape(caption)}" />\n'
        f'  <figcaption>{html.escape(caption)}</figcaption>\n'
        f'</figure>'
    )


def validate(args) -> tuple[dict, dict[str, Path]]:
    if args.mode not in {"fixture", "final"}:
        fail("mode must be fixture or final")
    if not SOURCE_RE.fullmatch(args.source_sha):
        fail("sourceSha argument must be exactly 40 lowercase hexadecimal characters")
    if not VERSION_RE.fullmatch(args.version):
        fail("version argument must be numeric SemVer major.minor.patch")

    manifest_path = Path(args.manifest).resolve()
    if not manifest_path.is_file():
        fail(f"manifest does not exist: {manifest_path}")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"cannot read manifest JSON: {exc}")

    manifest = require_object(manifest, "manifest")
    require_exact_keys(manifest, TOP_KEYS, TOP_KEYS, "manifest")

    schema = require_string(manifest["schema"], "schema")
    if schema != SCHEMA:
        fail(f"unsupported schema: {schema}")

    kind = require_string(manifest["kind"], "kind")
    if kind not in {"fixture", "final"}:
        fail("kind must be fixture or final")
    if kind != args.mode:
        fail(f"evidence kind={kind} cannot be used in mode={args.mode}")

    source_sha = require_string(manifest["sourceSha"], "sourceSha")
    if not SOURCE_RE.fullmatch(source_sha):
        fail("manifest sourceSha must be exactly 40 lowercase hexadecimal characters")
    if source_sha != args.source_sha:
        fail(f"manifest sourceSha does not match WEBASSISTANT_SOURCE_SHA: {source_sha} != {args.source_sha}")

    version = require_string(manifest["version"], "version")
    if not VERSION_RE.fullmatch(version):
        fail("manifest version must be numeric SemVer major.minor.patch")
    if version != args.version:
        fail(f"manifest version does not match webassist/VERSION: {version} != {args.version}")

    artifacts = require_object(manifest["artifacts"], "artifacts")
    require_exact_keys(artifacts, ARTIFACT_KEYS, ARTIFACT_KEYS, "artifacts")
    canonical_names = {
        "windows": f"WebAssistant-win-x64-{version}.exe",
        "linux": f"WebAssistant-linux-x64-{version}.zip",
    }
    for platform, canonical_name in canonical_names.items():
        identity = require_object(artifacts[platform], f"artifacts.{platform}")
        require_exact_keys(identity, ARTIFACT_ID_KEYS, ARTIFACT_ID_KEYS, f"artifacts.{platform}")
        filename = require_string(identity["filename"], f"artifacts.{platform}.filename")
        digest = require_string(identity["sha256"], f"artifacts.{platform}.sha256")
        if filename != canonical_name:
            fail(f"artifacts.{platform}.filename is not canonical for VERSION {version}: {filename}")
        if not SHA256_RE.fullmatch(digest):
            fail(f"artifacts.{platform}.sha256 must be 64 lowercase hexadecimal characters")

    alt_target = require_object(manifest["altTarget"], "altTarget")
    require_exact_keys(alt_target, ALT_KEYS, ALT_KEYS, "altTarget")
    os_name = require_string(alt_target["osName"], "altTarget.osName")
    os_version = require_string(alt_target["osVersion"], "altTarget.osVersion")
    completed = alt_target["completed"]
    if not isinstance(completed, bool):
        fail("altTarget.completed must be boolean")
    if args.mode == "final" and (os_name != "ALT Linux" or os_version != "10.1" or completed is not True):
        fail("final evidence requires completed actual ALT Linux 10.1 target evidence")

    captures = manifest["captures"]
    if not isinstance(captures, list):
        fail("captures must be an array")
    seen: set[str] = set()
    resolved: dict[str, Path] = {}
    manifest_dir = manifest_path.parent
    for index, item in enumerate(captures):
        capture = require_object(item, f"captures[{index}]")
        require_exact_keys(capture, CAPTURE_KEYS, CAPTURE_KEYS, f"captures[{index}]")
        slot = require_string(capture["slot"], f"captures[{index}].slot")
        if slot not in CAPTURES:
            fail(f"unknown capture slot: {slot}")
        if slot in seen:
            fail(f"duplicate capture slot: {slot}")
        seen.add(slot)
        expected_platform, _ = CAPTURES[slot]
        platform = require_string(capture["platform"], f"captures[{index}].platform")
        if platform != expected_platform:
            fail(f"capture slot {slot} requires platform={expected_platform}, got {platform}")
        capture_kind = require_string(capture["kind"], f"captures[{index}].kind")
        if capture_kind != kind:
            fail(f"capture slot {slot} kind={capture_kind} does not match manifest kind={kind}")
        relative = require_string(capture["path"], f"captures[{index}].path")
        path = capture_path(manifest_dir, relative)
        if not path.is_file():
            fail(f"capture file does not exist for slot {slot}: {relative}")
        expected_hash = require_string(capture["sha256"], f"captures[{index}].sha256")
        if not SHA256_RE.fullmatch(expected_hash):
            fail(f"capture slot {slot} sha256 must be 64 lowercase hexadecimal characters")
        actual_hash = sha256_file(path)
        if actual_hash != expected_hash:
            fail(f"capture slot {slot} sha256 mismatch: expected {expected_hash}, got {actual_hash}")
        resolved[slot] = path

    missing = set(CAPTURES) - seen
    if missing:
        fail(f"missing required capture slots: {', '.join(sorted(missing))}")

    return manifest, resolved


def expand_guide(args, manifest: dict, captures: dict[str, Path]) -> None:
    guide_path = Path(args.guide).resolve()
    if not guide_path.is_file():
        fail(f"guide source does not exist: {guide_path}")
    text = guide_path.read_text(encoding="utf-8")
    replacements = {
        "VERSION": manifest["version"],
        "SOURCE_SHA": manifest["sourceSha"],
        "WINDOWS_FILENAME": manifest["artifacts"]["windows"]["filename"],
        "WINDOWS_SHA256": manifest["artifacts"]["windows"]["sha256"],
        "LINUX_FILENAME": manifest["artifacts"]["linux"]["filename"],
        "LINUX_SHA256": manifest["artifacts"]["linux"]["sha256"],
        "ALT_OS_NAME": manifest["altTarget"]["osName"],
        "ALT_OS_VERSION": manifest["altTarget"]["osVersion"],
    }
    for key, value in replacements.items():
        text = text.replace("{{" + key + "}}", str(value))
    for slot, path in captures.items():
        text = text.replace("{{" + slot + "}}", figure_markdown(slot, path))

    leftovers = sorted(set(re.findall(r"\{\{[A-Z0-9_]+\}\}", text)))
    if leftovers:
        fail(f"guide contains unresolved placeholders: {', '.join(leftovers)}")

    output = Path(args.output_markdown).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="Validate installation evidence and bind it to the guide source.")
    parser.add_argument("--mode", required=True, choices=["fixture", "final"])
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--guide", required=True)
    parser.add_argument("--output-markdown", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()

    manifest, captures = validate(args)
    expand_guide(args, manifest, captures)


if __name__ == "__main__":
    main()
