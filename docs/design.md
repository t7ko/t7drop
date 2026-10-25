# t7drop: design

Files, images, text and links between the user's Android phone and Windows laptop,
through a relay on the user's VPS. Both devices only make outbound HTTPS requests to the
relay: no LAN discovery, no peer-to-peer connection, so the devices may sit on different
networks. Two devices, one user, a small code base.

Agreed with the user on 2026-10-20. The protocol below is a draft that milestone M1 fixes.

## Parts

| Folder | What | Stack |
| --- | --- | --- |
| `server/` | The relay on the VPS, and its deployment scripts | Python 3.12 standard library (`http.server`, threads) and Ubuntu's `python3-cryptography`, both already on the VPS; systemd; behind the existing nginx |
| `cli/` | Command-line client: the test tool, and the Windows receiver until the tray app exists | Python 3.13, httpx, PyNaCl, segno (QR codes) |
| `android/` | The Android app | Kotlin, Compose, WorkManager, a libsodium binding |
| `windows/` | The tray app `T7Drop.exe`: an app of its own, not part of ping-tray | C# 5 on .NET Framework 4.8 (`csc.exe`), `libsodium.dll` |

## Encryption

Two layers: end to end between the devices, and at rest on the server with a key that
lives only in the server's memory.

### End to end, between the devices

- All paired devices share one group key K (32 random bytes). The first device creates it;
  a new device gets it in the pairing QR code that an already paired device shows. K never
  reaches the server.
- Each item is two libsodium `crypto_secretstream_xchacha20poly1305` streams under K:
  - meta: one message (tag FINAL), JSON
    `{"v": 1, "kind": "file" | "text", "name", "mime", "size", "from", "to", "created"}`;
  - content: the file's bytes, or the text as UTF-8, in messages of 1 MiB of plaintext,
    the last one tagged FINAL. A stream is its 24-byte header and the messages
    (17 bytes of overhead each).
- Every message carries associated data `t7drop/1/meta/<item id>` or
  `t7drop/1/data/<item id>`, so the server cannot swap streams between items, or meta and
  content. Truncated, reordered or altered streams fail to decrypt.
- The sender encrypts the whole item into a local ciphertext file when it queues it, and
  keeps that file until the item is delivered. Every upload, retry or re-upload after a
  server restart sends the same bytes. The Android app deletes its plaintext copy once
  the ciphertext is written.
- The receiver downloads the ciphertext to a temporary file, decrypts it into a temporary
  file beside the target, checks the FINAL tag, renames it into place, and only then
  acknowledges.

### At rest on the server, with a key only in RAM

- At start the server creates a random 256-bit key that exists only in its memory: never
  written to disk, and the VPS has no swap. The service runs with `LimitCORE=1`, which
  makes the kernel skip core dumps even through apport's pipe, and marks itself
  non-dumpable.
- Each uploaded chunk is encrypted with AES-256-GCM under that key (random 96-bit nonce,
  associated data: item id and chunk number) before it touches the disk, and decrypted
  when downloaded.
- A restart loses the key. The server therefore deletes every chunk file when it starts,
  and its queue, kept in memory, starts empty. Senders still hold their ciphertext and
  upload again (see "Reliability"); the user accepted that a restart loses what was
  waiting on the server.
- The only data kept across restarts: the device list (id, name, SHA-256 of the token) and
  unused pairing codes (SHA-256).

## Server

- Nothing is installed on the VPS: the server uses the Python standard library and Ubuntu's
  `python3-cryptography`, which certbot already needs, so security updates come through
  unattended-upgrades. How it is deployed, and the plan for the nginx change:
  `docs/deployment.md`.
- Runs as its own user `t7drop`, as one process (the queue lives in its memory), under a
  systemd service with sandboxing: `ProtectSystem=strict`, `ReadWritePaths=` only its data
  folder, `PrivateTmp`, `NoNewPrivileges`, `MemoryMax=200M`, `LimitCORE=0`,
  `Restart=always`. It listens on loopback only.
