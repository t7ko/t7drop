# t7drop

Files, images, text and links between an Android phone and a Windows laptop, through a
small relay on your own server. Both devices only make outbound HTTPS requests, so they
need not be on the same network, and nothing is peer-to-peer.

- **End-to-end encrypted**: libsodium secretstream under a key that the paired devices
  share and the server never sees. The server also encrypts what it stores with a key
  that exists only in its memory, so a restart leaves nothing readable on disk.
- **Reliable**: items queue on the sender until the recipient has saved and acknowledged
  them; uploads and downloads resume; a retry never delivers twice.
- **Small**: the server is the Python standard library plus `cryptography`, behind an
  existing nginx.

Status: in development. The server and a command-line client work; the Android app and
the Windows tray app come next. The design is in [docs/design.md](docs/design.md).

## Tests

```
python -m pip install -r requirements-dev.txt
python -m pytest
```
