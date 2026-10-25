"""The CLI against a real server: two devices, sending and receiving."""

import os
import time
import uuid

import pytest

from t7drop_cli.agent import Agent
from t7drop_cli.api import Api, TransientError
from t7drop_cli.files import safe_name, unique_path
from t7drop_cli.main import main
from t7drop_cli.state import Profile


class Device:
    def __init__(self, tmp_path, name):
        self.name = name
        self.home = str(tmp_path / ("home-" + name))
        self.downloads = str(tmp_path / ("downloads-" + name))

    def run(self, *args):
        return main(["--home", self.home] + list(args))

    def receive(self):
        return self.run("receive", "--dir", self.downloads)

    def files(self):
        return sorted(os.listdir(self.downloads)) if os.path.isdir(self.downloads) else []

    def read(self, name):
        with open(os.path.join(self.downloads, name), "rb") as f:
            return f.read()

    def states(self):
        return [i.state for i in Profile(self.home).outbox()]


def url(server):
    return "http://127.0.0.1:%d" % server.port


def first_device(server, tmp_path, name="a"):
    device = Device(tmp_path, name)
    code = server.registry.create_invite("pair", name=name)
    assert device.run("pair", url(server), code, "--name", name) == 0
    return device


def joined_device(inviter, tmp_path, capsys, name="b"):
    capsys.readouterr()
    assert inviter.run("invite", "--png", str(tmp_path / "qr.png")) == 0
    invitation = next(line for line in capsys.readouterr().out.splitlines() if line.startswith("t7drop:pair?"))
    device = Device(tmp_path, name)
    assert device.run("join", invitation, "--name", name) == 0
    return device


@pytest.fixture
def pair_of(server, tmp_path, capsys):
    a = first_device(server, tmp_path)
    return a, joined_device(a, tmp_path, capsys)


def test_a_file_and_a_text_go_from_one_device_to_the_other(pair_of, tmp_path, capsys):
    a, b = pair_of
    source = tmp_path / "photo.jpg"
    data = os.urandom(9 * 1024 * 1024 + 123)
    source.write_bytes(data)
    assert a.run("send", str(source)) == 0
    assert a.run("send", "--text", "https://example.org/\nand \u0442\u0435\u043a\u0441\u0442") == 0
    assert a.states() == ["on_server", "on_server"]

    capsys.readouterr()
    assert b.receive() == 0
    assert b.files() == ["photo.jpg"]
    assert b.read("photo.jpg") == data
    assert "and \u0442\u0435\u043a\u0441\u0442" in capsys.readouterr().out
    with open(os.path.join(b.home, "texts.txt"), encoding="utf-8") as f:
        assert "https://example.org/" in f.read()

    assert a.receive() == 0
    assert a.states() == ["delivered", "delivered"]
    outbox = Profile(a.home).outbox()
    assert all(not os.path.exists(i.data_path) for i in outbox)


def test_an_upload_cut_by_a_server_restart_is_sent_again(start_server, tmp_path, capsys, monkeypatch):
    first = start_server()
    a = first_device(first, tmp_path)
    b = joined_device(a, tmp_path, capsys)
    source = tmp_path / "big.bin"
    data = os.urandom(10 * 1024 * 1024)
    source.write_bytes(data)
    assert a.run("send", "--queue-only", str(source)) == 0

    calls = []
    real_put = Api.put_chunk

    def failing_put(self, item_id, n, chunk):
        calls.append(n)
        if len(calls) == 2:
            raise TransientError("connection reset")
        real_put(self, item_id, n, chunk)

    monkeypatch.setattr(Api, "put_chunk", failing_put)
    assert a.receive() == 3
    assert a.states() == ["uploading"]
    monkeypatch.setattr(Api, "put_chunk", real_put)

    first.shutdown()
    second = start_server(port=first.port)
    assert a.receive() == 0
    assert a.states() == ["on_server"]
    assert b.receive() == 0
    assert b.read("big.bin") == data
    assert a.receive() == 0
    assert a.states() == ["delivered"]
    assert second.hub.status()["items"] == {"delivered": 1}


def test_an_item_on_a_restarted_server_is_uploaded_again(start_server, tmp_path, capsys):
    first = start_server()
    a = first_device(first, tmp_path)
    b = joined_device(a, tmp_path, capsys)
    assert a.run("send", "--text", "survives a restart") == 0
    assert a.states() == ["on_server"]
    first.shutdown()
    start_server(port=first.port)
    assert a.receive() == 0  # sees that the server lost it, and sends it again
    capsys.readouterr()
    assert b.receive() == 0
    assert "survives a restart" in capsys.readouterr().out


def test_a_lost_acknowledgement_does_not_save_the_file_twice(pair_of, tmp_path, monkeypatch):
    a, b = pair_of
    source = tmp_path / "doc.pdf"
    source.write_bytes(b"%PDF-1.7 tiny")
    a.run("send", str(source))

    real_ack = Api.ack

    def lost_ack(self, item_id):
        raise TransientError("timed out")

    monkeypatch.setattr(Api, "ack", lost_ack)
    assert b.receive() == 3
    monkeypatch.setattr(Api, "ack", real_ack)
    assert b.files() == ["doc.pdf"]
    assert b.receive() == 0
    assert b.files() == ["doc.pdf"]
    assert a.receive() == 0
    assert a.states() == ["delivered"]


