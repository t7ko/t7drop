#!/usr/bin/env python3
"""t7drop-admin: pairing codes and devices of the t7drop server, on the VPS.

  t7drop-admin invite <name>      a one-time code that pairs the first device (10 minutes);
                                  a device paired with it may invite further devices
  t7drop-admin check-code         a one-time code for GET /ping, to check the public URL
  t7drop-admin devices            the paired devices
  t7drop-admin revoke <id|name>   removes a device; its token stops working at once
  t7drop-admin status             the running server's queue

It works on the files in the data folder, so it must run as the service's user: on the
VPS, /usr/local/sbin/t7drop-admin does that with runuser.
"""

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from t7drop_server import config as config_module  # noqa: E402
from t7drop_server.registry import Registry, format_code  # noqa: E402


def main(argv=None):
    parser = argparse.ArgumentParser(prog="t7drop-admin", description=__doc__.split("\n\n")[0])
    parser.add_argument("--config", default="/etc/t7drop/server.conf")
    commands = parser.add_subparsers(dest="command", required=True)
    invite = commands.add_parser("invite", help="a one-time code that pairs a first device")
    invite.add_argument("name", help="a name for the device, such as laptop")
    invite.add_argument("--minutes", type=int, default=10)
    commands.add_parser("check-code", help="a one-time code for checking the public URL")
    commands.add_parser("devices", help="the paired devices")
    revoke = commands.add_parser("revoke", help="removes a device")
    revoke.add_argument("device", help="its id, or its name")
    commands.add_parser("status", help="the running server's queue")
    args = parser.parse_args(argv)

    config = config_module.load(args.config)
    registry = Registry(config.data_dir)
    if args.command == "invite":
        code = registry.create_invite("pair", name=args.name, ttl=args.minutes * 60)
        print("Pairing code for %s: %s (valid for %d minutes, once)" % (args.name, format_code(code), args.minutes))
    elif args.command == "check-code":
        print(format_code(registry.create_invite("check", ttl=120)))
    elif args.command == "devices":
        devices = registry.devices()
        for d in devices:
            paired = time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime(d["created"]))
            role = "may invite devices" if d["can_invite"] else "invited"
            print("%s  %-24s  paired %s  %s" % (d["id"], d["name"], paired, role))
        if not devices:
            print("No devices are paired.")
    elif args.command == "revoke":
        try:
            device = registry.revoke(args.device)
        except ValueError as e:
            print(e, file=sys.stderr)
            return 1
        if device is None:
            print("No device %r." % args.device, file=sys.stderr)
            return 1
        print("Revoked %s (%s)." % (device["name"], device["id"]))
    elif args.command == "status":
        path = os.path.join(config.data_dir, "status.json")
        try:
            with open(path, encoding="utf-8") as f:
                print(json.dumps(json.load(f), indent=2))
        except FileNotFoundError:
            print("No status yet: is the server running?", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
