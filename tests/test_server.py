"""The server through its HTTP API."""

import os
import threading
import time
import uuid

from helpers import API, CHUNK, Http, b64, item_body, pair, sha256
from t7drop_server.registry import Registry


def new_id():
    return str(uuid.uuid4())


def blob_files(server):
    root = server.store.root
    return [os.path.join(d, f) for d, _, files in os.walk(root) for f in files]


def test_without_a_valid_token_the_server_looks_like_the_site(server):
    h = Http(server.port)
    for path, token in (("/sync", None), ("/sync", "wrong"), ("/devices", "x"), ("/nothing", None)):
        r = h.get(path, token=token)
        assert r.status == 404
        assert r.instance is None
        assert b"<center>nginx</center>" in r.body
    assert h.request("GET", "/").status == 404


def test_a_pairing_code_works_once(server):
    code = server.registry.create_invite("pair", name="laptop")
    h = Http(server.port)
    r = h.post("/pair", json_body={"code": code.lower()[:5] + "-" + code.lower()[5:]})
    assert r.status == 200
    device = r.json()
    assert device["name"] == "laptop"
    devices = Http(server.port, device["token"]).get("/devices").json()["devices"]
    assert [d["name"] for d in devices] == ["laptop"]
    assert h.post("/pair", json_body={"code": code}).status == 404


def test_a_pairing_code_expires(server, clock):
    code = server.registry.create_invite("pair", name="laptop")
    clock.advance(601)
    assert Http(server.port).post("/pair", json_body={"code": code}).status == 404


def test_wrong_codes_lock_pairing(server):
    h = Http(server.port)
    for _ in range(10):
        assert h.post("/pair", json_body={"code": "WRONGWRONG"}).status == 404
    code = server.registry.create_invite("pair", name="laptop")
    assert h.post("/pair", json_body={"code": code}).status == 404


def test_a_revoked_device_loses_access_at_once(server):
    a, a_id = pair(server, "a")
    assert a.get("/ping").status == 200
    admin = Registry(server.config.data_dir)  # as the admin command does it, in its own process
    assert admin.revoke("a")["id"] == a_id
    r = a.get("/ping")
    assert r.status == 404 and r.instance is None


def test_only_devices_paired_by_the_admin_may_invite(server):
    a, _ = pair(server, "a")  # with a code from the admin command
    codes = [a.post("/invites").json()["code"] for _ in range(3)]
    assert a.post("/invites").status == 429
    r = Http(server.port).post("/pair", json_body={"code": codes[0], "name": "phone"})
    assert r.status == 200
    phone = Http(server.port, r.json()["token"])
    refused = phone.post("/invites")
    assert refused.status == 403 and refused.json()["error"] == "not_allowed"
    devices = {d["name"]: d["can_invite"] for d in a.get("/devices").json()["devices"]}
    assert devices == {"a": True, "phone": False}


def test_a_check_code_answers_one_ping(server):
    code = server.registry.create_invite("check", ttl=120)
    h = Http(server.port)
    r = h.get("/ping", token=None, headers={"Authorization": "Check " + code})
    assert r.status == 200 and r.instance
    assert h.get("/ping", token=None, headers={"Authorization": "Check " + code}).status == 404


def test_upload_commit_download_and_ack(server):
    a, a_id = pair(server, "a")
    b, b_id = pair(server, "b")
    data = os.urandom(2 * CHUNK + 1000)
    item = new_id()
    r = a.put("/items/" + item, json_body=item_body(b_id, data, meta=b"secret meta"))
    assert r.status == 200 and r.json() == {"id": item, "state": "uploading", "have": []}
    assert b.get("/sync").json()["inbox"] == []
    for n in range(3):
        assert a.put("/items/%s/chunks/%d" % (item, n), body=data[n * CHUNK:(n + 1) * CHUNK]).status == 200
    assert b.get("/sync").json()["inbox"] == []
    assert a.post("/items/%s/commit" % item).json()["state"] == "ready"

    inbox = b.get("/sync").json()["inbox"]
    assert [(e["id"], e["from"], e["size"], e["sha256"], e["meta"]) for e in inbox] == \
        [(item, a_id, len(data), sha256(data), b64(b"secret meta"))]
    got = b"".join(b.get("/items/%s/chunks/%d" % (item, n)).body for n in range(3))
    assert got == data
    assert a.get("/items/%s/chunks/0" % item).status == 404

    assert b.post("/items/%s/ack" % item).json()["state"] == "delivered"
    assert b.post("/items/%s/ack" % item).json()["state"] == "delivered"
    assert blob_files(server) == []
    assert b.get("/sync").json()["inbox"] == []
    assert a.get("/sync").json()["outbox"] == [{"id": item, "to": b_id, "state": "delivered", "have": 0}]


