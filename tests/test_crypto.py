"""End-to-end encryption, and the test vectors in testdata/vectors."""

import hashlib
import io
import json
import os

import pytest

import helpers
from t7drop_cli import crypto
from t7drop_cli.pairing import make_uri, parse_uri

M = crypto.MESSAGE_BYTES
ITEM = "4f0dbea2-6e87-4e9d-91a2-cd5a7e5b6c05"
VECTORS = os.path.join(helpers.ROOT, "testdata", "vectors")


def seal(key, item_id, plain):
    out = io.BytesIO()
    assert crypto.seal_stream(key, item_id, io.BytesIO(plain), out) == len(plain)
    return out.getvalue()


def unseal(key, item_id, blob):
    out = io.BytesIO()
    crypto.open_stream(key, item_id, io.BytesIO(blob), out)
    return out.getvalue()


@pytest.mark.parametrize("size", [0, 1, 1000, M - 1, M, M + 1, 3 * M + 5])
def test_streams_round_trip(size):
    key = crypto.new_key()
    plain = os.urandom(size)
    blob = seal(key, ITEM, plain)
    assert len(blob) == crypto.sealed_size(size)
    assert unseal(key, ITEM, blob) == plain


def test_damaged_streams_are_refused():
    key = crypto.new_key()
    blob = seal(key, ITEM, os.urandom(2 * M + 10))
    one_message = crypto.HEADER_BYTES + M + crypto.A_BYTES
    damaged = [
        blob[:one_message],                     # cut after a whole message
        blob[:-1],                              # last byte missing
        blob[:5],                               # header cut
        blob + b"\0",                           # extended
        blob[:100] + bytes([blob[100] ^ 1]) + blob[101:],
    ]
    for bad in damaged:
        with pytest.raises(crypto.DecryptError):
            unseal(key, ITEM, bad)
    with pytest.raises(crypto.DecryptError):
        unseal(crypto.new_key(), ITEM, blob)
    with pytest.raises(crypto.DecryptError):
        unseal(key, "5a1ecfb3-7f98-4fae-a2b3-de6b8f6c7d06", blob)


def test_meta_round_trip_and_binding():
    key = crypto.new_key()
    meta = {"v": 1, "kind": "file", "name": "\u0444\u0430\u0439\u043b.txt", "size": 3}
    blob = crypto.seal_meta(key, ITEM, meta)
    assert crypto.open_meta(key, ITEM, blob) == meta
    with pytest.raises(crypto.DecryptError):
        crypto.open_meta(key, "5a1ecfb3-7f98-4fae-a2b3-de6b8f6c7d06", blob)
    with pytest.raises(crypto.DecryptError):
        crypto.open_meta(key, ITEM, seal(key, ITEM, b"{}"))  # content AD, not meta


def test_pairing_invitation_round_trip():
    key = crypto.new_key()
    uri = make_uri("https://example.org/abc", "ABCDE23456", key)
    assert parse_uri(uri) == ("https://example.org/abc", "ABCDE23456", key)
    for bad in ("https://example.org", "t7drop:pair?v=2&u=x&c=y&k=z", uri.replace("k=", "x=")):
        with pytest.raises(ValueError):
            parse_uri(bad)


def _vectors():
    with open(os.path.join(VECTORS, "vectors.json"), encoding="utf-8") as f:
        return json.load(f)


def _read(name):
    with open(os.path.join(VECTORS, name), "rb") as f:
        return f.read()


def test_the_vectors_decrypt():
    v = _vectors()
    key = bytes.fromhex(v["key_hex"])
    assert v["message_bytes"] == M
    for item in v["items"]:
        assert crypto.open_meta(key, item["id"], _read(item["meta_file"])) == item["meta"]
        plain = unseal(key, item["id"], _read(item["data_file"]))
        assert len(plain) == item["plain_size"]
        assert hashlib.sha256(plain).hexdigest() == item["plain_sha256"]
        if "plain_text" in item:
            assert plain.decode("utf-8") == item["plain_text"]


def test_the_vector_changes_are_refused():
    v = _vectors()
    key = bytes.fromhex(v["key_hex"])
    items = {i["name"]: i for i in v["items"]}
    for case in v["must_fail"]:
        item = items[case["item"]]
        blob = _read(item[case["file"] + "_file"])
        item_id = item["id"]
        change = case["change"]
        if change == "truncate":
            blob = blob[:case["length"]]
        elif change == "flip":
            blob = blob[:case["offset"]] + bytes([blob[case["offset"]] ^ 1]) + blob[case["offset"] + 1:]
        elif change == "append":
            blob = blob + bytes([case["byte"]])
        elif change == "item_id":
            item_id = case["id"]
        elif change == "swap":
            blob = _read(item[case["with"] + "_file"])
        with pytest.raises(crypto.DecryptError):
            if case["file"] == "meta":
                crypto.open_meta(key, item_id, blob)
            else:
                unseal(key, item_id, blob)
