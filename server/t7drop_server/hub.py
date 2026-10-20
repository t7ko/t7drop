"""The queue of items. It lives only in memory; the chunks are in a ChunkStore.

Item states: "uploading" until the sender commits all chunks and their hash matches,
"ready" while the recipient may download it, "delivered" once the recipient acknowledged
(the data is then deleted; a small record stays so that the sender sees the delivery).

Every change that matters to a device raises that device's version number, and wakes the
long polls (GET /sync) waiting for it.
"""

import base64
import collections
import dataclasses
import hashlib
import secrets
import threading
import time

from .protocol import CHUNK_SIZE, chunk_count

UPLOADING, READY, DELIVERED = "uploading", "ready", "delivered"


class ApiError(Exception):
    """A request t7drop refuses: an HTTP status, a short code for clients, and details."""

    def __init__(self, status, code, **details):
        super().__init__(code)
        self.status = status
        self.code = code
        self.details = details


@dataclasses.dataclass
class Item:
    id: str
    sender: str
    recipient: str
    size: int
    sha256: str
    meta: bytes
    created: float
    updated: float
    state: str = UPLOADING
    have: set = dataclasses.field(default_factory=set)
    delivered: float = 0.0

    @property
    def chunks(self):
        return chunk_count(self.size)

    def chunk_length(self, n):
        if n < self.chunks - 1:
            return CHUNK_SIZE
        return self.size - CHUNK_SIZE * (self.chunks - 1)

    def view(self):
        return {"id": self.id, "state": self.state, "have": sorted(self.have)}


