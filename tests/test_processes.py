"""The entry points as separate processes: server/main.py, server/admin.py, cli/t7drop.py."""

import os
import re
import socket
import subprocess
import sys
import time

import helpers

PYTHON = sys.executable


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run(*args, **kwargs):
    result = subprocess.run([PYTHON, "-E", "-s"] + list(args), capture_output=True, text=True,
                            encoding="utf-8", timeout=120, **kwargs)
    return result.returncode, result.stdout + result.stderr


def test_server_admin_and_cli_work_as_programs(tmp_path):
    port = free_port()
    data = tmp_path / "data"
    config = tmp_path / "server.conf"
    config.write_text("[server]\nlisten = 127.0.0.1:%d\ndata_dir = %s\n\n[limits]\nmax_item_bytes = 1M\n"
                      % (port, data), encoding="utf-8")
    server_py = os.path.join(helpers.ROOT, "server", "main.py")
    admin_py = os.path.join(helpers.ROOT, "server", "admin.py")
    cli_py = os.path.join(helpers.ROOT, "cli", "t7drop.py")
    server = subprocess.Popen([PYTHON, "-E", "-s", server_py, str(config)],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        for _ in range(100):
            try:
                socket.create_connection(("127.0.0.1", port), timeout=1).close()
                break
            except OSError:
                time.sleep(0.1)
        code, out = run(admin_py, "--config", str(config), "invite", "laptop")
        assert code == 0, out
        pairing_code = re.search(r"([2-9A-Z]{5}-[2-9A-Z]{5})", out).group(1)

        a = ["--home", str(tmp_path / "a")]
        b = ["--home", str(tmp_path / "b")]
        url = "http://127.0.0.1:%d" % port
        assert run(cli_py, *a, "pair", url, pairing_code, "--name", "laptop")[0] == 0
        code, out = run(cli_py, *a, "invite", "--png", str(tmp_path / "qr.png"))
        assert code == 0, out
        invitation = re.search(r"(t7drop:pair\?\S+)", out).group(1)
        assert run(cli_py, *b, "join", invitation, "--name", "phone")[0] == 0

        source = tmp_path / "note.txt"
        source.write_bytes(b"from one process to another")
        code, out = run(cli_py, *a, "send", str(source))
        assert code == 0, out
        code, out = run(cli_py, *b, "receive", "--dir", str(tmp_path / "in"))
        assert code == 0, out
        assert (tmp_path / "in" / "note.txt").read_bytes() == b"from one process to another"

        code, out = run(admin_py, "--config", str(config), "devices")
        assert code == 0 and "laptop" in out and "phone" in out
        assert run(admin_py, "--config", str(config), "revoke", "phone")[0] == 0
        code, out = run(cli_py, *b, "receive", "--dir", str(tmp_path / "in"))
        assert code == 3 and "without t7drop's header" in out
    finally:
        server.terminate()
        server.wait(10)
    assert "listening on 127.0.0.1:%d" % port in server.stdout.read()
