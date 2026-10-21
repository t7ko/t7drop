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

Status: in development. The server, a command-line client, the Android app and the Windows
tray app work. The design is in [docs/design.md](docs/design.md).

## Windows app

`T7Drop.exe` from the [releases](https://github.com/t7ko/t7drop/releases) is the whole app:
put it in a folder you can write to (it replaces itself with new releases) and start it.
It needs Windows 10 or 11 (x64) and the Visual C++ runtime, which most computers have.
Windows warns about programs from the internet without a signature; choose More info, Run
anyway. At the first start it asks for the server address and a pairing code from
`t7drop-admin invite <name>`, then shows a QR code that pairs the phone.

- Received files go to the Downloads folder; texts and links show in a notification (click:
  copy, or open the link) and in the history window (click the tray icon).
- Send: Send clipboard in the tray menu (text, a picture, or copied files), Send To in
  Explorer's menu, or drop files and text on the history window.

To build it: `windows\scripts\build.ps1` (nothing to install; it uses the C# compiler of
Windows), which also runs its tests.

## Tests

```
python -m pip install -r requirements-dev.txt
python -m pytest
```