class Hub:
    def __init__(self, config, store, now=time.time):
        self.config = config
        self.store = store
        self.now = now
        self.instance = secrets.token_hex(8)
        self.started = now()
        self.cond = threading.Condition(threading.RLock())
        self.items = {}
        self.versions = collections.defaultdict(int)
        self.closed = False

    def _touch(self, *devices):
        for device in devices:
            self.versions[device] += 1
        self.cond.notify_all()

    def _own(self, device, item_id):
        item = self.items.get(item_id)
        if item is None or item.sender != device:
            raise ApiError(404, "unknown_item")
        return item

    def reserved_bytes(self):
        return sum(i.size for i in self.items.values() if i.state != DELIVERED)

    def create(self, device, item_id, to, size, sha256, meta, devices, data=None):
        """Creates an item; the same request again returns the item as it is.

        `devices` are the ids of all paired devices; `data`, if given, is the whole
        content of an item that fits into one chunk, which is then committed at once.
        """
        c = self.config
        with self.cond:
            self._expire()
            item = self.items.get(item_id)
            if item is not None:
                same = (item.recipient, item.size, item.sha256, item.meta) == (to, size, sha256, meta)
                if item.sender != device or not (same or item.state == DELIVERED):
                    raise ApiError(409, "conflict")
            else:
                if to == device or to not in devices:
                    raise ApiError(400, "bad_recipient")
                if size < 1 or size > c.max_item_bytes:
                    raise ApiError(413, "too_large", limit=c.max_item_bytes)
                if len(meta) > c.max_meta_bytes:
                    raise ApiError(413, "meta_too_large", limit=c.max_meta_bytes)
                pending = sum(1 for i in self.items.values() if i.sender == device and i.state != DELIVERED)
                if pending >= c.max_items_per_device:
                    raise ApiError(429, "too_many_items")
                if self.reserved_bytes() + size > c.max_total_bytes:
                    raise ApiError(507, "storage_full")
                now = self.now()
                item = Item(item_id, device, to, size, sha256, meta, now, now)
                self.items[item_id] = item
            if data is not None and item.state == UPLOADING:
                if len(data) != size or item.chunks != 1:
                    raise ApiError(400, "bad_inline_data")
                self._store(item, 0, data)
                self._commit(item)
            return item.view()

    def put_chunk(self, device, item_id, n, data):
        with self.cond:
            item = self._own(device, item_id)
            if item.state != UPLOADING:
                raise ApiError(409, "not_uploading", state=item.state)
            if not 0 <= n < item.chunks:
                raise ApiError(400, "bad_chunk")
            if len(data) != item.chunk_length(n):
                raise ApiError(400, "bad_length", expected=item.chunk_length(n))
            self._store(item, n, data)

    def _store(self, item, n, data):
        self.store.put(item.id, n, data)
        item.have.add(n)
        item.updated = self.now()

    def commit(self, device, item_id):
        with self.cond:
            item = self._own(device, item_id)
            if item.state == UPLOADING:
                missing = [n for n in range(item.chunks) if n not in item.have]
                if missing:
                    raise ApiError(409, "missing_chunks", missing=missing)
                self._commit(item)
            return item.view()

    def _commit(self, item):
        digest = hashlib.sha256()
        for n in range(item.chunks):
            digest.update(self.store.get(item.id, n))
        if digest.hexdigest() != item.sha256:
            self.store.delete(item.id)
            item.have.clear()
            raise ApiError(422, "hash_mismatch")
        item.state = READY
        item.updated = self.now()
        self._touch(item.recipient, item.sender)

    def get_chunk(self, device, item_id, n):
        with self.cond:
            item = self.items.get(item_id)
            if item is None or item.recipient != device or item.state != READY:
                raise ApiError(404, "unknown_item")
            if not 0 <= n < item.chunks:
                raise ApiError(400, "bad_chunk")
            return self.store.get(item.id, n)

    def ack(self, device, item_id):
        with self.cond:
            item = self.items.get(item_id)
            if item is None or item.recipient != device or item.state == UPLOADING:
                raise ApiError(404, "unknown_item")
            if item.state == READY:
                self.store.delete(item.id)
                item.state = DELIVERED
                item.delivered = self.now()
                item.have.clear()
                item.meta = b""
                self._touch(item.sender, item.recipient)
            return item.view()

    def delete(self, device, item_id):
        with self.cond:
            self._drop(self._own(device, item_id))

    def _drop(self, item):
        self.store.delete(item.id)
        del self.items[item.id]
        self._touch(item.sender, item.recipient)

    def snapshot(self, device):
        with self.cond:
            inbox = [{"id": i.id, "from": i.sender, "size": i.size, "sha256": i.sha256,
                      "meta": base64.b64encode(i.meta).decode("ascii"), "created": int(i.created)}
                     for i in self.items.values() if i.recipient == device and i.state == READY]
            outbox = [{"id": i.id, "to": i.recipient, "state": i.state, "have": len(i.have)}
                      for i in self.items.values() if i.sender == device]
            return {"instance": self.instance, "version": self.versions[device],
                    "inbox": inbox, "outbox": outbox}

    def sync(self, device, since, wait):
        """The device's snapshot once its version differs from `since`, or after `wait` seconds."""
        deadline = time.monotonic() + max(0, min(wait, self.config.max_wait_seconds))
        with self.cond:
            self._expire()
            while self.versions[device] == since and not self.closed:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.cond.wait(remaining)
            return self.snapshot(device)

    def close(self):
        with self.cond:
            self.closed = True
            self.cond.notify_all()

    def expire(self):
        with self.cond:
            self._expire()

    def _expire(self):
        c, now = self.config, self.now()
        for item in list(self.items.values()):
            if item.state == UPLOADING:
                old = now - item.updated > c.unfinished_hours * 3600
            elif item.state == READY:
                old = now - item.created > c.undelivered_days * 86400
            else:
                old = now - item.delivered > c.delivered_days * 86400
            if old:
                self._drop(item)

    def status(self):
        with self.cond:
            states = collections.Counter(i.state for i in self.items.values())
            return {"instance": self.instance, "started": int(self.started),
                    "items": dict(states), "reserved_bytes": self.reserved_bytes()}
