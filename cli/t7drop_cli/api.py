"""The t7drop protocol over HTTPS (docs/design.md, "Protocol v1")."""

import base64
import http.client
import json
import ssl
import urllib.parse

INSTANCE_HEADER = "X-T7drop-Instance"


class TransientError(Exception):
    """No answer from t7drop: a network error, or a response without t7drop's header (nginx's
    error page, or an unknown token). Try again later; never drop items because of it."""


class ApiError(Exception):
    """t7drop refused the request."""

    def __init__(self, status, code, details):
        super().__init__("%s (HTTP %d)" % (code, status))
        self.status = status
        self.code = code
        self.details = details

    @property
    def transient(self):
        return self.status >= 500 or self.status in (429, 507)


class Api:
    def __init__(self, url, token=None, timeout=30):
        parts = urllib.parse.urlsplit(url)
        if parts.scheme not in ("https", "http") or not parts.hostname:
            raise ValueError("not an http(s) URL: %r" % url)
        self.url = url
        self.https = parts.scheme == "https"
        self.host = parts.hostname
        self.port = parts.port
        self.base = parts.path.rstrip("/") + "/api/v1"
        self.token = token
        self.timeout = timeout
        self.instance = None
        self._tls = ssl.create_default_context() if self.https else None

    def _connection(self, timeout):
        if self.https:
            return http.client.HTTPSConnection(self.host, self.port, timeout=timeout, context=self._tls)
        return http.client.HTTPConnection(self.host, self.port, timeout=timeout)

    def request(self, method, path, body=None, content_type=None, timeout=None, auth=True):
        headers = {}
        if auth and self.token:
            headers["Authorization"] = "Bearer " + self.token
        if body is not None:
            headers["Content-Type"] = content_type or "application/octet-stream"
        conn = self._connection(timeout or self.timeout)
        try:
            conn.request(method, self.base + path, body=body, headers=headers)
            response = conn.getresponse()
            data = response.read()
            status, instance = response.status, response.getheader(INSTANCE_HEADER)
        except (OSError, http.client.HTTPException) as e:
            raise TransientError("%s: %s" % (type(e).__name__, e)) from e
        finally:
            conn.close()
        if instance is None:
            raise TransientError("HTTP %d without t7drop's header" % status)
        self.instance = instance
        if status >= 400:
            try:
                details = json.loads(data)
            except ValueError:
                details = {}
            raise ApiError(status, details.get("error", "error"), details)
        return data

    def call(self, method, path, payload=None, **kwargs):
        body = json.dumps(payload).encode("utf-8") if payload is not None else None
        data = self.request(method, path, body, "application/json", **kwargs)
        return json.loads(data) if data else {}

    def pair(self, code, name):
        return self.call("POST", "/pair", {"code": code, "name": name}, auth=False)

    def ping(self):
        return self.call("GET", "/ping")

    def invite(self):
        return self.call("POST", "/invites")

    def devices(self):
        return self.call("GET", "/devices")["devices"]

    def create_item(self, item_id, to, size, sha256, meta, data=None):
        payload = {"to": to, "size": size, "sha256": sha256, "meta": base64.b64encode(meta).decode("ascii")}
        if data is not None:
            payload["data"] = base64.b64encode(data).decode("ascii")
        return self.call("PUT", "/items/" + item_id, payload)

    def put_chunk(self, item_id, n, data):
        self.request("PUT", "/items/%s/chunks/%d" % (item_id, n), data, timeout=120)

    def get_chunk(self, item_id, n):
        return self.request("GET", "/items/%s/chunks/%d" % (item_id, n), timeout=120)

    def commit(self, item_id):
        return self.call("POST", "/items/%s/commit" % item_id, timeout=120)

    def ack(self, item_id):
        return self.call("POST", "/items/%s/ack" % item_id)

    def delete(self, item_id):
        return self.call("DELETE", "/items/" + item_id)

    def sync(self, since, wait=0):
        return self.call("GET", "/sync?since=%d&wait=%d" % (since, wait), timeout=wait + 20)
