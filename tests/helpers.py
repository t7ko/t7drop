"""Shared by the tests: a fake clock, raw HTTP requests, pairing."""

import base64
import hashlib
import http.client
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
for folder in ("server", "cli"):
    path = os.path.join(ROOT, folder)
    if path not in sys.path:
        sys.path.insert(0, path)

from t7drop_server.app import Server  # noqa: E402
from t7drop_server.config import Config  # noqa: E402

API = "/api/v1"
CHUNK = 4 * 1024 * 1024


class Clock:
    def __init__(self):
        self.t = 1_800_000_000.0

    def __call__(self):
        return self.t

    def advance(self, seconds):
        self.t += seconds


class Response:
    def __init__(self, status, headers, body):
        self.status = status
        self.headers = {k.lower(): v for k, v in headers}
        self.body = body

    @property
    def instance(self):
        return self.headers.get("x-t7drop-instance")

    def json(self):
        return json.loads(self.body)


class Http:
    def __init__(self, port, token=None):
        self.port = port
        self.token = token

    def request(self, method, path, body=None, json_body=None, token="default", headers=None, timeout=30):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        all_headers = dict(headers or {})
        token = self.token if token == "default" else token
        if token:
            all_headers["Authorization"] = "Bearer " + token
        if json_body is not None:
            body = json.dumps(json_body).encode()
            all_headers["Content-Type"] = "application/json"
        try:
            conn.request(method, path, body=body, headers=all_headers)
            resp = conn.getresponse()
            return Response(resp.status, resp.getheaders(), resp.read())
        finally:
            conn.close()

    def get(self, path, **kw):
        return self.request("GET", API + path, **kw)

    def put(self, path, **kw):
        return self.request("PUT", API + path, **kw)

    def post(self, path, **kw):
        return self.request("POST", API + path, **kw)

    def delete(self, path, **kw):
        return self.request("DELETE", API + path, **kw)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def b64(data):
    return base64.b64encode(data).decode()


def start(tmp_path, clock, port=0, **settings):
    config = Config(host="127.0.0.1", port=port, data_dir=str(tmp_path / "data"), **settings)
    server = Server(config, now=clock, log=io.StringIO())
    server.start_in_thread()
    return server


def pair(server, name):
    """Pairs a device through the API; returns an Http with its token, and its id."""
    code = server.registry.create_invite("pair", name=name)
    r = Http(server.port).post("/pair", json_body={"code": code, "name": name})
    assert r.status == 200, r.body
    device = r.json()
    return Http(server.port, device["token"]), device["device_id"]


def item_body(to, data, meta=b"m" * 60, inline=False):
    body = {"to": to, "size": len(data), "sha256": sha256(data), "meta": b64(meta)}
    if inline:
        body["data"] = b64(data)
    return body
