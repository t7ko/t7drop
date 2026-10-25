"""Chunk files on disk, encrypted with a key that exists only in this process's memory.

Each chunk is AES-256-GCM with a random nonce and the item id and chunk number as
associated data. When the process ends, the key is gone and the files are unreadable;
a new ChunkStore deletes them.
"""

import os
import shutil

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

NONCE_BYTES = 12


class ChunkStore:
    def __init__(self, root):
        self.root = root
        self._aead = AESGCM(AESGCM.generate_key(bit_length=256))
        self.wipe()

    def wipe(self):
        shutil.rmtree(self.root, ignore_errors=True)
        os.makedirs(self.root, mode=0o700, exist_ok=True)

    def _folder(self, item_id):
        return os.path.join(self.root, item_id)

    @staticmethod
    def _aad(item_id, n):
        return ("%s/%d" % (item_id, n)).encode("ascii")

    def put(self, item_id, n, data):
        folder = self._folder(item_id)
        os.makedirs(folder, mode=0o700, exist_ok=True)
        nonce = os.urandom(NONCE_BYTES)
        path = os.path.join(folder, str(n))
        with open(path + ".tmp", "wb") as f:
            f.write(nonce + self._aead.encrypt(nonce, data, self._aad(item_id, n)))
        os.replace(path + ".tmp", path)

    def get(self, item_id, n):
        with open(os.path.join(self._folder(item_id), str(n)), "rb") as f:
            blob = f.read()
        return self._aead.decrypt(blob[:NONCE_BYTES], blob[NONCE_BYTES:], self._aad(item_id, n))

    def delete(self, item_id):
        shutil.rmtree(self._folder(item_id), ignore_errors=True)

    def self_test(self):
        """Stores, reads and deletes one chunk: the server should not start when the
        encryption or the folder fails (under a new sandbox setting, say)."""
        data = os.urandom(1024 * 1024)
        self.put(SELF_TEST_ID, 0, data)
        try:
            if self.get(SELF_TEST_ID, 0) != data:
                raise RuntimeError("the chunk store's self-test read back other bytes")
        finally:
            self.delete(SELF_TEST_ID)


SELF_TEST_ID = "00000000-0000-4000-8000-000000000000"  # no client makes it: random UUIDs only
