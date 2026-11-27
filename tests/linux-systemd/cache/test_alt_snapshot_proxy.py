import json
from pathlib import Path
import tempfile
import socket
import subprocess
import sys
import time
import http.client
import unittest
from unittest.mock import patch

from alt_snapshot_proxy import Snapshot, allowed

URL = "http://ftp.altlinux.org/pub/distributions/ALTLinux/p10/branch/x86_64/base/pkglist.classic.xz"
MISSING = URL + ".missing"


class SnapshotTests(unittest.TestCase):
    def test_origin_boundary(self):
        self.assertTrue(allowed(URL, "p10"))
        for url in (URL.replace("ftp.altlinux.org", "example.org"),
                    URL.replace("p10", "p11"), URL + "?x=1",
                    URL + "/%2e%2e/a", URL.replace("http://", "file://")):
            self.assertFalse(allowed(url, "p10"), url)

    def test_record_and_replay_exact_bytes_and_status(self):
        with tempfile.TemporaryDirectory() as root:
            writer = Snapshot(root, "p10", "record")
            with patch("alt_snapshot_proxy.fetch", side_effect=[(200, b"index"), (404, b"missing")]) as fetch:
                self.assertEqual(writer.get(URL), (200, b"index"))
                self.assertEqual(writer.get(URL), (200, b"index"))
                self.assertEqual(writer.get(MISSING), (404, b"missing"))
                self.assertEqual(fetch.call_count, 2)
            with patch("alt_snapshot_proxy.fetch", side_effect=AssertionError("network forbidden")):
                reader = Snapshot(root, "p10", "replay")
                self.assertEqual(reader.get(URL), (200, b"index"))
                self.assertEqual(reader.get(MISSING), (404, b"missing"))
                with self.assertRaises(KeyError):
                    reader.get(URL + ".unknown")
                with self.assertRaises(ValueError):
                    reader.get(URL.replace("p10", "p11"))
                with self.assertRaises(ValueError):
                    Snapshot(root, "p11", "replay")

    def test_corruption_detected_at_start_and_read(self):
        with tempfile.TemporaryDirectory() as root:
            writer = Snapshot(root, "p10", "record")
            with patch("alt_snapshot_proxy.fetch", return_value=(200, b"index")):
                writer.get(URL)
            reader = Snapshot(root, "p10", "replay")
            manifest = json.loads((Path(root) / "manifest.json").read_text())
            path = Path(root) / "objects" / manifest["records"][URL]["sha256"]
            path.write_bytes(b"xxxxx")
            with self.assertRaises(ValueError):
                reader.get(URL)
            with self.assertRaises(ValueError):
                Snapshot(root, "p10", "replay")

    def test_unix_transport_and_probe_do_not_access_upstream(self):
        # Реальные процессы, TCP -> Unix socket -> HTTP handler.
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            script = Path(__file__).with_name("alt_snapshot_proxy.py")
            with socket.socket() as reserved:
                reserved.bind(("127.0.0.1", 0))
                port = reserved.getsockname()[1]
            sock = root / "proxy.sock"
            probe = root / "probe"
            probe.touch()
            common = ["--probe-file", str(probe), "--probe-log", str(root / "probe.log"),
                      "--error-file", str(root / "errors")]
            processes = []
            try:
                for name, command in (
                    ("proxy", ["--platform", "p10", "--mode", "record",
                               "--snapshot", str(root / "snapshot"), "--unix-socket", str(sock)] + common),
                    ("relay", ["--relay-socket", str(sock), "--port", str(port)]),
                ):
                    ready = root / (name + ".ready")
                    p = subprocess.Popen([sys.executable, str(script)] + command +
                                         ["--ready-file", str(ready), "--pid-file", str(root / (name + ".pid"))])
                    processes.append(p)
                    for _ in range(100):
                        if ready.exists():
                            break
                        self.assertIsNone(p.poll())
                        time.sleep(0.02)
                    self.assertTrue(ready.exists())
                client = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
                try:
                    client.request("GET", URL)
                    response = client.getresponse()
                    self.assertEqual(response.status, 503)
                    response.read()
                finally:
                    client.close()
                self.assertEqual((root / "probe.log").read_text(), "GET " + URL + "\n")
                self.assertEqual(json.loads((root / "snapshot/manifest.json").read_text())["records"], {})
                self.assertFalse((root / "errors").exists())
            finally:
                for p in reversed(processes):
                    p.terminate()
                    p.wait(timeout=5)

    def test_upstream_failure_does_not_become_a_cached_success(self):
        with tempfile.TemporaryDirectory() as root:
            writer = Snapshot(root, "p10", "record")
            with patch("alt_snapshot_proxy.fetch", side_effect=TimeoutError):
                with self.assertRaises(TimeoutError):
                    writer.get(URL)
            self.assertEqual(writer.records, {})
            with self.assertRaises(ValueError):
                Snapshot(root, "p10", "replay")


if __name__ == "__main__":
    unittest.main()
