# t7drop: threat model and security review

Milestone M5, 2026-10-25: the server, its deployment, the CLI, the Android app and the
Windows app, reviewed against the threat model below. The findings and what was done about
them follow. The design is in `docs/design.md`, the deployment in `docs/deployment.md`.

## What t7drop protects

- The items: the content of files and texts, and their meta (names, types, sizes).
- The group key K, which reads and makes items, and the device tokens, which reach the
  server.
- The VPS and its other services: t7drop must not open a way into them.
- The laptop and the phone: t7drop must not become a way to run code on them.

## Who might attack, and what they can do

| Who | Can | Cannot |
| --- | --- | --- |
| Anyone on the internet | Reach the site's hostname | Find t7drop without its prefix: every unknown path gives the site's own answer |
| Someone who learned the URL prefix (from a log, say) | Tell that something answers there: t7drop's 404 page is nginx's standard one, not the site's own. Try pairing and check codes: 10 wrong ones an hour for all devices together, then pairing locks for an hour (so they can keep it locked) | Anything else: without a token every request gets the 404 page, including refused pairing requests (fixed in M5) |
| Someone on the network path | See sizes and times of TLS traffic | Read or change it (Let's Encrypt certificate, checked by the clients) |
| Whoever controls the VPS (its hoster, or someone who broke in) | See the metadata (sizes, times, device names, who sends to whom); delete, withhold or replay items; revoke or add devices | Read or forge items: K never reaches the server, every message is authenticated and bound to its item id and stream. A replay of an item a receiver saved in the last 30 days is only acknowledged again; an older one would be saved a second time (accepted) |
| A paired device, or one stolen before it is revoked | Read what the group sends; send files to the other devices | Stay in after it is revoked: its token stops at once, and its items leave the server within 30 s (M5). The others should then pair again with a new K (`docs/design.md`, "Pairing") |
| Another app on the phone | Share content to t7drop, which sends it to the laptop (the share screen shows); open a `t7drop:` link that fills in the pairing form | Make t7drop send its own private files or any `file://` path (M5); pair without the user's tap, and the form now names the server the link leads to (M5) |
| Whoever can publish releases on GitHub (the account, or a leaked token) | Publish a release | Get it installed: Android takes an APK only with the app's signing key; the Windows app, from 0.3.0 on, only an exe signed with its release key (M5). Nor an older version: the updater takes only a higher one, whose exe must carry that version |
| A program running as the user on the laptop | Everything the user can, including reading the DPAPI-protected pairing | Out of scope: t7drop cannot protect against its own user |

## Findings

Severity: what an attacker in the table above could gain, and how easily. "Fixed" means
fixed in M5, with a test.

### Server

| # | Finding | Severity | Done |
| --- | --- | --- | --- |
| S1 | Refusals of `POST /pair` (not JSON, a body over 16 KiB, a chunked body) came as t7drop's own 400, 411 or 413 with `X-T7drop-Instance`, not as the site's 404: t7drop showed itself to anyone with the prefix | Low | Fixed: any refusal of `/pair` is the 404 page |
| S2 | Wrong check codes (`Authorization: Check`, for `GET /ping`) were not counted against the pairing lock; and every wrong pairing or check code rewrote and synced `invites.json`, a disk write for anyone with the prefix | Low | Fixed: they count with wrong pairing codes (10 an hour), and the file is written only when a code is used up or has expired |
| S3 | No limit on chunk transfers at once: with 64 connections of 4 MiB each (three copies in memory while one is stored) a device could push the process past `MemoryMax=200M`; the restart empties the queue | Low (needs a token) | Fixed: at most 6 at once (`max_transfers`); the others wait up to 30 s, then get 503 `busy`, which clients retry |
| S4 | A revoked device's items stayed on the server until they expired (up to 7 days), and what it had sent was still delivered | Low | Fixed: the janitor drops items from and to unpaired devices every 30 s |
| S5 | Deeply nested JSON raised `RecursionError`: a 500 and a traceback in the log | Low | Fixed: 400 `bad_json` |
| S6 | Any error in the janitor (a full disk at `status.json`, say) ended its thread for good, and with it the expiry of items | Low | Fixed: it logs the error and goes on |
| S7 | Nothing proved at start that the encryption of stored chunks works in the sandbox | Hardening | Added: the server stores, reads and deletes a test chunk before it serves; `deploy.sh`'s check therefore covers it |

Checked and in order: tokens are 256 random bits, stored as SHA-256; pairing codes have 10
symbols of 31 (about 49 bits), last 10 minutes and work once; the token is checked before a
body is read, and each route has its own body limit; item ids must be random UUIDs before
they name a folder, chunk numbers and lengths are checked, and an item's space is reserved
when it is created; the log has method, route, status, device name and duration, never
tokens, meta, names or texts; stored chunks use AES-256-GCM with random 96-bit nonces under
a key made at each start, far below the nonce-collision bound at these numbers. The SHA-256
of pairing codes in `invites.json` could be searched offline (49 bits), but only by someone
who already reads `/var/lib/t7drop` as root, and only for 10 minutes.

Accepted: whoever knows the prefix can tell t7drop's 404 page from the site's, and can keep
pairing locked; the server sees the metadata.

### Deployment

| # | Finding | Severity | Done |
| --- | --- | --- | --- |
| D1 | The systemd sandbox allowed every system call and showed the service the whole `/proc` | Hardening | Added `SystemCallFilter=@system-service` (a blocked call fails with EPERM), `ProtectProc=invisible`, `ProcSubset=pid`, `RemoveIPC=yes`. Not `MemoryDenyWriteExecute`: ctypes (`prctl` in `main.py`) may need libffi closures. `check.sh` prints `systemd-analyze security`'s rating |
| D2 | An upgrade had no check and no way back but by hand | Medium (availability) | `deploy.sh` (M5 item 1): checks, and rolls back by itself |

Checked and in order: the service runs as `t7drop` without capabilities, with
`ProtectSystem=strict`, `NoNewPrivileges`, its data folder at 0700, loopback only
(`IPAddressDeny=any` but localhost); `LimitCORE=1` and the non-dumpable flag keep the
in-memory key out of core dumps, which matters here because `suid_dumpable` is 2 and apport
takes dumps through a pipe. The nginx location takes only its prefix (`^~`) on the web
hostname, keeps bodies in memory, and passes t7drop's statuses through. nginx's access log
holds the prefix and item ids with client addresses (readable by root only), never tokens:
`Authorization` is not logged.

### Clients

| # | Finding | Severity | Done |
| --- | --- | --- | --- |
| C1 | The Windows app's self-update trusted GitHub alone (size and SHA-256 from the release JSON, the exe's name and version), and by default installs without asking: whoever could publish a release could run code on the laptop without a click | Medium | Fixed: `T7Drop.exe.sig`, an Ed25519 signature by the release key, whose secret half is kept off the repository (`scripts/release.ps1` signs with it). A release without it is not offered; a download it does not verify is refused. Tested on a test copy: signed, damaged and missing signatures. Versions up to 0.2.0 trust GitHub alone until they update to 0.3.0 |
| C2 | The Windows app saved received files without the Mark of the Web: a program or script from the phone, perhaps downloaded from the web there, opened without SmartScreen's warning, and Office documents fully editable | Medium | Fixed (the user's choice): received files carry `Zone.Identifier` with `ZoneId=3`, as browser downloads do |
| C3 | The Windows app's instance pipe let the same user connect from another computer | Low | Fixed: the network is denied |
| C4 | File names with invisible format characters: U+202E shows `photo<U+202E>gpj.exe` as `photoexe.jpg` | Low | Fixed in all three clients: format characters (below U+10000) are removed |
| C5 | The Android share screen, open to every app, read whatever URI it was given with t7drop's rights, including `file://` paths and t7drop's own content | Low (the files went only to the user's laptop) | Fixed: only other apps' `content://` URIs |
| C6 | A `t7drop:` link from any app or web page fills in the pairing form, which did not say which server it leads to | Low (needs the user's tap) | Fixed: the form names the server |
| C7 | The Android app followed HTTP redirects with the token (the CLI and the Windows app never did) | Low | Fixed: it follows none |
| C8 | From Android 12 on, `allowBackup="false"` no longer stops a device-to-device transfer: the app's lists, with received texts, would move to a new phone (the pairing would not open there: its Keystore key stays) | Low | Fixed: data extraction rules leave out everything |
| C9 | Nothing kept invisible characters out of the repository's own files (the agent's edit tool once wrote a real U+202E for an escape; Android lint caught it) | Hardening | `tests/test_sources.py` checks every tracked text file |

Checked and in order: the Android app exports only its main screen (launcher and `t7drop:`
links) and the share screen; WorkManager's job service and the diagnostic receivers need
system permissions; the QR scanner is not exported. Release builds allow only HTTPS. The
pairing is sealed with an AES-GCM key in the Android Keystore, and on Windows with DPAPI
(current user, with entropy). Texts open only http(s) links; received files open only on
the user's click; the Windows log has ids and sizes, no names or texts. libsodium is checked
against its SHA-256 whenever it is written out. The CLI keeps its secrets in a plain file of
its profile: it is a test tool.

## Open

- Windows installs up to 0.2.0 accept any release until they update to 0.3.0, the first
  version that checks signatures; that update itself is the last one taken on GitHub's word.
- After a device is revoked, pairing the others again with a new K is a manual step.
- Losing the Windows release key (`AGENTS.local.md` says where it is) would leave installed
  copies without updates: a new key needs a new download by hand. Like the APK's key, the
  user backs it up.
