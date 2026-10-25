"""Paired devices and one-time codes, kept in two JSON files in the data folder.

Only devices paired with a code from the admin command may invite further devices (in
practice: the first device); a device invited by another device may not.

Both the server and the admin command (admin.py) change these files: under a lock file,
always replacing a file as a whole. The server reads devices.json again whenever the
file changes, so a device revoked with the admin command loses access with its next
request. Tokens and codes are stored only as SHA-256 hashes.
"""

import contextlib
import hashlib
import json
import os
import secrets
import threading
import time

CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"  # no 0, 1, I, L, O
CODE_LENGTH = 10
MAX_NAME = 40


def normalize_code(code):
    return "".join(c for c in str(code).upper() if c in CODE_ALPHABET)


def format_code(code):
    return code[:5] + "-" + code[5:]


def clean_name(name, fallback="device"):
    text = " ".join("".join(c for c in str(name or "") if c.isprintable()).split())
    return text[:MAX_NAME] or fallback


def _hash(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _read_json(path, default):
    try:
        with open(path, "rb") as f:
            return json.loads(f.read().decode("utf-8"))
    except FileNotFoundError:
        return default


def _write_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(json.dumps(data, indent=1, sort_keys=True).encode("utf-8"))
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


@contextlib.contextmanager
def _file_lock(path):
    with open(path, "a+b") as f:
        if os.name == "nt":
            import msvcrt
            while True:
                try:
                    f.seek(0)
                    msvcrt.locking(f.fileno(), msvcrt.LK_LOCK, 1)
                    break
                except OSError:
                    pass
            try:
                yield
            finally:
                f.seek(0)
                msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(f.fileno(), fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(f.fileno(), fcntl.LOCK_UN)


class Registry:
    def __init__(self, data_dir, now=time.time):
        self.now = now
        self.devices_path = os.path.join(data_dir, "devices.json")
        self.invites_path = os.path.join(data_dir, "invites.json")
        self.lock_path = os.path.join(data_dir, "registry.lock")
        self._mutex = threading.Lock()
        self._stamp = False
        self._devices = []
        self._by_hash = {}

    @contextlib.contextmanager
    def _changing(self):
        with self._mutex, _file_lock(self.lock_path):
            try:
                yield
            finally:
                self._stamp = False

    def _refresh(self):
        try:
            st = os.stat(self.devices_path)
            stamp = (st.st_mtime_ns, st.st_size)
        except FileNotFoundError:
            stamp = None
        if stamp != self._stamp:
            self._devices = _read_json(self.devices_path, {"devices": []})["devices"]
            self._by_hash = {d["token_sha256"]: d for d in self._devices}
            self._stamp = stamp

    def authenticate(self, token):
        """The device a token belongs to, as {"id", "name", "can_invite"}, or None."""
        if not token:
            return None
        with self._mutex:
            self._refresh()
            device = self._by_hash.get(_hash(token))
        if device is None:
            return None
        return {"id": device["id"], "name": device["name"], "can_invite": device.get("can_invite", False)}

    def devices(self):
        with self._mutex:
            self._refresh()
            return [{"id": d["id"], "name": d["name"], "created": d["created"],
                     "can_invite": d.get("can_invite", False)} for d in self._devices]

    def add_device(self, name, can_invite=False):
        """Registers a device; returns it as {"id", "name"} and its new token."""
        token = secrets.token_urlsafe(32)
        device = {"id": secrets.token_hex(6), "name": clean_name(name), "can_invite": bool(can_invite),
                  "token_sha256": _hash(token), "created": int(self.now())}
        with self._changing():
            data = _read_json(self.devices_path, {"devices": []})
            data["devices"].append(device)
            _write_json(self.devices_path, data)
        return {"id": device["id"], "name": device["name"]}, token

    def revoke(self, ident):
        """Removes the device with this id, or the one device with this name; returns it, or None."""
        with self._changing():
            data = _read_json(self.devices_path, {"devices": []})
            found = [d for d in data["devices"] if d["id"] == ident]
            found = found or [d for d in data["devices"] if d["name"] == ident]
            if len(found) > 1:
                raise ValueError("%d devices are named %r; give the id" % (len(found), ident))
            if not found:
                return None
            data["devices"].remove(found[0])
            _write_json(self.devices_path, data)
        return {"id": found[0]["id"], "name": found[0]["name"]}

    def create_invite(self, kind="pair", name=None, created_by="admin", ttl=600):
        """A new one-time code: "pair" pairs a device, "check" answers one GET /ping."""
        code = "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))
        entry = {"code_sha256": _hash(code), "kind": kind, "name": clean_name(name, "") or None,
                 "created_by": created_by, "expires": int(self.now() + ttl)}
        with self._changing():
            invites = self._live_invites()
            invites.append(entry)
            _write_json(self.invites_path, {"invites": invites})
        return code

    def active_invites(self, created_by):
        with self._mutex, _file_lock(self.lock_path):
            return sum(1 for i in self._live_invites() if i["created_by"] == created_by)

    def redeem_invite(self, code, kind):
        """Uses up a live code of this kind; returns its entry, or None.

        The file is written only when a code is used up or has expired, so that wrong codes
        (anyone may send them) cost no disk writes.
        """
        code_hash = _hash(normalize_code(code))
        with self._changing():
            stored = _read_json(self.invites_path, {"invites": []})["invites"]
            invites = self._live(stored)
            match = next((i for i in invites if i["code_sha256"] == code_hash and i["kind"] == kind), None)
            if match is not None:
                invites.remove(match)
            if len(invites) != len(stored):
                _write_json(self.invites_path, {"invites": invites})
        return match

    def _live_invites(self):
        return self._live(_read_json(self.invites_path, {"invites": []})["invites"])

    def _live(self, invites):
        now = self.now()
        return [i for i in invites if i["expires"] > now]
