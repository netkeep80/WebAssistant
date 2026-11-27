#!/usr/bin/env python3
"""Ограниченный снимок HTTP-ответов официальной ветки ALT для испытаний."""
import argparse
import hashlib
import json
import os
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import threading
import socket
import socketserver
import selectors
from urllib.error import HTTPError
from urllib.parse import unquote, urlsplit
from urllib.request import (HTTPRedirectHandler, ProxyHandler, Request, build_opener)

LIMIT = 256 * 1024 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest()


def allowed(url, platform):
    p = urlsplit(url)
    path = unquote(p.path)
    prefixes = (f"/pub/distributions/ALTLinux/{platform}/branch/",
                f"/{platform}/branch/")
    return (p.scheme in ("http", "https")
            and p.netloc in ("ftp.altlinux.org", "ftp.altlinux.org:80",
                             "ftp.altlinux.org:443")
            and not p.query and not p.fragment
            and ".." not in path.split("/") and "\\" not in path
            and not any(ord(c) < 32 for c in path)
            and path.startswith(prefixes))


class RestrictedRedirect(HTTPRedirectHandler):
    def __init__(self, platform):
        self.platform = platform

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not allowed(newurl, self.platform):
            raise ValueError("Переадресация за пределы разрешённой ветки ALT")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def fetch(url, platform):
    # Не использовать произвольные прокси и не пересылать заголовки клиента.
    opener = build_opener(ProxyHandler({}), RestrictedRedirect(platform))
    request = Request(url, headers={"Accept-Encoding": "identity"})
    try:
        response = opener.open(request, timeout=120)
    except HTTPError as exc:
        if exc.code != 404:
            exc.close()
            raise
        response = exc
    with response:
        if response.status not in (200, 404):
            raise ValueError(f"Неожиданный HTTP-статус {response.status}")
        if response.headers.get("Content-Encoding", "identity").lower() != "identity":
            raise ValueError("Неожиданное кодирование ответа")
        data = response.read(LIMIT + 1)
        if len(data) > LIMIT:
            raise ValueError("Превышен предел размера объекта")
        length = response.headers.get("Content-Length")
        if length is not None and int(length) != len(data):
            raise ValueError("Неполный HTTP-ответ")
        return response.status, data


class Snapshot:
    def __init__(self, root, platform, mode):
        self.root = Path(root)
        self.platform = platform
        self.mode = mode
        self.lock = threading.Lock()
        self.manifest = self.root / "manifest.json"
        self.objects = self.root / "objects"
        if mode == "record":
            self.root.mkdir(parents=True, exist_ok=True)
            if any(self.root.iterdir()):
                raise ValueError("Запись допускается только в пустой каталог")
            self.objects.mkdir()
            self.records = {}
            self.save()
        elif mode == "replay":
            doc = json.loads(self.manifest.read_text(encoding="utf-8"))
            if set(doc) != {"schema", "platform", "records"} or doc["schema"] != 1 or doc["platform"] != platform:
                raise ValueError("Несовместимый манифест")
            self.records = doc["records"]
            if not isinstance(self.records, dict) or not self.records:
                raise ValueError("Пустой или некорректный снимок")
            for url, entry in self.records.items():
                if not allowed(url, platform):
                    raise ValueError("Недопустимый URL в манифесте")
                self.read(entry)
        else:
            raise ValueError("Недопустимый режим")

    def save(self):
        tmp = self.root / "manifest.new"
        tmp.write_text(json.dumps({"schema": 1, "platform": self.platform,
                                   "records": self.records},
                                  sort_keys=True, indent=2) + "\n", encoding="utf-8")
        os.replace(tmp, self.manifest)

    def read(self, entry):
        if set(entry) != {"status", "sha256", "size"} or entry["status"] not in (200, 404):
            raise ValueError("Некорректная запись снимка")
        sha = entry["sha256"]
        if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
            raise ValueError("Некорректный SHA-256")
        size = entry["size"]
        if type(size) is not int or not 0 <= size <= LIMIT:
            raise ValueError("Некорректный размер")
        path = self.objects / sha
        if not path.is_file() or path.is_symlink() or path.stat().st_size != size:
            raise ValueError("Размер или тип объекта не совпадает")
        data = path.read_bytes()
        if digest(data) != sha:
            raise ValueError("SHA-256 объекта не совпадает")
        return entry["status"], data

    def get(self, url):
        if not allowed(url, self.platform):
            raise ValueError("URL за пределами разрешённой ветки ALT")
        with self.lock:
            if url in self.records:
                return self.read(self.records[url])
            if self.mode != "record":
                raise KeyError("URL отсутствует в снимке")
            status, data = fetch(url, self.platform)
            sha = digest(data)
            path = self.objects / sha
            if not path.exists():
                path.write_bytes(data)
            self.records[url] = {"status": status, "sha256": sha, "size": len(data)}
            self.save()
            print(f"ALT_CACHE_RECORD status={status} bytes={len(data)} url={url}", flush=True)
            return status, data


