"""The Windows app's transfer engine against a local server, with the CLI as the phone.

windows/scripts/build.ps1 builds windows/obj/Tests.exe; `Tests.exe agent <home> ...` runs the
app's Store, Transfers and Worker without a window (windows/tests/Agent.cs). These tests are
skipped where it has not been built. The server runs in this process, so a test can slow its
chunk handling down to interrupt a transfer at a known point.
"""

import os
import queue
import re
import socket
import subprocess
import sys
import threading
import time

import pytest

import helpers
from t7drop_cli.main import main
from t7drop_cli.state import Profile

TESTS_EXE = os.path.join(helpers.ROOT, "windows", "obj", "Tests.exe")
pytestmark = pytest.mark.skipif(sys.platform != "win32" or not os.path.exists(TESTS_EXE),
                                reason="needs windows/obj/Tests.exe from windows/scripts/build.ps1")
MB = 1024 * 1024


class Windows:
    """The app's engine in its own data folder."""

    def __init__(self, tmp_path):
        self.home = str(tmp_path / "windows")
        self.downloads = os.path.join(self.home, "downloads")
        self.process = None
        self.lines = queue.Queue()
        self.output = []

    def run(self, *args, timeout=120):
        result = subprocess.run([TESTS_EXE, "agent", self.home] + [str(a) for a in args], capture_output=True,
                                text=True, encoding="utf-8", timeout=timeout)
        return result.returncode, result.stdout + result.stderr

    def start(self, *args):
        self.process = subprocess.Popen([TESTS_EXE, "agent", self.home, "run"] + [str(a) for a in args],
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8")
        self.lines = queue.Queue()
        self.output = []
        threading.Thread(target=self._read, args=(self.process, self.lines), daemon=True).start()

    def _read(self, process, lines):
        for line in process.stdout:
            self.output.append(line.rstrip("\n"))
            lines.put(line.rstrip("\n"))
        lines.put(None)

    def wait_for(self, pattern, timeout=30):
        deadline = time.monotonic() + timeout
        while True:
            try:
                line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                line = None
            if line is None:
                raise AssertionError("no line like %r; output:\n%s" % (pattern, "\n".join(self.output)))
            match = re.search(pattern, line)
            if match:
                return match

    def finish(self, timeout=120):
        code = self.process.wait(timeout)
        self.process = None
        return code

    def kill(self):
        self.process.kill()
        self.process.wait(10)
        self.process = None

    def states(self):
        code, out = self.run("status")
        assert code == 0, out
        return sorted(line.split()[1] for line in out.splitlines() if line.strip())

    def files(self):
        return sorted(os.listdir(self.downloads)) if os.path.isdir(self.downloads) else []


class Phone:
    """The CLI as the phone."""

    def __init__(self, tmp_path, name="phone"):
        self.home = str(tmp_path / ("home-" + name))
        self.downloads = str(tmp_path / ("downloads-" + name))

    def cli(self, *args):
        return main(["--home", self.home] + [str(a) for a in args])

    def receive(self):
        return self.cli("receive", "--dir", self.downloads)

    def receive_until(self, check, timeout=60):
        deadline = time.monotonic() + timeout
        while not check():
            assert time.monotonic() < deadline, "not received in %d s" % timeout
            self.receive()
            time.sleep(0.2)

    def files(self):
        return sorted(os.listdir(self.downloads)) if os.path.isdir(self.downloads) else []

    def read(self, name):
        with open(os.path.join(self.downloads, name), "rb") as f:
            return f.read()

    def texts(self):
        path = os.path.join(self.home, "texts.txt")
        return open(path, encoding="utf-8").read() if os.path.exists(path) else ""

    def states(self):
        return [i.state for i in Profile(self.home).outbox()]


def url(server):
    return "http://127.0.0.1:%d" % server.port


def join(windows, tmp_path, capsys, name="phone"):
    code, out = windows.run("invite")
    assert code == 0, out
    invitation = re.search(r"(t7drop:pair\?\S+)", out).group(1)
    phone = Phone(tmp_path, name)
    capsys.readouterr()
    assert phone.cli("join", invitation, "--name", name) == 0
    return phone


@pytest.fixture
def paired(server, tmp_path, capsys):
    windows = Windows(tmp_path)
    code, out = windows.run("pair", url(server), server.registry.create_invite("pair", name="laptop"), "laptop")
    assert code == 0, out
    windows.device_id = re.search(r"device (\w+)", out).group(1)
    yield windows, join(windows, tmp_path, capsys)
    if windows.process:
        windows.kill()


def slow(server, method, seconds):
    """Makes the server's hub take `seconds` for each call of `method`; returns the list of calls."""
    calls = []
    real = getattr(server.hub, method)

    def slower(*args):
        time.sleep(seconds)
        result = real(*args)
        calls.append(args[2])
        return result

    setattr(server.hub, method, slower)
    return calls


def track_connections(server):
    """Records the server's connections, so that restart() can cut them as a process restart would."""
    connections = []
    real = server.httpd.process_request

    def process_request(request, client_address):
        connections.append(request)
        real(request, client_address)

    server.httpd.process_request = process_request
    return connections


def restart(server, start_server, connections):
    """Stops the server and cuts its connections (its handler threads would serve them on), then starts
    a new one on the same port and data folder: what a restart of the service does."""
    server.shutdown()
    for connection in connections:
        try:
            connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
    return start_server(port=server.port)


def write(tmp_path, name, size):
    path = tmp_path / name
    data = os.urandom(size)
    path.write_bytes(data)
    return path, data


def test_texts_and_files_both_ways(paired, tmp_path):
    windows, phone = paired
    windows.start(90, "--until-received", 2)
    windows.wait_for(r"STATUS Online")
    time.sleep(1)  # in its long poll now

    started = time.monotonic()
    assert phone.cli("send", "--text", "Привет 👋\nhttps://example.org/a?b=c") == 0
    match = windows.wait_for(r"RECEIVED \S+ text (.*)", timeout=10)
    assert time.monotonic() - started < 2, "a text arrives within about 2 s of its sending"
    assert match.group(1) == "Привет 👋\\nhttps://example.org/a?b=c"

    source, data = write(tmp_path, "photo.jpg", 9 * MB + 5)
    assert phone.cli("send", source) == 0
    saved = windows.wait_for(r"RECEIVED \S+ file (.*)").group(1)
    assert windows.finish() == 0
    assert os.path.basename(saved) == "photo.jpg"
    assert windows.files() == ["photo.jpg"]
    with open(saved, "rb") as f:
        assert f.read() == data
    with open(saved + ":Zone.Identifier", encoding="ascii") as f:
        assert "ZoneId=3" in f.read(), "marked as from the internet (docs/security.md)"
    assert phone.receive() == 0
    assert phone.states() == ["delivered", "delivered"]

    big, big_data = write(tmp_path, "notes.pdf", 6 * MB)
    code, out = windows.run("send-file", big)
    assert code == 0, out
    code, out = windows.run("send-text", "from Windows: ok")
    assert code == 0, out
    assert windows.states() == ["queued", "queued"]
    windows.start(90, "--until-delivered")
    phone.receive_until(lambda: "notes.pdf" in phone.files() and "from Windows: ok" in phone.texts())
    assert windows.finish() == 0, "\n".join(windows.output)
    assert phone.read("notes.pdf") == big_data
    assert windows.states() == ["delivered", "delivered"], "delivered once the phone has acknowledged"


def test_a_lost_acknowledgement_does_not_save_twice(paired, server, tmp_path):
    windows, phone = paired
    source, data = write(tmp_path, "once.bin", 100 * 1024)
    assert phone.cli("send", source) == 0
    windows.start(60, "--until-received", 1, "--fail-acks")
    windows.wait_for(r"RECEIVED \S+ file")
    windows.finish()
    assert windows.files() == ["once.bin"]
    assert server.hub.status()["items"] == {"ready": 1}, "not acknowledged"

    code, out = windows.run("run", 4)
    assert code == 0, out
    assert "RECEIVED" not in out
    assert windows.files() == ["once.bin"], "saved once"
    assert server.hub.status()["items"] == {"delivered": 1}, "acknowledged now"


def test_an_upload_resumes_with_the_missing_chunks(paired, server, tmp_path):
    windows, phone = paired
    puts = slow(server, "put_chunk", 0.3)
    source, data = write(tmp_path, "big.bin", 30 * MB)  # 8 chunks of ciphertext
    code, out = windows.run("send-file", source)
    assert code == 0, out
    windows.start(90, "--until-delivered")
    deadline = time.monotonic() + 30
    while len(puts) < 3:
        assert time.monotonic() < deadline
        time.sleep(0.05)
    windows.kill()
    sent_before = len(puts)
    assert windows.states() == ["uploading"]

    windows.start(90, "--until-delivered")
    phone.receive_until(lambda: "big.bin" in phone.files())
    assert windows.finish() == 0
    assert phone.read("big.bin") == data
    assert len(puts) <= 8 + 1, "chunks sent: %d before the kill, %d in all" % (sent_before, len(puts))


def test_an_upload_cut_by_a_server_restart_is_sent_again(paired, start_server, server, tmp_path):
    windows, phone = paired
    connections = track_connections(server)
    puts = slow(server, "put_chunk", 0.3)
    source, data = write(tmp_path, "restart.bin", 20 * MB)
    code, out = windows.run("send-file", source)
    assert code == 0, out
    windows.start(120, "--until-delivered")
    deadline = time.monotonic() + 30
    while len(puts) < 2:
        assert time.monotonic() < deadline
        time.sleep(0.05)
    second = restart(server, start_server, connections)
    phone.receive_until(lambda: "restart.bin" in phone.files(), timeout=90)
    assert windows.finish() == 0, "\n".join(windows.output)
    assert phone.read("restart.bin") == data
    assert second.hub.status()["items"] == {"delivered": 1}
    # It noticed the restart one of two ways, as timing has it: the connection failed, or
    # .NET resent the cut request on a new connection and the new server did not know the item.
    assert any("not reachable" in line or "changed on the server" in line for line in windows.output), \
        "it noticed the restart"


def test_a_download_resumes_and_no_partial_file_shows(paired, server, tmp_path):
    windows, phone = paired
    gets = slow(server, "get_chunk", 0.3)
    source, data = write(tmp_path, "video.mp4", 25 * MB)  # 7 chunks
    assert phone.cli("send", source) == 0
    seen = set()
    stop = threading.Event()

    def watch():
        while not stop.is_set():
            for name in windows.files():
                path = os.path.join(windows.downloads, name)
                try:
                    size = os.path.getsize(path)
                except OSError:
                    continue
                seen.add((name, size == len(data)))
            time.sleep(0.01)

    watcher = threading.Thread(target=watch)
    watcher.start()
    try:
        windows.start(90, "--until-received", 1)
        deadline = time.monotonic() + 30
        while len(gets) < 3:
            assert time.monotonic() < deadline
            time.sleep(0.05)
        windows.kill()
        fetched_before = len(gets)
        windows.start(90, "--until-received", 1)
        windows.wait_for(r"RECEIVED \S+ file")
        assert windows.finish() == 0
    finally:
        stop.set()
        watcher.join()
    with open(os.path.join(windows.downloads, "video.mp4"), "rb") as f:
        assert f.read() == data
    assert len(gets) <= 7 + 1, "chunks fetched: %d before the kill, %d in all" % (fetched_before, len(gets))
    for name, complete in seen:
        assert complete or (name.startswith(".video.mp4.") and name.endswith(".t7drop-part")), (name, complete)


def test_a_failed_item_is_sent_again_to_the_phone_paired_now(paired, server, tmp_path, capsys):
    windows, phone = paired
    code, out = windows.run("send-text", "for the new phone")
    assert code == 0, out
    server.registry.revoke(Profile(phone.home).device_id)  # before it reaches the server
    code, out = windows.run("run", 3)
    assert windows.states() == ["failed"], out
    code, out = windows.run("status")
    item_id = out.split()[0]
    assert "no longer paired" in out

    new_phone = join(windows, tmp_path, capsys, name="new-phone")
    code, out = windows.run("retry", item_id)
    assert code == 0, out
    windows.start(60, "--until-delivered")
    new_phone.receive_until(lambda: "for the new phone" in new_phone.texts())
    assert windows.finish() == 0
    assert windows.states() == ["delivered"]


def test_a_cancelled_item_goes_from_the_server(paired, server, tmp_path):
    windows, phone = paired
    code, out = windows.run("send-text", "never mind")
    assert code == 0, out
    code, out = windows.run("run", 3)
    assert windows.states() == ["on_server"], out
    item_id = windows.run("status")[1].split()[0]
    assert windows.run("cancel", item_id)[0] == 0
    code, out = windows.run("run", 3)
    assert windows.states() == [], out
    assert server.hub.status()["items"] == {}
    assert phone.receive() == 0
    assert "never mind" not in phone.texts()