def test_create_again_tells_which_chunks_the_server_has(server):
    a, _ = pair(server, "a")
    _, b_id = pair(server, "b")
    data = os.urandom(3 * CHUNK)
    item = new_id()
    body = item_body(b_id, data)
    a.put("/items/" + item, json_body=body)
    a.put("/items/%s/chunks/0" % item, body=data[:CHUNK])
    a.put("/items/%s/chunks/2" % item, body=data[2 * CHUNK:])
    assert a.put("/items/" + item, json_body=body).json()["have"] == [0, 2]
    r = a.post("/items/%s/commit" % item)
    assert r.status == 409 and r.json() == {"error": "missing_chunks", "missing": [1]}
    assert a.put("/items/" + item, json_body=dict(body, size=1)).status == 409
    a.put("/items/%s/chunks/1" % item, body=data[CHUNK:2 * CHUNK])
    assert a.post("/items/%s/commit" % item).json()["state"] == "ready"
    assert a.put("/items/%s/chunks/1" % item, body=data[CHUNK:2 * CHUNK]).status == 409


def test_a_wrong_hash_is_refused_and_the_chunks_dropped(server):
    a, _ = pair(server, "a")
    _, b_id = pair(server, "b")
    item = new_id()
    body = dict(item_body(b_id, b"x" * 100), sha256=sha256(b"y" * 100))
    a.put("/items/" + item, json_body=body)
    a.put("/items/%s/chunks/0" % item, body=b"x" * 100)
    r = a.post("/items/%s/commit" % item)
    assert r.status == 422 and r.json()["error"] == "hash_mismatch"
    assert a.put("/items/" + item, json_body=body).json()["have"] == []
    assert blob_files(server) == []


def test_chunk_numbers_and_lengths_are_checked(server):
    a, _ = pair(server, "a")
    _, b_id = pair(server, "b")
    item = new_id()
    a.put("/items/" + item, json_body=item_body(b_id, b"x" * 100))
    assert a.put("/items/%s/chunks/0" % item, body=b"x" * 99).json() == {"error": "bad_length", "expected": 100}
    assert a.put("/items/%s/chunks/1" % item, body=b"x" * 100).status == 400
    assert a.put("/items/00000000-0000-0000-0000-000000000000", json_body={}).json()["error"] == "bad_item_id"


def test_small_content_goes_in_one_request(server):
    a, _ = pair(server, "a")
    b, b_id = pair(server, "b")
    item = new_id()
    r = a.put("/items/" + item, json_body=item_body(b_id, b"hello", inline=True))
    assert r.json()["state"] == "ready"
    assert [e["id"] for e in b.get("/sync").json()["inbox"]] == [item]


def test_recipients_must_be_other_paired_devices(server):
    a, a_id = pair(server, "a")
    assert a.put("/items/" + new_id(), json_body=item_body(a_id, b"x")).json()["error"] == "bad_recipient"
    assert a.put("/items/" + new_id(), json_body=item_body("nobody", b"x")).json()["error"] == "bad_recipient"


def test_size_limits(start_server):
    server = start_server(max_item_bytes=1000, max_total_bytes=1500, max_meta_bytes=100)
    a, _ = pair(server, "a")
    _, b_id = pair(server, "b")
    assert a.put("/items/" + new_id(), json_body=item_body(b_id, b"x" * 1001)).status == 413
    assert a.put("/items/" + new_id(), json_body=item_body(b_id, b"x", meta=b"m" * 101)).status == 413
    assert a.put("/items/" + new_id(), json_body=item_body(b_id, b"x" * 800)).status == 200
    r = a.put("/items/" + new_id(), json_body=item_body(b_id, b"x" * 800))
    assert r.status == 507 and r.json()["error"] == "storage_full"


