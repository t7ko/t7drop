"""The repository's own text files: no invisible format characters (docs/security.md).

Bidirectional controls such as U+202E can make code read differently from what it does,
and other format characters hide in names. Tests that need them write them as escapes.
Android lint checks this for Kotlin only; this covers every tracked file.
"""

import os
import subprocess
import unicodedata

import pytest

import helpers

BINARY = (".jar", ".dll", ".png", ".ico", ".p12", ".apk", ".exe", ".meta", ".data")
BOM = chr(0xFEFF)  # allowed as the first character of a file only


def tracked_files():
    try:
        out = subprocess.run(["git", "-C", helpers.ROOT, "ls-files", "-z"], capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        pytest.skip("git is not available here")
    return [name for name in out.decode("utf-8").split("\0") if name and not name.endswith(BINARY)]


def test_no_tracked_text_file_holds_invisible_format_characters():
    found = []
    for name in tracked_files():
        path = os.path.join(helpers.ROOT, name)
        if not os.path.isfile(path):
            continue  # deleted in the working tree
        with open(path, "rb") as f:
            data = f.read()
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            continue  # not text
        if text.startswith(BOM):
            text = text[1:]
        for number, line in enumerate(text.splitlines(), 1):
            marks = sorted({"U+%04X" % ord(c) for c in line if unicodedata.category(c) == "Cf"})
            if marks:
                found.append("%s:%d: %s" % (name, number, ", ".join(marks)))
    assert found == [], "\n".join(found)
