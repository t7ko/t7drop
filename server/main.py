#!/usr/bin/env python3
"""Runs the t7drop server: python3 -E -s main.py [server.conf]

Run by t7drop.service on the VPS (docs/deployment.md). Stops on SIGTERM or Ctrl+C, and
deletes the chunk files then: they cannot be read without the key that ends with the
process anyway.
"""

import os
import signal
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from t7drop_server import VERSION  # noqa: E402
from t7drop_server import config as config_module  # noqa: E402
from t7drop_server.app import Server  # noqa: E402

PR_SET_DUMPABLE = 4


def make_not_dumpable():
    """Keeps the in-memory key out of core dumps and away from ptrace by the same user."""
    if not sys.platform.startswith("linux"):
        return
    import ctypes
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) != 0:
        sys.stderr.write("warning: prctl(PR_SET_DUMPABLE) failed: errno %d\n" % ctypes.get_errno())


def main(argv):
    config = config_module.load(argv[1] if len(argv) > 1 else None)
    make_not_dumpable()
    server = Server(config)

    def stop(signum, frame):
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    sys.stderr.write("t7drop %s listening on %s:%d, instance %s\n"
                     % (VERSION, config.host, server.port, server.hub.instance))
    sys.stderr.flush()
    server.serve_forever()
    server.store.wipe()
    sys.stderr.write("t7drop stopped\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