- Reached through the existing nginx under a path of the existing hostname
  (`https://<host>/<prefix>/`): no new DNS record and no new certificate. A subdomain
  would need a certificate of its own under the shared wildcard-DNS domain, whose Let's
  Encrypt rate limits all of that domain's users share. The prefix is only unlisted; the
  tokens are the security. nginx gets one isolated include file, checked with `nginx -t`
  and applied with a reload (`docs/deployment.md`).
- Requests without a valid token get the same 404 as any unknown path of the site; so does
  any refused pairing request. The threat model and the M5 review: `docs/security.md`.
- Admin command on the VPS (`server/admin.py`, run as the service's user): create the
  first pairing code, list and revoke devices, show the queue, make a one-time code that
  checks the public URL. It works on the files in the data folder, which the server
  rereads when they change: `devices.json` (ids, names, SHA-256 of the tokens),
  `invites.json` (SHA-256 of the one-time codes, with their expiry) and `status.json`
  (written by the server every 30 s).
- The HTTP server is the standard library's `ThreadingHTTPServer`: one thread per
  connection, at most 64; long polls wait on a condition variable.
- Limits (configurable): 200 MiB per item (ciphertext), 2 GiB in total, reserved when an
  item is created, so an upload is never refused half-way; meta up to 4 KiB; chunks of
  4 MiB; unfinished uploads are dropped after 24 hours and undelivered items after 7 days;
  a delivered item leaves a small record without data for 7 days, so its sender can see
  that it was delivered. At most 6 chunk transfers at once, which keeps memory low; others
  wait up to 30 s, then get 503 and retry.
- Logs: method, route, status, sizes, device name. Never tokens, keys, meta, file names or
  text.

## Protocol v1 (draft)

Base URL `https://<host>/<prefix>/api/v1`. Every call except `/pair` carries
`Authorization: Bearer <token>`. Bodies are JSON, except chunks. Every response carries
`X-T7drop-Instance`, random for each server start, so clients notice a restart. A
response without it comes from nginx, not from t7drop (nginx turns its own errors into the
site's 404, and an unknown token gets that 404 too): clients treat it as a temporary failure
and retry; they never drop items or their pairing because of it.

| Call | Does |
| --- | --- |
| `POST /pair {code, name}` | Exchanges a one-time pairing code for `{device_id, token}`. |
| `POST /invites` | A device paired by the admin (the first device) asks for a pairing code for a new device; others get 403. |
| `GET /devices` | Paired devices: id and name. |
| `PUT /items/{id} {to, size, sha256, meta}` | Creates an item; the same call again changes nothing and returns which chunks the server has. `size` and `sha256` describe the ciphertext; `meta` is the encrypted meta stream. Small content can come inline. |
| `PUT /items/{id}/chunks/{n}` | Stores chunk n (4 MiB, the last one shorter). Sending it again replaces it with the same bytes. |
| `POST /items/{id}/commit` | The server checks size and SHA-256. Only now does the recipient see the item. |
| `GET /sync?wait=50` | Long poll: answers when something changes for this device, or after 50 s. Returns the inbox (committed items not yet acknowledged) and the states of its own items. |
| `GET /items/{id}/chunks/{n}` | Downloads chunk n. |
| `POST /items/{id}/ack` | The recipient saved the item: the server deletes the data and marks it delivered. |
| `DELETE /items/{id}` | The sender cancels an item. |

## Reliability

- The sender picks the item id (a random UUID) when it queues the item, so a retry can
  never create a second item.
- Delivered means the recipient acknowledged after decrypting and saving. Sender states:
  queued, uploading, on server, delivered; or failed (expired, rejected), with Retry.
- After a server restart (a new instance value, or an unknown item), senders create and
  upload again, with the same id, every item that is not delivered.
- Receivers remember the ids they saved (30 days). An item already saved is only
  acknowledged again, never saved twice.
- They also remember the ids they refused (another group's key, another device's, damaged)
  for 8 days, a day longer than the server keeps an item, so a refused item is never
  downloaded again, not even after the app restarts (M5).
- What is left over goes by itself (M5): a partial download of an item no longer waiting
  (cancelled, expired, or lost in a server restart, after which it may come again) once
  nothing has written it for a day, a temporary file of a decryption that never finished
  likewise (Windows and the CLI; Android itself drops a pending Downloads entry after a
  week), and a delivered item 30 days after its delivery.
- Nothing appears in a target folder until it is complete and verified.
- Clients retry with exponential backoff and jitter, from 1 second to at most 5 minutes.
  A long poll times out on the client after 70 seconds.

## Pairing

1. The first device: on the VPS, `t7drop-admin invite <name>` prints a one-time code
   (valid 10 minutes). The device takes the server URL and the code, gets its token, and
   creates K.
2. Each further device: the first device asks the server for a code and shows a QR code
   with the server URL, the code and K. The new device scans it and pairs. The QR code is
   the only way K travels. Only devices paired with a code from `t7drop-admin` may ask
   for codes (the user's rule): a device invited by another device may not invite.
3. Wrong codes: at most 10 an hour (pairing and check codes together); then pairing is
   locked for an hour.
4. A lost or stolen device is revoked on the VPS: its token stops at once, and its items
   leave the server within 30 s. The remaining devices should then pair again with a new K.

Secrets on the devices: Windows protects the token and K with DPAPI (current user);
Android wraps them with an Android Keystore key; the CLI keeps them in a file in the
user's profile (it is a test tool).

## Android app

- A share target for `ACTION_SEND` and `ACTION_SEND_MULTIPLE` (any type). It reads and
  encrypts the shared content at once, because the permission to read a shared
  `content://` URI ends with the activity, queues one WorkManager job per item and closes.
  It takes only other apps' `content://` URIs, never `file://` or t7drop's own (M5).
- Upload and download workers: network constraint, exponential backoff, and a foreground
  notification (type `dataSync`) only while a transfer runs. They survive the app being
  swiped away and the phone restarting. No permanent service.
- Main screen: the inbox (long poll while the app is open) and the sent items with their
  states, Retry and Cancel. Text: tap to copy, links open. Files: saved to
  `Downloads` through MediaStore (no storage permission), then Open or Share.
- Undelivered items are checked again (and uploaded again after a server restart) when the
  app opens, and by a worker with backoff while any are left.
- The Xiaomi card from quick-alarms: Autostart, and battery saver "No restrictions".
- libsodium: lazysodium-android 5.2 through JNA (the AAR, with its native part; the
  desktop JAR it asks for is excluded); native code for arm64-v8a and x86_64 only.
- QR scanner: zxing-android-embedded (no Google services). An invitation can also come
  as a `t7drop:pair?...` link, which opens the app.
- As built in M3 (minSdk 29): one unique WorkManager job, "transfer", does all the work
  (send, sync, receive). `Scheduler.kick` lets a running job finish and queues one more
  run after it, and replaces a job that waits out a retry pause. While the main screen
  is open it long-polls `/sync` and kicks the job whenever the device's version changes;
  closed, nothing runs until a share or the next opening. The job goes foreground (a
  "Transferring" notification, type dataSync) only for items over 1 MB; received items
  post a notification when the app is not open.
- Received files are written through MediaStore with IS_PENDING, so they appear in
  Downloads only when complete; texts stay in the app's list. The pairing (token
  and group key) is encrypted with an Android Keystore key; `allowBackup` is off.
- Sending goes to the device that may invite (the laptop) when there are several.
- Debug builds allow plain HTTP to 10.0.2.2 (the computer running the emulator), for a
  test server there; release builds only HTTPS.
- Release builds signed with the app's own key, published as GitHub releases, installed
  and updated through Obtainium.

## Windows app

- `T7Drop.exe`, a tray app of its own. It copies the patterns of ping-tray (tray icon,
  single instance, Start with Windows, self-update from GitHub, `SleepWatcher`) into its
  own source; nothing is shared with ping-tray.
- Receiving: long poll; after Modern Standby it polls again at once. Files go to a folder
  (default: Downloads), with names cleaned of what Windows forbids. Text and links
  go to the history and show a notification; clicking it copies the text or opens the
  link. Copying text automatically is a setting, off by default.
- Sending: "Send clipboard" in the tray menu (text, an image, or copied files), an entry in
  Explorer's Send To menu, and dragging onto the history window. The encrypted copy waits
  in `%LOCALAPPDATA%\T7Drop\outbox`.
- `libsodium.dll` (the official build) is embedded in the exe, written out at the first
  start and called through P/Invoke. A QR code encoder is ported into the source.
- Token and K are protected with DPAPI.
- From 0.3.0 on, the self-update takes only an exe signed with the release key: Ed25519,
  `T7Drop.exe.sig` beside `T7Drop.exe` in the release; the public half is in `Updater.cs`.
  Received files carry the Mark of the Web, as browser downloads do (M5, `docs/security.md`).
- As built in M4: the CLI agent's logic (`Transfers`) runs on a thread of its own
  (`Worker`) that long-polls `/sync` while nothing else is to be done and retries with
  backoff. A new item, a retry or a cancel ends the long poll at once; waking from sleep
  (Kernel-Power 507 for Modern Standby) and network changes also cut a transfer in flight
  and retry without waiting. libsodium is 1.0.22's MSVC x64 DLL, which needs the Visual C++
  runtime (the mingw build needs a libgcc DLL it does not include). The QR encoder is
  Project Nayuki's algorithm, checked against segno's symbols.
- The first device pairs with a code from t7drop-admin (server address, code and name in a
  window), creates K and shows the invitation as a QR code at once; it notices when a new
  device has paired. Items go to the device chosen in the settings, or else to the newest
  device paired after this one: the phone it invited, not an old device of another group.
- Received files are decrypted into a hidden `.<name>.<id>.t7drop-part` in the download
  folder and renamed when complete. Both apps save into the Downloads folder itself, not a
  subfolder (the user's choice, 2026-10-21; until 0.2.0 it was `Downloads/t7drop`). The history window lists what was received and sent,
  with the states and Retry or Cancel; dropping files or text on it sends them, and Ctrl+V
  sends the clipboard. A folder is sent as a zip.
- One copy runs per user: a second start hands Send To's files to it, or shows its history
  window, through a named pipe. Start with Windows and the Send To entry are turned on at the
  first start and can be turned off in the settings. The updater is ping-tray's.

## Releases

- One repository and one version for the Android and Windows apps: GitHub release
  `v<version>` carries `t7drop-<version>.apk`, `T7Drop.exe` and, from 0.3.0 on,
  `T7Drop.exe.sig`. Obtainium takes the APK
  (an asset filter `\.apk$` if needed); the Windows updater takes `T7Drop.exe` from the
  latest release. Should the two ever need versions of their own: tags `android-v...` and
  `windows-v...`, filtered by regular expression in Obtainium and in the updater.
- The server is deployed by scripts in `server/deploy/`, which the user runs; it is not
  part of the releases.
- No server address in the repository or in released files.

## Milestones

| # | Result | Done when |
| --- | --- | --- |
| M0 Inspection | Read-only report on the VPS; deployment plan with exact files and rollback commands | The user approves the plan |
| M1 Server and CLI, local | Server, admin command, CLI (`pair`, `send`, `receive --watch`), tests, test vectors for the encryption | Tests pass: duplicate create, interrupted and resumed upload, server killed mid-upload and clients uploading again, wrong hash refused, item hidden until commit, acknowledged item deleted, quotas, expiry, wrong or revoked token, single-use codes; chunk files on disk are unreadable; the CLI decrypts the test vectors |
| M2 Deploy | Service on the VPS behind nginx | `nginx -t` passes; the VPS's other services work before and after; 200 MiB over the internet between two CLI devices; network cut and service restart during a transfer both end in delivery; memory under 100 MB |
| M3 Android app | Pairing by QR, sharing, inbox, history; release through GitHub and Obtainium | Photo, several files, link and text from Gallery, Files and Chrome reach the CLI receiver; shared in airplane mode with the app swiped away, sent by itself later; 200 MiB over mobile data; no duplicates after forced retries; text from the laptop copies on the phone, a file saves; the app decrypts the test vectors |
| M4 Windows app | Replaces the CLI receiver | Starts at login; receives within about 2 s without a click, never a partial file; Send clipboard, Send To, drag-and-drop; "delivered" only after the phone acknowledges; receives again within seconds after the lid opens; failed items can be retried; updates itself; decrypts the test vectors |
| M5 Hardening | Deploy and rollback script, security review, optional extras (a periodic check on Android, off by default) | Per item |
