"""Writes testdata/vectors/: encrypted items that every t7drop client must decrypt, and
changes to them that it must refuse (vectors.json lists both). Run it once; the files are
committed. Encryption is randomized, so another run gives other files, equally valid.

  python tests/make_vectors.py
"""

import hashlib
import io
import json
import os
import sys

import helpers  # noqa: F401  (puts cli/ on the path)
from t7drop_cli import crypto

OUT = os.path.join(helpers.ROOT, "testdata", "vectors")
KEY = bytes(range(32))
SENDER, RECIPIENT = "aaaaaaaaaaaa", "bbbbbbbbbbbb"


def pattern(n):
    return bytes((i * 7 + 3) % 256 for i in range(n))


def main():
    os.makedirs(OUT, exist_ok=True)
    text = "Hello, t7drop! \u041f\u0440\u0438\u0432\u0435\u0442 \U0001f44b\nSecond line."
    items = [
        ("text", "0b6f7a6e-2a43-4a59-9d6e-8f1c3a1d2e01", "text", None, text.encode("utf-8")),
        ("empty", "1c7a8b7f-3b54-4b6a-8e7f-9a2d4b2e3f02", "file", "empty.txt", b""),
        ("two-messages", "2d8b9c80-4c65-4c7b-9f80-ab3e5c3f4a03", "file", "pattern.bin",
         pattern(crypto.MESSAGE_BYTES + 1)),
    ]
    listed = []
    for name, item_id, kind, file_name, plain in items:
        meta = {"v": 1, "kind": kind, "mime": "text/plain; charset=utf-8" if kind == "text" else "application/octet-stream",
                "size": len(plain), "from": SENDER, "to": RECIPIENT, "created": 1800000000000}
        if file_name:
            meta["name"] = file_name
        with open(os.path.join(OUT, name + ".meta"), "wb") as f:
            f.write(crypto.seal_meta(KEY, item_id, meta))
        src = io.BytesIO(plain)
        with open(os.path.join(OUT, name + ".data"), "wb") as f:
            crypto.seal_stream(KEY, item_id, src, f)
        entry = {"name": name, "id": item_id, "meta_file": name + ".meta", "data_file": name + ".data",
                 "meta": meta, "plain_size": len(plain), "plain_sha256": hashlib.sha256(plain).hexdigest()}
        if kind == "text":
            entry["plain_text"] = text
        listed.append(entry)
    vectors = {
        "about": "t7drop protocol 1 test vectors: see docs/design.md, Encryption. "
                 "pattern.bin is byte i = (i * 7 + 3) % 256.",
        "key_hex": KEY.hex(),
        "message_bytes": crypto.MESSAGE_BYTES,
        "items": listed,
        "must_fail": [
            {"item": "two-messages", "file": "data", "change": "truncate",
             "length": crypto.HEADER_BYTES + crypto.MESSAGE_BYTES + crypto.A_BYTES,
             "why": "the final message is missing"},
            {"item": "two-messages", "file": "data", "change": "flip", "offset": 1000, "why": "altered"},
            {"item": "two-messages", "file": "data", "change": "append", "byte": 0, "why": "data after FINAL"},
            {"item": "text", "file": "data", "change": "item_id", "id": "3e9cad91-5d76-4d8c-8091-bc4f6d4a5b04",
             "why": "associated data of another item"},
            {"item": "text", "file": "meta", "change": "item_id", "id": "3e9cad91-5d76-4d8c-8091-bc4f6d4a5b04",
             "why": "associated data of another item"},
            {"item": "text", "file": "meta", "change": "swap", "with": "data",
             "why": "a content stream used as meta"},
        ],
    }
    with open(os.path.join(OUT, "vectors.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(vectors, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("Wrote", OUT)


if __name__ == "__main__":
    sys.exit(main())