def serve(args):
    snapshot = Snapshot(args.snapshot, args.platform, args.mode)

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            self.reply(True)

        def do_HEAD(self):
            self.reply(False)

        def reply(self, body):
            if Path(args.probe_file).exists() and allowed(self.path, args.platform):
                with open(args.probe_log, "a", encoding="utf-8") as stream:
                    stream.write(f"{self.command} {self.path}\n")
                self.send_error(503, "Probe only; no upstream request")
                return
            try:
                status, data = snapshot.get(self.path)
            except Exception as exc:
                # Даже если APT допускает частичный отказ update, harness отклонит прогон.
                with open(args.error_file, "a", encoding="utf-8") as stream:
                    stream.write(f"{type(exc).__name__}: {exc}: {self.path}\n")
                self.send_error(502, "Snapshot failure")
                return
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Connection", "close")
            self.end_headers()
            if body:
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError):
                    pass

        def do_CONNECT(self):
            with open(args.error_file, "a", encoding="utf-8") as stream:
                stream.write("CONNECT запрещён\n")
            self.send_error(502, "CONNECT unsupported")

        def log_message(self, format, *args):
            pass

    if args.unix_socket:
        # Сокет доступен только процессам испытательного runner, не контейнеру.
        os.umask(0o077)
        server = socketserver.ThreadingUnixStreamServer(args.unix_socket, Handler)
    else:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    with server:
        signal_ready(args)
        server.serve_forever()


def signal_ready(args):
    Path(args.pid_file).write_text(str(os.getpid()), encoding="ascii")
    Path(args.ready_file).touch()


def relay(args):
    # Только один заранее заданный Unix socket; произвольные адреса не принимаются.
    class RelayHandler(socketserver.BaseRequestHandler):
        def handle(self):
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as upstream:
                upstream.connect(args.relay_socket)
                with selectors.DefaultSelector() as selector:
                    selector.register(self.request, selectors.EVENT_READ, upstream)
                    selector.register(upstream, selectors.EVENT_READ, self.request)
                    while selector.get_map():
                        for key, _ in selector.select():
                            data = key.fileobj.recv(65536)
                            if data:
                                key.data.sendall(data)
                            else:
                                selector.unregister(key.fileobj)
                                try:
                                    key.data.shutdown(socket.SHUT_WR)
                                except OSError:
                                    pass

    with socketserver.ThreadingTCPServer(("127.0.0.1", args.port), RelayHandler) as server:
        signal_ready(args)
        server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--platform", choices=("p10", "p11"))
    parser.add_argument("--mode", choices=("record", "replay"))
    parser.add_argument("--snapshot")
    parser.add_argument("--port", type=int)
    parser.add_argument("--unix-socket")
    parser.add_argument("--relay-socket")
    parser.add_argument("--probe-file")
    parser.add_argument("--probe-log")
    parser.add_argument("--ready-file", required=True)
    parser.add_argument("--pid-file", required=True)
    parser.add_argument("--error-file")
    args = parser.parse_args()
    if args.relay_socket:
        if not args.port or args.unix_socket or args.mode:
            parser.error("Некорректные параметры relay")
        relay(args)
    else:
        if not all((args.platform, args.mode, args.snapshot, args.probe_file,
                    args.probe_log, args.error_file)):
            parser.error("Не заданы обязательные параметры прокси")
        if bool(args.unix_socket) == bool(args.port):
            parser.error("Требуется ровно один транспорт")
        if args.unix_socket and args.mode != "record":
            parser.error("Unix socket допускается только для записи")
        serve(args)
