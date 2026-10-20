"""End-to-end encryption of items, as docs/design.md describes: libsodium secretstream
(XChaCha20-Poly1305) under the group key, through PyNaCl.

An item is two streams. The meta stream is one message, tagged FINAL, holding JSON. The
content stream holds the content in messages of MESSAGE_BYTES of plaintext, the last one
(possibly shorter, possibly empty) tagged FINAL. Every message carries the associated
data "t7drop/1/meta/<item id>" or "t7drop/1/data/<item id>".
"""

import json

import nacl.bindings as sodium
import nacl.exceptions

KEY_BYTES = sodium.crypto_secretstream_xchacha20poly1305_KEYBYTES
HEADER_BYTES = sodium.crypto_secretstream_xchacha20poly1305_HEADERBYTES
A_BYTES = sodium.crypto_secretstream_xchacha20poly1305_ABYTES
TAG_MESSAGE = sodium.crypto_secretstream_xchacha20poly1305_TAG_MESSAGE
TAG_FINAL = sodium.crypto_secretstream_xchacha20poly1305_TAG_FINAL
MESSAGE_BYTES = 1024 * 1024


class DecryptError(Exception):
    """The stream is not intact: wrong key or item, altered, cut short, or extended."""


def new_key():
    return sodium.crypto_secretstream_xchacha20poly1305_keygen()


def _ad(part, item_id):
    return ("t7drop/1/%s/%s" % (part, item_id)).encode("ascii")


def _state():
    return sodium.crypto_secretstream_xchacha20poly1305_state()


def _read_full(src, n):
    data = b""
    while len(data) < n:
        part = src.read(n - len(data))
        if not part:
            break
        data += part
    return data


def sealed_size(plain_size):
    messages = max(1, -(-plain_size // MESSAGE_BYTES))
    return HEADER_BYTES + plain_size + messages * A_BYTES


def seal_meta(key, item_id, meta):
    state = _state()
    header = sodium.crypto_secretstream_xchacha20poly1305_init_push(state, key)
    text = json.dumps(meta, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return header + sodium.crypto_secretstream_xchacha20poly1305_push(state, text, _ad("meta", item_id), TAG_FINAL)


def open_meta(key, item_id, blob):
    if len(blob) < HEADER_BYTES + A_BYTES:
        raise DecryptError("meta too short")
    state = _state()
    try:
        sodium.crypto_secretstream_xchacha20poly1305_init_pull(state, blob[:HEADER_BYTES], key)
        text, tag = sodium.crypto_secretstream_xchacha20poly1305_pull(state, blob[HEADER_BYTES:], _ad("meta", item_id))
    except nacl.exceptions.CryptoError as e:
        raise DecryptError("meta does not decrypt") from e
    if tag != TAG_FINAL:
        raise DecryptError("meta is not one final message")
    try:
        meta = json.loads(text.decode("utf-8"))
    except ValueError as e:
        raise DecryptError("meta is not JSON") from e
    if not isinstance(meta, dict):
        raise DecryptError("meta is not a JSON object")
    return meta


def seal_stream(key, item_id, src, dst):
    """Encrypts all that src gives into dst; returns the plaintext size."""
    state = _state()
    dst.write(sodium.crypto_secretstream_xchacha20poly1305_init_push(state, key))
    ad = _ad("data", item_id)
    total = 0
    block = _read_full(src, MESSAGE_BYTES)
    while True:
        following = _read_full(src, MESSAGE_BYTES) if len(block) == MESSAGE_BYTES else b""
        last = not following
        dst.write(sodium.crypto_secretstream_xchacha20poly1305_push(state, block, ad, TAG_FINAL if last else TAG_MESSAGE))
        total += len(block)
        if last:
            return total
        block = following


def open_stream(key, item_id, src, dst):
    """Decrypts src into dst; returns the plaintext size. Raises DecryptError on any damage,
    possibly after writing part of the plaintext: write to a temporary file."""
    header = _read_full(src, HEADER_BYTES)
    if len(header) != HEADER_BYTES:
        raise DecryptError("stream too short")
    state = _state()
    try:
        sodium.crypto_secretstream_xchacha20poly1305_init_pull(state, header, key)
    except nacl.exceptions.CryptoError as e:
        raise DecryptError("stream header refused") from e
    ad = _ad("data", item_id)
    total = 0
    while True:
        message = _read_full(src, MESSAGE_BYTES + A_BYTES)
        if len(message) < A_BYTES:
            raise DecryptError("stream cut short")
        try:
            block, tag = sodium.crypto_secretstream_xchacha20poly1305_pull(state, message, ad)
        except nacl.exceptions.CryptoError as e:
            raise DecryptError("stream does not decrypt") from e
        dst.write(block)
        total += len(block)
        if tag == TAG_FINAL:
            if src.read(1):
                raise DecryptError("data after the final message")
            return total
        if tag != TAG_MESSAGE or len(message) != MESSAGE_BYTES + A_BYTES:
            raise DecryptError("stream cut short")
