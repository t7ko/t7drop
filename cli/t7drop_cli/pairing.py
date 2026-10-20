"""The pairing invitation a paired device shows (as a QR code) to a new device:
t7drop:pair?v=1&u=<server URL>&c=<one-time code>&k=<group key, base64url>"""

import base64
import urllib.parse


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def from_b64url(text):
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def make_uri(url, code, key):
    return "t7drop:pair?" + urllib.parse.urlencode({"v": "1", "u": url, "c": code, "k": b64url(key)})


def parse_uri(uri):
    """Returns (url, code, key); raises ValueError if this is not a t7drop invitation."""
    parts = urllib.parse.urlsplit(uri.strip())
    if parts.scheme != "t7drop" or parts.path != "pair":
        raise ValueError("not a t7drop pairing invitation")
    query = {k: v[-1] for k, v in urllib.parse.parse_qs(parts.query).items()}
    if query.get("v") != "1":
        raise ValueError("unknown invitation version %r" % query.get("v"))
    try:
        key = from_b64url(query["k"])
        url, code = query["u"], query["c"]
    except (KeyError, ValueError) as e:
        raise ValueError("incomplete invitation") from e
    if len(key) != 32 or not url.startswith(("https://", "http://")):
        raise ValueError("damaged invitation")
    return url, code, key
