#!/usr/bin/env python3
"""Looks for anything that gives access to the VPS or to t7drop in what git would publish.

This repository is public. Run this before every commit and every push:

  python tools/scan_private.py              the working tree (tracked and new files)
  python tools/scan_private.py --history    also every file and commit message in history

It checks for:
- the private strings in local/private-strings.txt (git-ignored; one per line, # comments);
- every nginx location path found in the reports in local/ (the VPS's private paths);
- the tokens and keys of CLI profiles named in local/private-strings.txt as "profile: <dir>";
- generic patterns: IPv4 addresses, e-mail addresses, personal paths, private keys,
  bearer tokens, pairing invitations with a key.

It prints where it found something, never the value itself, and exits with 1 if it did.
"""

import argparse
import glob
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL = os.path.join(ROOT, "local")

ALLOWED_IPS = {"127.0.0.1", "0.0.0.0", "1.1.1.1", "8.8.8.8", "1.0.0.0"}  # 1.0.0.0: the version in app manifests
ALLOWED_IP_PREFIXES = ("192.0.2.", "198.51.100.", "203.0.113.", "10.", "192.168.")
ALLOWED_EMAILS = {"noreply@anthropic.com", "ivan.t7ko@gmail.com"}
# The public test key (bytes 0..31) of testdata/vectors and the unit tests.
TEST_KEY = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"

PATTERNS = [
    ("IPv4 address", re.compile(rb"(?<![\d.])(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(?![\d.])")),
    ("e-mail address", re.compile(rb"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
    ("personal path", re.compile(rb"(?i)(C:[\\/]+Users[\\/]+\w+|[A-Z]:[\\/]+t7ko|I:[\\/]+Syncthing|/home/\w+|/mnt/[a-z]/)")),
    ("private key", re.compile(rb"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("bearer token", re.compile(rb"Bearer [A-Za-z0-9_-]{30,}")),
    ("pairing invitation with a key", re.compile(rb"t7drop:pair\?[^\s\"']*k=[A-Za-z0-9_-]{20,}")),
]


def private_strings():
    """The literal strings that must not appear, from the git-ignored local/ folder."""
    found = set()
    profiles = []
    listed = os.path.join(LOCAL, "private-strings.txt")
    if os.path.exists(listed):
        with open(listed, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if line.startswith("profile:"):
                    profiles.append(line[len("profile:"):].strip())
                else:
                    found.add(line)
    else:
        print("warning: no %s; only the reports and the generic patterns are checked" % listed)
    location = re.compile(r"location\s+(?:[=~^*]+\s*)?\^?/([A-Za-z0-9_-]{6,})")
    generic = {"assets"}
    for report in glob.glob(os.path.join(LOCAL, "*.txt")):
        with open(report, encoding="utf-8", errors="replace") as f:
            found.update(p for p in location.findall(f.read()) if p.lower() not in generic)
    for profile in profiles:
        try:
            with open(os.path.join(profile, "config.json"), encoding="utf-8") as f:
                config = json.load(f)
        except (OSError, ValueError):
            print("warning: cannot read the profile %s" % profile)
            continue
        found.update(v for k, v in config.items() if k in ("token", "key", "url", "device_id") and v)
    return sorted(found)


def git(*args):
    return subprocess.run(["git", "-C", ROOT] + list(args), capture_output=True, check=True).stdout


def tree_files():
    names = git("ls-files", "-z", "--cached", "--others", "--exclude-standard").split(b"\0")
    for name in filter(None, names):
        path = os.path.join(ROOT, name.decode())
        if os.path.isfile(path):
            with open(path, "rb") as f:
                yield name.decode(), f.read()


def history_blobs():
    seen = set()
    for commit in git("rev-list", "--all").split():
        for line in git("ls-tree", "-r", commit.decode()).splitlines():
            meta, name = line.split(b"\t", 1)
            blob = meta.split()[2].decode()
            if blob not in seen:
                seen.add(blob)
                yield "%s (%s in %s)" % (name.decode(), blob[:8], commit.decode()[:8]), git("cat-file", "blob", blob)
    yield "commit messages", git("log", "--all", "--format=%H %an <%ae> %cn <%ce>%n%B")


def check(label, data, literals):
    problems = []
    for literal in literals:
        if literal.encode() in data:
            problems.append("private string %s..." % literal[:3])
    for name, pattern in PATTERNS:
        for match in pattern.finditer(data):
            value = match.group(0).decode(errors="replace")
            if name == "IPv4 address":
                ip = match.group(1).decode()
                if ip in ALLOWED_IPS or ip.startswith(ALLOWED_IP_PREFIXES) or any(int(p) > 255 for p in ip.split(".")):
                    continue
            if name == "e-mail address" and value.lower() in ALLOWED_EMAILS:
                continue
            if name == "pairing invitation with a key" and value.endswith("k=" + TEST_KEY):
                continue
            problems.append("%s %s..." % (name, value[:4]))
    for problem in sorted(set(problems)):
        print("%s: %s" % (label, problem))
    return bool(problems)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--history", action="store_true", help="also scan all of git history")
    args = parser.parse_args()
    literals = private_strings()
    print("checking %d private strings and %d patterns" % (len(literals), len(PATTERNS)))
    bad = False
    sources = [tree_files()] + ([history_blobs()] if args.history else [])
    count = 0
    for source in sources:
        for label, data in source:
            count += 1
            bad |= check(label, data, literals)
    print("%d files scanned: %s" % (count, "PROBLEMS FOUND" if bad else "nothing found"))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
