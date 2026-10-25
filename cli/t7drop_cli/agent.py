"""What a client does: encrypts and queues items, uploads them, receives, acknowledges.

The rules of docs/design.md ("Reliability"): the item id is made when the item is queued,
so retries never create a second item; the ciphertext stays until the recipient has
acknowledged; an item the server no longer has (after a restart) is uploaded again;
received ids are remembered, so nothing is saved twice.
"""

import base64
import hashlib
import io
import mimetypes
import os
import random
import time
import uuid

from . import crypto
from .api import ApiError, TransientError
from .files import safe_name, unique_path
from .state import write_json

CHUNK_SIZE = 4 * 1024 * 1024
INLINE_MAX = 64 * 1024
GIVE_UP_SECONDS = 7 * 86400
KEEP_DELIVERED_SECONDS = 30 * 86400  # then a delivered item leaves the outbox, as in the apps

QUEUED, UPLOADING, ON_SERVER, DELIVERED, FAILED = "queued", "uploading", "on_server", "delivered", "failed"


class _CountingWriter:
    def __init__(self, f):
        self.f = f
        self.sha256 = hashlib.sha256()
        self.count = 0

    def write(self, data):
        self.f.write(data)
        self.sha256.update(data)
        self.count += len(data)


def human_size(n):
    for unit in ("bytes", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return ("%d %s" if unit == "bytes" else "%.1f %s") % (n, unit)
        n /= 1024.0


class Agent:
    def __init__(self, profile, api, download_dir, out=print, now=time.time):
        self.profile = profile
        self.api = api
        self.download_dir = download_dir
        self.out = out
        self.now = now
        self.instance = None
        self.version = -1
        self.interrupted = False  # an upload met a changed server: retry without a long poll

    # Queueing

    def queue_file(self, path, to):
        meta = {"kind": "file", "name": os.path.basename(path),
                "mime": mimetypes.guess_type(path)[0] or "application/octet-stream"}
        with open(path, "rb") as src:
            return self._queue(to, meta, src)

    def queue_text(self, text, to):
        meta = {"kind": "text", "mime": "text/plain; charset=utf-8"}
        return self._queue(to, meta, io.BytesIO(text.encode("utf-8")))

    def _queue(self, to, meta, src):
        item_id = str(uuid.uuid4())
        key = self.profile.key
        folder = self.profile.new_out_item(item_id)
        with open(os.path.join(folder, "data.bin"), "wb") as f:
            writer = _CountingWriter(f)
            size = crypto.seal_stream(key, item_id, src, writer)
        meta = dict(meta, v=1, size=size, to=to, created=int(self.now() * 1000))
        meta["from"] = self.profile.device_id
        with open(os.path.join(folder, "meta.bin"), "wb") as f:
            f.write(crypto.seal_meta(key, item_id, meta))
        item = {"id": item_id, "to": to, "kind": meta["kind"], "name": meta.get("name"), "size": size,
                "csize": writer.count, "sha256": writer.sha256.hexdigest(), "created": self.now(),
                "state": QUEUED, "error": None}
        write_json(os.path.join(folder, "item.json"), item)
        return item_id

    # Sending

    def send_pending(self):
        for item in self.profile.outbox():
            if item.state not in (QUEUED, UPLOADING):
                continue
            if self.now() - item.data["created"] > GIVE_UP_SECONDS:
                item.set_state(FAILED, "expired")
                self.out("Not delivered in 7 days, given up: %s" % item.label)
                continue
            self._upload(item)

    def _upload(self, item):
        d = item.data
        with open(item.meta_path, "rb") as f:
            meta = f.read()
        inline = None
        if d["csize"] <= INLINE_MAX:
            with open(item.data_path, "rb") as f:
                inline = f.read()
        try:
            view = self.api.create_item(d["id"], d["to"], d["csize"], d["sha256"], meta, inline)
            if view["state"] == "uploading":
                if item.state != UPLOADING:
                    item.set_state(UPLOADING)
                have = set(view["have"])
                with open(item.data_path, "rb") as f:
                    for n in range(max(1, -(-d["csize"] // CHUNK_SIZE))):
                        if n not in have:
                            f.seek(n * CHUNK_SIZE)
                            self.api.put_chunk(d["id"], n, f.read(CHUNK_SIZE))
                view = self.api.commit(d["id"])
        except ApiError as e:
            if e.transient:
                raise TransientError(str(e)) from e
            if e.code in ("unknown_item", "not_uploading", "missing_chunks", "hash_mismatch"):
                self.interrupted = True  # it changed under us (a server restart, another client copy)
                return
            item.set_state(FAILED, e.code)
            self.out("The server refused %s: %s" % (item.label, e.code))
            return
        self._apply_state(item, view["state"])

    def _apply_state(self, item, state):
        if state == "delivered" and item.state != DELIVERED:
            item.set_state(DELIVERED)
            item.drop_payload()
            self.out("Delivered: %s" % item.label)
        elif state == "ready" and item.state not in (ON_SERVER, DELIVERED):
            item.set_state(ON_SERVER)

    # Receiving

    def sync(self, wait=0):
        since = self.version if self.instance == self.api.instance else -1
        snapshot = self.api.sync(since, wait)
        self.instance = snapshot["instance"]
        self.version = snapshot["version"]
        self._apply_outbox(snapshot["outbox"])
        for entry in snapshot["inbox"]:
            self._receive(entry)
        self.profile.drop_stale_parts({e["id"] for e in snapshot["inbox"]}, self.now(), self.download_dir)

    def _apply_outbox(self, entries):
        on_server = {e["id"]: e for e in entries}
        for item in self.profile.outbox():
            if item.state == DELIVERED and self.now() - item.data.get("changed", item.data["created"]) > KEEP_DELIVERED_SECONDS:
                self.profile.forget(item)
                continue
            if item.state in (DELIVERED, FAILED):
                continue
            entry = on_server.get(item.id)
            if entry is not None:
                self._apply_state(item, entry["state"])
            elif item.state == ON_SERVER:
                item.set_state(QUEUED)  # the server lost it (a restart): upload it again

    def _receive(self, entry):
        item_id = entry["id"]
        if item_id in self.profile.refused():
            return
        if item_id in self.profile.received():
            self._ack(item_id)
            return
        key = self.profile.key
        try:
            meta = crypto.open_meta(key, item_id, base64.b64decode(entry["meta"]))
        except (crypto.DecryptError, ValueError):
            self.profile.mark_refused(item_id, self.now())
            self.out("Item %s does not decrypt with this device's key; not saved." % item_id)
            return
        if meta.get("to") != self.profile.device_id:
            self.profile.mark_refused(item_id, self.now())
            self.out("Item %s is addressed to another device; not saved." % item_id)
            return
        part = self._download(entry)
        try:
            if meta.get("kind") == "text":
                self._save_text(item_id, meta, part)
            else:
                self._save_file(item_id, meta, part)
        except crypto.DecryptError as e:
            self.profile.mark_refused(item_id, self.now())
            self.out("Item %s did not decrypt (%s); not saved." % (item_id, e))
            return
        finally:
            if os.path.exists(part):
                os.remove(part)
        self.profile.mark_received(item_id, self.now())
        self._ack(item_id)

    def _download(self, entry):
        """Downloads the ciphertext, resuming a partial download; returns its file."""
        os.makedirs(self.profile.incoming_dir, exist_ok=True)
        part = os.path.join(self.profile.incoming_dir, entry["id"] + ".part")
        chunks = max(1, -(-entry["size"] // CHUNK_SIZE))
        done = os.path.getsize(part) // CHUNK_SIZE if os.path.exists(part) else 0
        with open(part, "r+b" if os.path.exists(part) else "wb") as f:
            f.truncate(done * CHUNK_SIZE)
            f.seek(done * CHUNK_SIZE)
            for n in range(done, chunks):
                try:
                    f.write(self.api.get_chunk(entry["id"], n))
                except ApiError as e:
                    raise TransientError(str(e)) from e
                f.flush()
        digest = hashlib.sha256()
        with open(part, "rb") as f:
            for block in iter(lambda: f.read(1024 * 1024), b""):
                digest.update(block)
        if digest.hexdigest() != entry["sha256"]:
            os.remove(part)
            raise TransientError("the download of %s does not match its hash" % entry["id"])
        return part

    def _save_file(self, item_id, meta, part):
        os.makedirs(self.download_dir, exist_ok=True)
        name = safe_name(meta.get("name"))
        tmp = os.path.join(self.download_dir, ".%s.%s.t7drop-part" % (name, item_id[:8]))
        try:
            with open(part, "rb") as src, open(tmp, "wb") as dst:
                size = crypto.open_stream(self.profile.key, item_id, src, dst)
            target = unique_path(self.download_dir, name)
            os.rename(tmp, target)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)
        self.out("Received %s (%s)" % (target, human_size(size)))

    def _save_text(self, item_id, meta, part):
        buffer = io.BytesIO()
        with open(part, "rb") as src:
            crypto.open_stream(self.profile.key, item_id, src, buffer)
        text = buffer.getvalue().decode("utf-8", errors="replace")
        self.profile.add_text(meta.get("from"), text, self.now())
        self.out("Text received:\n%s" % text)

    def _ack(self, item_id):
        try:
            self.api.ack(item_id)
        except ApiError as e:
            if e.code != "unknown_item":
                raise TransientError(str(e)) from e

    # Both

    def run(self, watch=False, wait=50, sleep=time.sleep):
        """Sends what is queued and receives what is waiting; with watch, forever."""
        delay = 1.0
        while True:
            try:
                self.interrupted = False
                self.send_pending()
                self.sync(wait if watch and not self.interrupted else 0)
                self.send_pending()
                delay = 1.0
                if not watch:
                    return
            except (TransientError, ApiError) as e:
                if not watch:
                    raise
                pause = delay * random.uniform(0.5, 1.0)
                self.out("Server not reachable (%s); trying again in %.0f s" % (e, pause))
                sleep(pause)
                delay = min(delay * 2, 300.0)
