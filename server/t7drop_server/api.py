"""The HTTP side of the server: routes, authentication, request bodies, responses.

A request without a valid token gets the same 404 page that nginx gives for an unknown
path, without t7drop's headers, so the service does not show itself. Every other
response carries X-T7drop-Instance.
"""

import base64
import binascii
import collections
import json
import re
import sys
import threading
import time
import traceback
import urllib.parse
from http.server import BaseHTTPRequestHandler

from . import protocol
from .hub import ApiError

NOT_FOUND_PAGE = (b"<html>\r\n<head><title>404 Not Found</title></head>\r\n<body>\r\n"
                  b"<center><h1>404 Not Found</h1></center>\r\n<hr><center>nginx</center>\r\n"
                  b"</body>\r\n</html>\r\n")

JSON_LIMIT = 16 * 1024
CREATE_LIMIT = 128 * 1024
MAX_INVITES_PER_DEVICE = 3
INVITE_SECONDS = 600

_ID = r"(?P<id>[0-9a-f-]{36})"
_N = r"(?P<n>[0-9]{1,6})"

# method, path after /api/v1, handler, needs a device token, body limit in bytes
ROUTES = [
    ("POST", r"/pair", "pair", False, JSON_LIMIT),
    ("GET", r"/ping", "ping", True, 0),
    ("POST", r"/invites", "invite", True, JSON_LIMIT),
    ("GET", r"/devices", "devices", True, 0),
    ("GET", r"/sync", "sync", True, 0),
    ("PUT", r"/items/" + _ID, "create", True, CREATE_LIMIT),
    ("DELETE", r"/items/" + _ID, "delete", True, 0),
    ("PUT", r"/items/" + _ID + r"/chunks/" + _N, "put_chunk", True, protocol.CHUNK_SIZE),
    ("GET", r"/items/" + _ID + r"/chunks/" + _N, "get_chunk", True, 0),
    ("POST", r"/items/" + _ID + r"/commit", "commit", True, JSON_LIMIT),
    ("POST", r"/items/" + _ID + r"/ack", "ack", True, JSON_LIMIT),
]
_COMPILED = [(method, re.compile("^" + re.escape(protocol.API_PREFIX) + pattern + "$"), name, auth, limit)
             for method, pattern, name, auth, limit in ROUTES]


class NotFound(Exception):
    """Answer with the site's 404 page, as if t7drop were not there."""


class App:
    """What all request handlers share."""

    def __init__(self, config, registry, hub, log=sys.stderr):
        self.config = config
        self.registry = registry
        self.hub = hub
        self.log = log
        self._failures = collections.deque()
        self._failures_lock = threading.Lock()

    def pairing_allowed(self):
        with self._failures_lock:
            cutoff = time.monotonic() - 3600
            while self._failures and self._failures[0] < cutoff:
                self._failures.popleft()
            return len(self._failures) < self.config.max_failed_pairings

    def pairing_failed(self):
        with self._failures_lock:
            self._failures.append(time.monotonic())


def _query(text):
    return {k: v[-1] for k, v in urllib.parse.parse_qs(text).items()}


def _int(text):
    try:
        return int(text)
    except (TypeError, ValueError):
        raise ApiError(400, "bad_request")


def _json(body):
    try:
        data = json.loads(body.decode("utf-8")) if body else {}
    except (ValueError, UnicodeDecodeError):
        raise ApiError(400, "bad_json")
    if not isinstance(data, dict):
        raise ApiError(400, "bad_json")
    return data


def _b64(value):
    if not isinstance(value, str):
        raise ApiError(400, "bad_request")
    try:
        return base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError):
        raise ApiError(400, "bad_base64")


def _item_id(params):
    if not protocol.is_item_id(params["id"]):
        raise ApiError(400, "bad_item_id")
    return params["id"]