def test_bodies_over_the_limit_are_refused(server):
    a, _ = pair(server, "a")
    r = a.post("/invites", body=b"x" * (17 * 1024))
    assert r.status == 413 and r.headers.get("connection") == "close"


def test_a_long_poll_wakes_when_an_item_arrives(server):
    a, _ = pair(server, "a")
    b, b_id = pair(server, "b")
    version = b.get("/sync").json()["version"]
    result = {}

    def poll():
        started = time.monotonic()
        result["snapshot"] = b.get("/sync?since=%d&wait=20" % version).json()
        result["seconds"] = time.monotonic() - started

    thread = threading.Thread(target=poll)
    thread.start()
    time.sleep(0.3)
    item = new_id()
    a.put("/items/" + item, json_body=item_body(b_id, b"hello", inline=True))
    thread.join(10)
    assert result["seconds"] < 5
    assert [e["id"] for e in result["snapshot"]["inbox"]] == [item]
    assert result["snapshot"]["version"] > version


def test_a_long_poll_ends_after_its_wait(server):
    b, _ = pair(server, "b")
    version = b.get("/sync").json()["version"]
    started = time.monotonic()
    snapshot = b.get("/sync?since=%d&wait=1" % version).json()
    assert 0.9 < time.monotonic() - started < 5
    assert snapshot["version"] == version


def test_unfinished_undelivered_and_delivered_items_expire(server, clock):
    a, _ = pair(server, "a")
    b, b_id = pair(server, "b")
    unfinished, ready, delivered = new_id(), new_id(), new_id()
    a.put("/items/" + unfinished, json_body=item_body(b_id, b"x" * 10))
    a.put("/items/%s/chunks/0" % unfinished, body=b"x" * 10)
    a.put("/items/" + ready, json_body=item_body(b_id, b"r", inline=True))
    a.put("/items/" + delivered, json_body=item_body(b_id, b"d", inline=True))
    b.post("/items/%s/ack" % delivered)

    clock.advance(25 * 3600)
    assert sorted(e["id"] for e in a.get("/sync").json()["outbox"]) == sorted([ready, delivered])
    clock.advance(6 * 86400)
    assert a.get("/sync").json()["outbox"] == []
    assert blob_files(server) == []


def test_chunks_on_disk_are_encrypted(server):
    a, _ = pair(server, "a")
    _, b_id = pair(server, "b")
    data = b"PLAINTEXT-MARKER " * 1000
    item = new_id()
    a.put("/items/" + item, json_body=item_body(b_id, data))
    a.put("/items/%s/chunks/0" % item, body=data)
    files = blob_files(server)
    assert len(files) == 1
    with open(files[0], "rb") as f:
        stored = f.read()
    assert b"PLAINTEXT" not in stored
    assert len(stored) == len(data) + 12 + 16


def test_a_restart_forgets_items_and_keeps_devices(start_server):
    first = start_server()
    a, _ = pair(first, "a")
    _, b_id = pair(first, "b")
    item = new_id()
    a.put("/items/" + item, json_body=item_body(b_id, b"x" * 10))
    a.put("/items/%s/chunks/0" % item, body=b"x" * 10)
    old_instance = a.get("/ping").instance
    first.shutdown()

    second = start_server(port=first.port)
    assert blob_files(second) == []
    r = a.get("/ping")
    assert r.status == 200 and r.instance != old_instance
    assert a.get("/sync").json()["outbox"] == []
    assert a.put("/items/" + item, json_body=item_body(b_id, b"x" * 10)).json()["have"] == []


def test_the_sender_can_delete_an_item(server):
    a, _ = pair(server, "a")
    b, b_id = pair(server, "b")
    item = new_id()
    a.put("/items/" + item, json_body=item_body(b_id, b"hello", inline=True))
    assert b.delete("/items/" + item).status == 404
    assert a.delete("/items/" + item).status == 200
    assert b.get("/sync").json()["inbox"] == []
    assert blob_files(server) == []
