"""A CLI profile: one paired device, in one folder.

  config.json      server URL, device id and name, token, group key (in plain text: the
                   CLI is a test tool)
  outbox/<id>/     item.json (state), meta.bin and data.bin (ciphertext, kept until the
                   item is delivered)
  incoming/        downloads in progress
  received.json    ids of the items saved, for 30 days, so that none is saved twice
  refused.json     ids of the items refused (not this group's key, not for this device, or
                   damaged), for 8 days: the server keeps an item 7, and it is not looked
                   at again in the meantime
  texts.txt        texts received
"""

import json
import os
import re
import shutil
import time

from .pairing import from_b64url

KEEP_RECEIVED_SECONDS = 30 * 86400
KEEP_REFUSED_SECONDS = 8 * 86400
STALE_PART_SECONDS = 86400  # a partial download of an item no longer waiting, untouched this long
DECRYPTING_NAME = re.compile(r"^\..+\.[0-9a-f]{8}\.t7drop-part$")  # agent.Agent._save_file's temporary file


def default_home():
    if os.environ.get("T7DROP_HOME"):
        return os.environ["T7DROP_HOME"]
    base = os.environ.get("APPDATA") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(base, "t7drop-cli")


def write_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
    os.replace(tmp, path)


def read_json(path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return default


class OutItem:
    def __init__(self, folder):
        self.folder = folder
        self.data = read_json(os.path.join(folder, "item.json"), None)

    @property
    def id(self):
        return self.data["id"]

    @property
    def state(self):
        return self.data["state"]

    @property
    def label(self):
        return self.data.get("name") or "text (%d bytes)" % self.data["size"]

    @property
    def meta_path(self):
        return os.path.join(self.folder, "meta.bin")

    @property
    def data_path(self):
        return os.path.join(self.folder, "data.bin")

    def save(self):
        write_json(os.path.join(self.folder, "item.json"), self.data)

    def set_state(self, state, error=None):
        self.data["state"] = state
        self.data["error"] = error
        self.data["changed"] = time.time()
        self.save()

    def drop_payload(self):
        for path in (self.meta_path, self.data_path):
            if os.path.exists(path):
                os.remove(path)


class Profile:
    def __init__(self, home):
        self.home = home
        self.config_path = os.path.join(home, "config.json")
        self.outbox_dir = os.path.join(home, "outbox")
        self.incoming_dir = os.path.join(home, "incoming")
        self.received_path = os.path.join(home, "received.json")
        self.refused_path = os.path.join(home, "refused.json")
        self.texts_path = os.path.join(home, "texts.txt")
        self.config = read_json(self.config_path, None)

    @property
    def paired(self):
        return self.config is not None

    def save_config(self, config):
        os.makedirs(self.home, exist_ok=True)
        write_json(self.config_path, config)
        self.config = config

    @property
    def key(self):
        return from_b64url(self.config["key"])

    @property
    def device_id(self):
        return self.config["device_id"]

    def new_out_item(self, item_id):
        folder = os.path.join(self.outbox_dir, item_id)
        os.makedirs(folder)
        return folder

    def outbox(self):
        if not os.path.isdir(self.outbox_dir):
            return []
        items = [OutItem(os.path.join(self.outbox_dir, name)) for name in os.listdir(self.outbox_dir)]
        return sorted((i for i in items if i.data), key=lambda i: i.data["created"])

    def forget(self, item):
        shutil.rmtree(item.folder, ignore_errors=True)

    def received(self):
        return read_json(self.received_path, {})

    def mark_received(self, item_id, now):
        received = {k: t for k, t in self.received().items() if now - t < KEEP_RECEIVED_SECONDS}
        received[item_id] = now
        write_json(self.received_path, received)

    def refused(self):
        return read_json(self.refused_path, {})

    def mark_refused(self, item_id, now):
        refused = {k: t for k, t in self.refused().items() if now - t < KEEP_REFUSED_SECONDS}
        refused[item_id] = now
        write_json(self.refused_path, refused)

    def drop_stale_parts(self, waiting, now, download_dir=None):
        """Deletes the partial downloads of items that are no longer waiting (cancelled,
        expired, or lost in a server restart: then they may come again, so not at once), and
        the .<name>.<id>.t7drop-part files a stopped decryption left in download_dir."""
        if os.path.isdir(self.incoming_dir):
            for name in os.listdir(self.incoming_dir):
                path = os.path.join(self.incoming_dir, name)
                if name.endswith(".part") and name[:-len(".part")] in waiting:
                    continue
                if now - os.path.getmtime(path) > STALE_PART_SECONDS:
                    os.remove(path)
        if download_dir and os.path.isdir(download_dir):
            for name in os.listdir(download_dir):
                path = os.path.join(download_dir, name)
                if DECRYPTING_NAME.match(name) and now - os.path.getmtime(path) > STALE_PART_SECONDS:
                    os.remove(path)

    def add_text(self, sender, text, now):
        os.makedirs(self.home, exist_ok=True)
        stamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(now))
        with open(self.texts_path, "a", encoding="utf-8") as f:
            f.write("----- %s from %s\n%s\n" % (stamp, sender, text))
