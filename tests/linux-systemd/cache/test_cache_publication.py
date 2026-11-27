import importlib.util
import json
import os
from pathlib import Path
import tempfile
import subprocess
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("publication", Path(__file__).with_name("cache-publication.py"))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
SOURCE = "a" * 40
ID = "sha256:" + "b" * 64


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.env = patch.dict(os.environ, GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="1")
        self.env.start()
        self.addCleanup(self.env.stop)
        self.root = Path(self.temp.name)
        for platform in p.PLATFORMS:
            d = self.root / platform
            d.mkdir()
            (d / "cache.tar").write_bytes(b"synthetic tar")
            p.write(d / "manifest.json", {"schema": 1, "platform": platform})
            meta = dict(schema=1, platform=platform, architecture="amd64", base_image_id=ID,
                        base_repo_digests=[f"registry.altlinux.org/{platform}/alt@{ID}"],
                        source_sha=SOURCE, captured_at=int(time.time()),
                        manifest_sha256=p.sha(d / "manifest.json"), recipe=p.recipe())
            p.write(d / "metadata.json", meta)
            p.write(d / "receipt.json", dict(schema=1, platform=platform, source_sha=SOURCE,
                run_id="123", run_attempt="1", image_id=ID, tar_sha256=p.sha(d / "cache.tar"),
                metadata_sha256=p.sha(d / "metadata.json"), manifest_sha256=meta["manifest_sha256"],
                inventory_sha256="c" * 64, lifecycle="PASS"))

    def test_exact_pair(self):
        pair = p.pair(SOURCE, self.root / "p10", self.root / "p11", ID, ID)
        self.assertEqual(set(pair["platforms"]), {"p10", "p11"})
        self.assertEqual(pair["platforms"]["p10"]["ref"], p.PREFIX + "p10@" + ID)
        self.assertEqual(pair["platforms"]["p11"]["ref"], p.PREFIX + "p11@" + ID)

    def test_reject_corruption(self):
        (self.root / "p10/cache.tar").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            p.verify("p10", SOURCE, self.root / "p10")

    def test_reject_cross_run_platform_and_source(self):
        with patch.dict(os.environ, GITHUB_RUN_ATTEMPT="2"):
            with self.assertRaises(ValueError):
                p.verify("p10", SOURCE, self.root / "p10")
        for platform, source in (("p11", SOURCE), ("p10", "d" * 40)):
            with self.assertRaises(ValueError):
                p.verify(platform, source, self.root / "p10")

    def test_reject_stale_metadata_even_with_matching_hash(self):
        d = self.root / "p10"
        m = p.read(d / "metadata.json")
        m["captured_at"] -= p.MAX_AGE + 1
        p.write(d / "metadata.json", m)
        r = p.read(d / "receipt.json")
        r["metadata_sha256"] = p.sha(d / "metadata.json")
        p.write(d / "receipt.json", r)
        with self.assertRaises(ValueError):
            p.verify("p10", SOURCE, d)

    def test_partial_pair_is_rejected(self):
        (self.root / "p11/receipt.json").unlink()
        with self.assertRaises(ValueError):
            p.pair(SOURCE, self.root / "p10", self.root / "p11", ID, ID)

    def test_bad_remote_digest_is_rejected(self):
        with self.assertRaises(ValueError):
            p.pair(SOURCE, self.root / "p10", self.root / "p11", "latest", ID)

    def test_publish_guard_rejects_untrusted_context_without_commands(self):
        # Любая попытка вызвать git/docker до отказа оставит след.
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        trace = self.root / "trace"
        for name in ("git", "docker"):
            cmd = bin_dir / name
            cmd.write_text('#!/bin/bash\nprintf "%s\\n" "$0" >> "$TRACE"\nexit 99\n')
            cmd.chmod(0o755)
        base = dict(os.environ, GITHUB_REPOSITORY="netkeep80/WebAssistant",
                    GITHUB_REF="refs/heads/main", GITHUB_EVENT_NAME="workflow_dispatch",
                    ALT_CACHE_PUBLICATION_APPROVED="true", GITHUB_SHA=SOURCE,
                    PATH=str(bin_dir) + ":" + os.environ["PATH"], TRACE=str(trace))
        script = Path(__file__).with_name("publish-cache-pair.sh").resolve()
        for override in ({"GITHUB_EVENT_NAME": "pull_request"},
                         {"GITHUB_REF": "refs/heads/other"},
                         {"GITHUB_REPOSITORY": "fork/WebAssistant"},
                         {"ALT_CACHE_PUBLICATION_APPROVED": ""}):
            env = dict(base, **override)
            result = subprocess.run(["bash", str(script), str(self.root)], env=env,
                                    capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(trace.exists(), override)