def test_sending_while_the_server_is_down_queues_the_item(start_server, tmp_path, capsys):
    first = start_server()
    a = first_device(first, tmp_path)
    b = joined_device(a, tmp_path, capsys)
    assert a.run("devices") == 0  # a learns about b
    first.shutdown()
    capsys.readouterr()
    assert a.run("devices") == 3
    assert a.run("send", "--text", "sent offline") == 0
    assert "Not sent yet" in capsys.readouterr().out
    assert a.states() == ["queued"]
    start_server(port=first.port)
    assert a.receive() == 0
    assert a.states() == ["on_server"]
    assert b.receive() == 0
    assert "sent offline" in capsys.readouterr().out


def test_queued_items_wait_for_the_server(start_server, tmp_path, capsys):
    first = start_server()
    a = first_device(first, tmp_path)
    b = joined_device(a, tmp_path, capsys)
    agent = Agent(Profile(a.home), Api(url(first), Profile(a.home).config["token"]), str(tmp_path / "x"))
    agent.queue_text("written offline", Profile(b.home).device_id)
    first.shutdown()
    with pytest.raises(TransientError):
        agent.run()
    assert a.states() == ["queued"]
    start_server(port=first.port)
    assert a.receive() == 0
    assert a.states() == ["on_server"]
    capsys.readouterr()
    assert b.receive() == 0
    assert "written offline" in capsys.readouterr().out


def test_watch_mode_waits_and_retries_when_the_server_is_down(server, tmp_path, capsys):
    a = first_device(server, tmp_path)
    profile = Profile(a.home)
    agent = Agent(profile, Api("http://127.0.0.1:9", profile.config["token"]), str(tmp_path / "x"), out=lambda s: None)
    pauses = []

    def sleep(seconds):
        pauses.append(seconds)
        if len(pauses) == 4:
            raise KeyboardInterrupt

    with pytest.raises(KeyboardInterrupt):
        agent.run(watch=True, sleep=sleep)
    assert pauses[0] <= 1 and pauses[3] > pauses[0]


def test_an_item_from_another_group_is_refused(server, pair_of, tmp_path, capsys):
    a, b = pair_of
    stranger = first_device(server, tmp_path, "c")  # paired by the admin: its own key
    assert stranger.run("send", "--to", "b", "--text", "hello") == 0
    capsys.readouterr()
    assert b.receive() == 0
    assert "does not decrypt" in capsys.readouterr().out
    assert b.files() == []
    assert not os.path.exists(os.path.join(b.home, "received.json"))
    assert b.receive() == 0, "a new run of the CLI"
    assert "does not decrypt" not in capsys.readouterr().out, "the refusal was kept: not looked at again"


def test_partial_downloads_of_items_gone_are_deleted_after_a_day(pair_of, tmp_path):
    a, b = pair_of
    profile = Profile(b.home)
    os.makedirs(profile.incoming_dir)
    old, recent, waiting = (os.path.join(profile.incoming_dir, "%s.part" % uuid.uuid4()) for _ in range(3))
    two_days_ago = time.time() - 2 * 86400
    for path in (old, recent, waiting):
        with open(path, "wb") as f:
            f.write(b"x" * 100)
    os.utime(old, (two_days_ago, two_days_ago))
    os.utime(waiting, (two_days_ago, two_days_ago))
    profile.drop_stale_parts({os.path.basename(waiting)[:-len(".part")]}, time.time())
    assert not os.path.exists(old)
    assert os.path.exists(recent), "perhaps lost in a server restart: its sender may upload it again"
    assert os.path.exists(waiting), "still in the inbox: its download goes on"
    os.makedirs(b.downloads)
    leftover = os.path.join(b.downloads, ".photo.jpg.0123abcd.t7drop-part")
    not_ours = os.path.join(b.downloads, "notes.t7drop-part")
    for path in (leftover, not_ours):
        with open(path, "wb") as f:
            f.write(b"x")
        os.utime(path, (two_days_ago, two_days_ago))
    os.utime(recent, (two_days_ago, two_days_ago))
    assert b.receive() == 0, "a sync tidies up too"
    assert not os.path.exists(recent)
    assert not os.path.exists(leftover), "an old decryption that never finished"
    assert os.path.exists(not_ours)


def test_delivered_items_leave_the_outbox_after_30_days(pair_of):
    a, b = pair_of
    assert a.run("send", "--text", "old news") == 0
    assert b.receive() == 0
    assert a.receive() == 0
    assert a.states() == ["delivered"]
    item = Profile(a.home).outbox()[0]
    item.data["changed"] = time.time() - 31 * 86400
    item.save()
    assert a.receive() == 0
    assert a.states() == []


def test_an_invited_device_may_not_invite(pair_of, tmp_path, capsys):
    a, b = pair_of
    capsys.readouterr()
    assert b.run("invite") == 4
    assert "Only a device paired with a code from t7drop-admin" in capsys.readouterr().err


def test_received_names_are_made_safe(tmp_path):
    assert safe_name("../../etc/passwd") == "passwd"
    assert safe_name("C:\\Windows\\win.ini") == "win.ini"
    assert safe_name('a<b>c:"d|e?f*.txt') == "a_b_c__d_e_f_.txt"
    assert safe_name("CON.txt") == "_CON.txt"
    assert safe_name("name. . ") == "name"
    assert safe_name("") == "file"
    assert len(safe_name("x" * 300 + ".jpg")) == 150
    assert safe_name("photo\u202egpj.exe") == "photogpj.exe"
    assert safe_name("\u200bnote\ufeff.\u2066txt") == "note.txt"
    (tmp_path / "a.txt").write_bytes(b"")
    (tmp_path / "a (1).txt").write_bytes(b"")
    assert unique_path(str(tmp_path), "a.txt") == str(tmp_path / "a (2).txt")
