"""t7drop command-line client: a test tool for the server, and the Windows receiver until
the tray app exists. Each profile folder (--home) is one paired device."""

import argparse
import os
import platform
import sys
import time

from . import crypto
from .agent import DELIVERED, FAILED, Agent, human_size
from .api import Api, ApiError, TransientError
from .pairing import b64url, make_uri, parse_uri
from .state import Profile, default_home


def default_downloads():
    return os.path.join(os.path.expanduser("~"), "Downloads", "t7drop")


def _api(profile):
    return Api(profile.config["url"], profile.config["token"])


def _save_pairing(profile, url, reply, key):
    profile.save_config({"url": url, "device_id": reply["device_id"], "device_name": reply["name"],
                         "token": reply["token"], "key": b64url(key)})
    print("Paired as %s (%s). Profile: %s" % (reply["name"], reply["device_id"], profile.home))
    try:
        _devices(profile, _api(profile))
    except (TransientError, ApiError):
        pass


def cmd_pair(profile, args):
    if profile.paired:
        print("This profile is paired already: %s" % profile.home, file=sys.stderr)
        return 2
    url = args.url.rstrip("/")
    _save_pairing(profile, url, Api(url).pair(args.code, args.name), crypto.new_key())
    return 0


def cmd_join(profile, args):
    if profile.paired:
        print("This profile is paired already: %s" % profile.home, file=sys.stderr)
        return 2
    try:
        url, code, key = parse_uri(args.invitation)
    except ValueError as e:
        print(e, file=sys.stderr)
        return 2
    _save_pairing(profile, url, Api(url).pair(code, args.name), key)
    return 0


def cmd_invite(profile, args):
    try:
        reply = _api(profile).invite()
    except ApiError as e:
        if e.code != "not_allowed":
            raise
        print("Only a device paired with a code from t7drop-admin may invite devices.", file=sys.stderr)
        return 4
    uri = make_uri(profile.config["url"], reply["code"], profile.key)
    print("Scan this on the new device within %d minutes, or give it this invitation:" % (reply["expires_in"] // 60))
    print(uri)
    import segno
    qr = segno.make(uri, error="m")
    if args.png:
        qr.save(args.png, scale=8, border=4)
        print("QR code saved to %s" % args.png)
    else:
        qr.terminal(compact=True)
    return 0


def _devices(profile, api):
    """The paired devices; when the server cannot be reached, as it last listed them."""
    try:
        devices = api.devices()
    except TransientError:
        if "devices" not in profile.config:
            raise
        return profile.config["devices"]
    if devices != profile.config.get("devices"):
        profile.save_config(dict(profile.config, devices=devices))
    return devices


def cmd_devices(profile, args):
    devices = _api(profile).devices()
    profile.save_config(dict(profile.config, devices=devices))
    for d in devices:
        mine = "  (this device)" if d["id"] == profile.device_id else ""
        print("%s  %s%s" % (d["id"], d["name"], mine))
    return 0


def _recipient(profile, api, wanted):
    others = [d for d in _devices(profile, api) if d["id"] != profile.device_id]
    if wanted:
        others = [d for d in others if wanted in (d["id"], d["name"])]
    if len(others) != 1:
        names = ", ".join(d["name"] for d in others) or "none"
        raise SystemExit("Give the recipient with --to (devices: %s)" % names)
    return others[0]["id"]


def cmd_send(profile, args):
    if not args.files and args.text is None:
        print("Give files to send, or --text", file=sys.stderr)
        return 2
    api = _api(profile)
    to = _recipient(profile, api, args.to)
    agent = Agent(profile, api, default_downloads())
    for path in args.files:
        agent.queue_file(path, to)
        print("Queued %s" % path)
    if args.text is not None:
        agent.queue_text(args.text, to)
        print("Queued text")
    if not args.queue_only:
        try:
            agent.send_pending()
        except TransientError as e:
            print("Not sent yet (%s); 't7drop receive' sends it later." % e)
    return 0


def cmd_status(profile, args):
    for item in profile.outbox():
        d = item.data
        when = time.strftime("%Y-%m-%d %H:%M", time.localtime(d["created"]))
        error = " (%s)" % d["error"] if d.get("error") else ""
        print("%s  %-10s%s  %s, %s" % (when, d["state"], error, item.label, human_size(d["size"])))
    return 0


def cmd_clean(profile, args):
    for item in profile.outbox():
        if item.state in (DELIVERED, FAILED):
            profile.forget(item)
    return 0


def cmd_receive(profile, args):
    agent = Agent(profile, _api(profile), args.dir)
    try:
        agent.run(watch=args.watch)
    except KeyboardInterrupt:
        pass
    return 0


COMMANDS = {"pair": cmd_pair, "join": cmd_join, "invite": cmd_invite, "devices": cmd_devices,
            "send": cmd_send, "status": cmd_status, "clean": cmd_clean, "receive": cmd_receive}


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except AttributeError:
            pass
    parser = argparse.ArgumentParser(prog="t7drop", description=__doc__)
    parser.add_argument("--home", help="the profile folder (default: T7DROP_HOME, or %%APPDATA%%\\t7drop-cli)")
    commands = parser.add_subparsers(dest="command", required=True)
    c = commands.add_parser("pair", help="pair the first device of a group, with a code from t7drop-admin")
    c.add_argument("url", help="the server URL, https://<host>/<prefix>")
    c.add_argument("code")
    c.add_argument("--name", default=platform.node() or "laptop")
    c = commands.add_parser("join", help="pair with an invitation from a paired device")
    c.add_argument("invitation", help="t7drop:pair?...")
    c.add_argument("--name", default=platform.node() or "device")
    c = commands.add_parser("invite", help="show an invitation (QR code) that pairs a new device")
    c.add_argument("--png", help="save the QR code as this PNG file instead of printing it")
    commands.add_parser("devices", help="list the paired devices")
    c = commands.add_parser("send", help="queue files or a text, and try to send them now")
    c.add_argument("files", nargs="*")
    c.add_argument("--text")
    c.add_argument("--to", help="the recipient's name or id; needed when there are several")
    c.add_argument("--queue-only", action="store_true", help="queue only; 'receive' sends it")
    commands.add_parser("status", help="list the items sent and their states")
    commands.add_parser("clean", help="forget items delivered or failed")
    c = commands.add_parser("receive", help="send what is queued and receive what is waiting")
    c.add_argument("--watch", action="store_true", help="keep doing so, until Ctrl+C")
    c.add_argument("--dir", default=default_downloads(), help="where files go (default: %(default)s)")
    args = parser.parse_args(argv)

    profile = Profile(args.home or default_home())
    if args.command not in ("pair", "join") and not profile.paired:
        print("Not paired yet (no %s); run 'pair' or 'join' first." % profile.config_path, file=sys.stderr)
        return 2
    try:
        return COMMANDS[args.command](profile, args)
    except TransientError as e:
        print("Server not reachable: %s" % e, file=sys.stderr)
        return 3
    except ApiError as e:
        print("The server refused: %s" % e, file=sys.stderr)
        return 4
