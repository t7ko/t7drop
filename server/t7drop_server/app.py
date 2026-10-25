"""Puts the server together: registry, chunk store, queue, HTTP server, janitor."""

import json
import os
import sys
import threading
import time
import traceback
from http.server import ThreadingHTTPServer

from .api import App, make_handler
from .blobs import ChunkStore
from .hub import Hub
from .registry import Registry

JANITOR_SECONDS = 30


class _HTTPServer(ThreadingHTTPServer):
    """One thread per connection, at most MAX_HANDLERS at a time; more are closed at once."""

    daemon_threads = True
    MAX_HANDLERS = 64

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._slots = threading.BoundedSemaphore(self.MAX_HANDLERS)

    def process_request(self, request, client_address):
        if not self._slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self._slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._slots.release()


class Server:
    def __init__(self, config, now=time.time, log=sys.stderr):
        os.makedirs(config.data_dir, mode=0o700, exist_ok=True)
        self.config = config
        self.registry = Registry(config.data_dir, now)
        self.store = ChunkStore(os.path.join(config.data_dir, "blobs"))
        self.store.self_test()
        self.hub = Hub(config, self.store, now)
        self.app = App(config, self.registry, self.hub, log)
        self.httpd = _HTTPServer((config.host, config.port), make_handler(self.app))
        self.port = self.httpd.server_address[1]
        self.status_path = os.path.join(config.data_dir, "status.json")
        self._stop = threading.Event()
        self._closed = False

    def serve_forever(self):
        threading.Thread(target=self._janitor, daemon=True).start()
        self.httpd.serve_forever(poll_interval=0.5)

    def start_in_thread(self):
        threading.Thread(target=self.serve_forever, daemon=True).start()

    def shutdown(self):
        """Stops serving; call it from another thread than serve_forever."""
        if self._closed:
            return
        self._closed = True
        self._stop.set()
        self.hub.close()
        self.httpd.shutdown()
        self.httpd.server_close()

    def _janitor(self):
        written = None
        while True:
            try:
                written = self.tidy(written)
            except Exception:
                self.app.log.write("janitor: " + traceback.format_exc())
                self.app.log.flush()
            if self._stop.wait(JANITOR_SECONDS):
                return

    def tidy(self, written=None):
        """Drops expired items and those of revoked devices, and writes status.json for the
        admin command if it changed; returns the status."""
        self.hub.expire()
        self.hub.drop_unpaired({d["id"] for d in self.registry.devices()})
        status = self.hub.status()
        if status != written:
            tmp = self.status_path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(status, f)
            os.replace(tmp, self.status_path)
        return status
