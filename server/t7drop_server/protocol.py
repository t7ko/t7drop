"""Constants of protocol version 1, shared with the clients (docs/design.md)."""

import re

API_PREFIX = "/api/v1"
CHUNK_SIZE = 4 * 1024 * 1024
INLINE_MAX = 64 * 1024
INSTANCE_HEADER = "X-T7drop-Instance"

_ITEM_ID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


def is_item_id(value):
    """A lowercase random UUID, as clients make them; safe as a file name."""
    return isinstance(value, str) and bool(_ITEM_ID.match(value))


def is_sha256(value):
    return isinstance(value, str) and bool(_SHA256.match(value))


def chunk_count(size):
    return max(1, -(-size // CHUNK_SIZE))