def make_handler(app):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "t7drop"
        sys_version = ""
        timeout = 120

        def log_message(self, format, *args):
            pass

        def do_GET(self):
            self._handle("GET")

        def do_PUT(self):
            self._handle("PUT")

        def do_POST(self):
            self._handle("POST")

        def do_DELETE(self):
            self._handle("DELETE")

        def _handle(self, method):
            started = time.monotonic()
            self.device = None
            name, status = "-", 500
            try:
                try:
                    path, _, query = self.path.partition("?")
                    route, params = self._route(method, path)
                    if route is None:
                        raise NotFound()
                    _, _, name, needs_token, limit = route
                    if needs_token:
                        self.device = app.registry.authenticate(self._credentials("bearer"))
                        if self.device is None and not (name == "ping" and self._check_code()):
                            raise NotFound()
                    body = self._body(limit)
                    status, payload = getattr(self, "r_" + name)(params, _query(query), body)
                    self._send(status, payload)
                except NotFound:
                    status = 404
                    self._send_not_found()
                except ApiError as e:
                    status = e.status
                    self._send(e.status, dict(e.details, error=e.code))
                except (ConnectionError, TimeoutError):
                    status = 499
                    self.close_connection = True
                except Exception:
                    status = 500
                    app.log.write(traceback.format_exc())
                    self.close_connection = True
                    if self.device is not None:
                        self._send(500, {"error": "internal"})
                    else:
                        self._send_not_found()
            except OSError:
                self.close_connection = True
            finally:
                who = self.device["name"] if self.device else "-"
                app.log.write("%s %s %d %s %.3fs\n" % (method, name, status, who, time.monotonic() - started))
                app.log.flush()

        def _route(self, method, path):
            for route in _COMPILED:
                if route[0] == method:
                    match = route[1].match(path)
                    if match:
                        return route, match.groupdict()
            return None, None

        def _credentials(self, scheme):
            kind, _, value = self.headers.get("Authorization", "").partition(" ")
            return value.strip() if kind.lower() == scheme else None

        def _check_code(self):
            code = self._credentials("check")
            return bool(code) and app.registry.redeem_invite(code, "check") is not None

        def _body(self, limit):
            if self.headers.get("Transfer-Encoding"):
                self.close_connection = True
                raise ApiError(411, "length_required")
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                self.close_connection = True
                raise ApiError(400, "bad_length")
            if length < 0 or length > limit:
                self.close_connection = True
                raise ApiError(413, "body_too_large", limit=limit)
            data = b""
            while len(data) < length:
                part = self.rfile.read(length - len(data))
                if not part:
                    raise ConnectionError("request body cut short")
                data += part
            return data

        def _send(self, status, payload):
            if isinstance(payload, bytes):
                body, content_type = payload, "application/octet-stream"
            else:
                body, content_type = json.dumps(payload).encode("utf-8"), "application/json"
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header(protocol.INSTANCE_HEADER, app.hub.instance)
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)

        def _send_not_found(self):
            if (self.headers.get("Content-Length") or "0") != "0":
                self.close_connection = True  # the body was not read
            self.send_response(404)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(NOT_FOUND_PAGE)))
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(NOT_FOUND_PAGE)

        # Routes: each returns (status, JSON-able dict or bytes).

        def r_pair(self, params, query, body):
            data = _json(body)
            if not app.pairing_allowed():
                raise NotFound()
            invite = app.registry.redeem_invite(str(data.get("code", "")), "pair")
            if invite is None:
                app.pairing_failed()
                raise NotFound()
            device, token = app.registry.add_device(data.get("name") or invite.get("name") or "device",
                                                    can_invite=invite.get("created_by") == "admin")
            self.device = device
            return 200, {"device_id": device["id"], "name": device["name"], "token": token}

        def r_ping(self, params, query, body):
            return 200, {"ok": True, "device": self.device["id"] if self.device else None}

        def r_invite(self, params, query, body):
            if not self.device["can_invite"]:
                raise ApiError(403, "not_allowed")
            if app.registry.active_invites(self.device["id"]) >= MAX_INVITES_PER_DEVICE:
                raise ApiError(429, "too_many_invites")
            code = app.registry.create_invite("pair", created_by=self.device["id"], ttl=INVITE_SECONDS)
            return 200, {"code": code, "expires_in": INVITE_SECONDS}

        def r_devices(self, params, query, body):
            return 200, {"devices": app.registry.devices()}

        def r_sync(self, params, query, body):
            since = _int(query.get("since", "-1"))
            wait = _int(query.get("wait", "0"))
            return 200, app.hub.sync(self.device["id"], since, wait)

        def r_create(self, params, query, body):
            item_id = _item_id(params)
            data = _json(body)
            to, size, sha256 = data.get("to"), data.get("size"), data.get("sha256")
            if not isinstance(to, str) or type(size) is not int or not protocol.is_sha256(sha256):
                raise ApiError(400, "bad_request")
            meta = _b64(data.get("meta"))
            inline = _b64(data["data"]) if data.get("data") is not None else None
            devices = {d["id"] for d in app.registry.devices()}
            return 200, app.hub.create(self.device["id"], item_id, to, size, sha256, meta, devices, inline)

        def r_delete(self, params, query, body):
            item_id = _item_id(params)
            app.hub.delete(self.device["id"], item_id)
            return 200, {"id": item_id, "state": "deleted"}

        def r_put_chunk(self, params, query, body):
            app.hub.put_chunk(self.device["id"], _item_id(params), int(params["n"]), body)
            return 200, {"ok": True}

        def r_get_chunk(self, params, query, body):
            return 200, app.hub.get_chunk(self.device["id"], _item_id(params), int(params["n"]))

        def r_commit(self, params, query, body):
            return 200, app.hub.commit(self.device["id"], _item_id(params))

        def r_ack(self, params, query, body):
            return 200, app.hub.ack(self.device["id"], _item_id(params))

    return Handler
