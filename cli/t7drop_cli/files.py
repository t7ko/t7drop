"""File names from other devices, made safe for this computer."""

import os

_FORBIDDEN = set('<>:"/\\|?*')
_RESERVED = ({"CON", "PRN", "AUX", "NUL"} | {"COM%d" % i for i in range(1, 10)}
             | {"LPT%d" % i for i in range(1, 10)})
MAX_NAME = 150


def safe_name(name):
    """The last path part of `name`, without characters or names that Windows forbids."""
    name = str(name or "").replace("\\", "/").rsplit("/", 1)[-1]
    name = "".join("_" if c in _FORBIDDEN or ord(c) < 32 else c for c in name)
    name = name.strip().rstrip(". ")
    if not name:
        name = "file"
    if name.split(".", 1)[0].upper() in _RESERVED:
        name = "_" + name
    if len(name) > MAX_NAME:
        root, ext = os.path.splitext(name)
        ext = ext[:20]
        name = root[:MAX_NAME - len(ext)] + ext
    return name


def unique_path(folder, name):
    """folder/name, or folder/name (1), (2)... if that exists."""
    root, ext = os.path.splitext(name)
    path = os.path.join(folder, name)
    n = 1
    while os.path.exists(path):
        path = os.path.join(folder, "%s (%d)%s" % (root, n, ext))
        n += 1
    return path
