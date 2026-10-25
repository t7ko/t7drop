# Notes for AI coding agents: t7drop

Files, images, text and links between the user's Android phone and Windows laptop through
a relay on the user's VPS, end-to-end encrypted. The design, the protocol draft and the
milestones are in `docs/design.md`; read it before changing anything.

## **THIS REPOSITORY IS PUBLIC: NEVER PUBLISH ANYTHING THAT GIVES ACCESS**

**The repository goes to the user's public GitHub (`t7ko/t7drop`), and its releases are
public too. Never put into a commit, a commit message, a release or a release note
anything that gives access to the VPS, to the other services on it, or to t7drop, or that points
at them:**

- **the VPS's IP address, hostnames, domain, or its hoster;**
- **t7drop's URL prefix, and any private path or port of the VPS's nginx or of the other
  services on it; also what those services are (keep this repository generic about them);**
- **tokens, keys, passwords, pairing or check codes, invitations (`t7drop:pair?...`),
  device or instance ids;**
- **SSH details, and any output of the scripts in `server/deploy/` (they write it to the
  git-ignored `local/`);**
- **this machine's paths and names.**

**These belong only in the git-ignored `AGENTS.local.md` and `local/`. Before every commit
run `python tools/scan_private.py`, and before every push `python tools/scan_private.py
--history`. It reads the private strings from the git-ignored `local/private-strings.txt`:
add every new code, key, path or name there as soon as it appears. If the scan finds
anything, do not commit or push: fix it first, and rewrite any commit that has it before
the push. Once pushed, assume it is public forever.**

## Status

- 2026-10-20: design agreed (`docs/design.md`); M0 done: two read-only reports on the VPS
  and the deployment plan (`docs/deployment.md`), approved by the user.
- 2026-10-20: M1 done: the server and the CLI, with tests (43 on Windows; the server's 21
  also on Linux with the VPS's Python 3.12 and cryptography 41).
- 2026-10-20: M2 steps 1 and 2 done on the VPS: t7drop.service runs, nginx passes the
  prefix to it; all other paths answer as before. Only devices paired by the admin (the
  first device) may invite others: the user's rule.
- 2026-10-20: M2 tests through the public URL with two CLI devices: texts both ways,
  20 MB and 200 MB files; an upload and a download killed half-way resume where they
  stopped; a service restart (`restart.sh`) during a transfer of two 200 MB files: both
  delivered once, identical. About 1.6 MB/s each way from the user's laptop.
  Memory: the process peaked at 64 MB (VmHWM), 33 MB after; the cgroup's 113 MB peak was
  mostly page cache. M2 done; the user confirmed the other services work after the nginx
  change.
- 2026-10-21: M3, the Android app, works on the emulator against a local server: pairing
  by link, texts and files both ways (share sheet, file picker), 200 MB each way,
  airplane mode with the app killed (delivered 17 s after the network returned), the
  app killed with kill -9 during an upload (resumed with the missing chunks only), an
  update during a download (finished, identical). The encryption tests pass on the
  emulator with the app's libsodium.
- 2026-10-21: v0.1.0 published on GitHub (t7ko/t7drop); the user's phone (Redmi Note 12
  Pro, HyperOS) installed it through Obtainium and paired by scanning the CLI's QR code
  against the real server. On the phone: a photo shared from the gallery reached the
  laptop intact; a text with a link, a text file and 20 MB reached the phone (Copy, Open
  link, Open work); a text shared in airplane mode with the app swiped away went out by
  itself about 2 minutes later, once the network was back, without opening the app. That
  phone's HyperOS has no Autostart setting; Battery saver "No restrictions" was enough.
  Skipped by the user's choice: a very large file over mobile data. M3 done with v0.1.1.
- 2026-10-21: M4, the Windows tray app: the end-to-end tests (`tests/test_windows.py`) and,
  against a local server, a test copy (pairing window, QR code, Send To, Send clipboard,
  offline and back, self-update through `fake-release-server.js`). Then on the real server
  with the user's phone: the app paired with an admin code, the phone unpaired and scanned
  its QR code, and `laptop-cli` and the phone's old entry were revoked (the phone's 0.1.1
  sent to the oldest device that may invite: fixed in 0.2.0 with `Recipients.pick`). Texts,
  links and files both ways, Send clipboard, Send To, dropping on the history window,
  "delivered" only after the phone took the item; after 4.5 minutes of Modern Standby with
  the lid closed, a text from the phone arrived without a click. Windows 11 shows Send To
  only under "Show more options". The user's choices: received files go to Downloads itself
  on both apps, Escape closes the history window (both in 0.2.0).
- 2026-10-21/22: v0.2.0 published (`t7drop-0.2.0.apk` and `T7Drop.exe`). The user's 0.1.1
  updated itself from the GitHub release through Check for updates, and the phone took the
  APK through Obtainium. M4 done.
- 2026-10-22: M5 agreed with the user, item by item, each with its own tests and commit:
  1. deploy and rollback in one step; 2. a security review (threat model and findings in
  `docs/security.md`, fixes, tests; the Windows exe gets an Ed25519 signature that the
  updater checks); 3. robustness left over from M1 to M4. No optional extras (no periodic
  check on Android, no R8). Item 1: `deploy.sh` and `nginx-unhook.sh`, tested with
  stand-ins in WSL (`tests/test_deploy.py`, 16 tests); the user chose no installation in
  WSL, so the first real run is on the VPS.
- 2026-10-25: `deploy.sh` upgraded the VPS (the M2 code to the item 1 commit), checked
  locally and through nginx. Item 2: `docs/security.md` (threat model, findings S1-S7,
  D1-D2, C1-C9) and its fixes: the server (refusals of `/pair` look like the site, check codes
  count against pairing, 6 chunk transfers at once, revoked devices' items dropped, a
  self-test of the chunk store), the sandbox (system-call filter, private `/proc`), the
  Windows app (signed self-update with a release key made with the user's OK, the Mark of
  the Web on received files at the user's choice, the pipe closed to the network), names
  without invisible format characters on all clients, Android (share only other apps'
  content, the pairing form names the server, no redirects, no device-to-device copy of
  its data). `tests/test_sources.py` keeps invisible characters out of the repository.
  Commit bb0ff3b, deployed with `deploy.sh` (sandbox rating 1.3, OK). A test that needed a
  restart to show as "not reachable" was timing-dependent: the agent now logs the quiet
  path too. Item 3: refused ids kept on disk for 8 days, stale partial downloads and
  unfinished decryptions removed after a day, delivered items after 30 days, on all three
  clients (`docs/design.md`, "Reliability"). With it M5 is done. The app changes of items 2
  and 3 reach the devices with the next release (0.3.0, the first with a signed exe), made
  only when the user asks.

## Working with the VPS

- The VPS is in production for other services behind nginx (`AGENTS.local.md` names
  them). Never change them, the firewall or SSH; never restart a service other than
  `t7drop`. nginx
  changes go only into the isolated file the user approved, are checked with `nginx -t`,
  and are applied with `systemctl reload nginx`.
- The agent has no SSH access. It writes scripts in `server/deploy/`; the user runs them in
  the Terminal panel through `server/deploy/run-on-vps.sh`, which saves their output in
  `local/`; the agent reads it there.
- Before any step that changes the VPS: tell the user what it changes and how to roll it
  back, and wait for their yes.
- `local/` is git-ignored (Syncthing still syncs it). Its reports contain the VPS's nginx
  configuration, which can include private URL paths: never copy them into tracked files.

## Layout

- `docs/design.md`: architecture, encryption, protocol, milestones.
- `docs/deployment.md`: what the deployment adds to the VPS, the steps, the risks.
- `docs/security.md`: the threat model, and the M5 review's findings and fixes.
- `server/`: the relay. `main.py` runs it, `admin.py` is `t7drop-admin`; the package
  `t7drop_server/`: `protocol` (constants), `config`, `registry` (devices and codes in
  files), `blobs` (chunk files under the in-memory key), `hub` (the queue in memory),
  `api` (HTTP), `app` (wiring). It needs only the standard library and `cryptography`.
- `server/deploy/`: scripts the user runs against the VPS through `run-on-vps.sh`:
  `install.sh` (step 1, once) and `nginx-hook.sh` (step 2), both with `--upload-code`;
  `deploy.sh` (upgrades with a check and an automatic rollback, with `--upload-code`;
  `--rollback` goes back by hand), `nginx-unhook.sh` (undoes step 2 only), `check.sh`,
  `admin.sh`, `restart.sh`, `uninstall.sh`; and what they install: `t7drop.service`,
  `t7drop-admin`, `server.conf`, `nginx-location.conf`. `docs/deployment.md` lists them.
  Scripts that name the VPS's other services (the M0 inspections) are in the git-ignored
  `local/scripts/`; `run-on-vps.sh` finds them there.
- `cli/`: the command-line client, `t7drop.py`; package `t7drop_cli/`: `crypto`
  (secretstream), `pairing` (the invitation URI), `api`, `state` (the profile folder),
  `agent` (sending, receiving, retries), `files` (safe names), `main` (commands).
- `tests/`: pytest; `helpers.py` starts servers in-process. `make_vectors.py` wrote
  `testdata/vectors/`, which every client must decrypt (and whose listed changes it must
  refuse); the Android and Windows apps test against the same files.
- `tools/scan_private.py`: the check for private data before commits and pushes (see the
  top of this file).
- `android/`: the Android app (Kotlin, Compose), its own Gradle project; `CLAUDE.md`
  there imports the t7helpers Android notes (toolchain, emulator). In
  `app/src/main/java/t7helpers/t7drop/`: `Protocol`, `Pairing` (the invitation),
  `crypto/ItemCrypto` (secretstream, as the CLI), `net/Api`, `data/Store` (pairing,
  outbox, received list) and `data/Secrets` (Keystore), `transfer/Transfers` (the logic
  of the CLI's agent) and `transfer/TransferWorker` (WorkManager, `Scheduler.kick`),
  `Sender` (queueing shares), `Notifications`, `ui/` (MainActivity with the long poll,
  PairScreen, HomeScreen, Cards, ShareActivity). Unit tests in `app/src/test/`; the
  encryption tests run on a device (`app/src/androidTest/`, with `testdata/vectors` as
  assets): `gradlew.bat connectedDebugAndroidTest`. `scripts/build.ps1` builds the signed
  release APK into `dist/`; `scripts/emulator-ui.py` taps through the app over adb.
- Android release builds are signed with a key kept outside the repository; the git-ignored
  `android/local.properties` names it (`release.storeFile`, `release.storePasswordFile`,
  `release.keyAlias`). Certificate SHA-256:
  `3da8fbdde68e4afaae3e738842743d7fc82534a3f59b6703f7e53f6cddcb79dc`. Every update must use
  this key, or the app has to be uninstalled and loses its data.
- `windows/` (M4): the tray app `T7Drop.exe`; see "Windows app" below. `windows/CLAUDE.md`
  imports the t7helpers Windows notes (toolchain, trying tray apps).
- `scripts/push.ps1` pushes to GitHub with the token (after `scan_private.py --history`);
  `scripts/release.ps1` publishes release `v<versionName>` with the APK for Obtainium, and
  `T7Drop.exe` with `T7Drop.exe.sig` for the Windows app's updater: it signs with the release
  key that `windows.signKeyFile` in the root `local.properties` names. Every release from
  0.3.0 on needs that key, or the Windows apps refuse it. `versionName` in
  `android/app/build.gradle.kts` is the one version of both apps.

## Windows app (`windows/`)

- C# 5 on .NET Framework 4.8, compiled by Windows' own `csc.exe` (x64). `scripts/build.ps1`
  writes `obj/Version.cs` from `versionName`, builds `obj/T7Drop.exe` and `obj/Tests.exe`,
  runs the unit tests (`Tests.exe test <exe> <repository>`) and copies the exe to the
  repository's `dist/T7Drop.exe`.
- `src/`: `Program` (entry point: `--send <files>` from Explorer's Send To, `--exit`),
  `Instance` (one copy per user and data folder; a second start hands "send", "show" or
  "exit" to the first through a named pipe only this user may open), `TrayApp` (icon, menu,
  notifications, restarts after waking and network changes, the update flow), `Worker` (the
  loop on its own thread: long poll, backoff; `Kick` ends a long poll or a pause, `Restart`
  also cuts a transfer in flight), `Transfers` (the CLI agent's logic), `Api` (HttpClient),
  `ItemCrypto` and `Sodium` (libsodium through P/Invoke), `Store` (the data folder; the
  pairing under DPAPI), `Settings` (also `Paths`, `Log`), `Pairing`, `Names`, `QrCode`
  (Project Nayuki's encoder, ported), `Sender` (queues what the user sends; folders as zip),
  `Shell` (links, the clipboard, the Send To shortcut), the windows `PairForm`, `QrForm`,
  `HistoryForm`, `SettingsForm` (built in code, sized in em; `Ui` has the helpers), `Icons`;
  `Updater`, `SleepWatcher` and `Autostart` come from ping-tray. `Updater` (M5) takes only a
  release with `T7Drop.exe.sig`, the Ed25519 signature of the exe under the release key
  (`ReleaseKeyHex` is its public half); `windows/tests/Signing.cs` makes the key once
  (`Tests.exe keygen`) and signs (`Tests.exe sign <key file> <exe>`, which refuses any other
  key). `Files.MarkFromInternet` (in `Json.cs`) gives received files the Mark of the Web.
- `lib/libsodium.dll`: the official libsodium 1.0.22 MSVC x64 build (`lib/README.md`: source,
  signature, hashes). Embedded in the exe, written to `lib\<hash>\` in the data folder and
  loaded with LoadLibrary before any P/Invoke. It needs `VCRUNTIME140.dll`; without it the app
  names the Visual C++ Redistributable. The mingw build needs a libgcc DLL it does not ship.
- Data in `%LOCALAPPDATA%\T7Drop`: `pairing.dat` (DPAPI, current user), `settings.ini`,
  `devices.json`, `outbox\<id>\` (item.json, meta.bin, data.bin, as the CLI), `incoming\`,
  `received.json`, `history.json`, `t7drop.log` (ids and sizes, no names or texts).
  `T7DROP_HOME` makes a test copy: its own data, single-instance name, and download folder
  (`<home>\downloads`), and it leaves Start with Windows and Send To alone.
- Tests: `windows/tests/Tests.cs` (vectors, QR symbols against segno's in `qr-vectors.txt`,
  names and invitations as the CLI makes them, store, queueing, updater) and
  `windows/tests/Agent.cs`: `Tests.exe agent <home> pair|join|invite|send-file|send-text|
  status|retry|cancel|run ...` drives the engine without windows. `tests/test_windows.py`
  runs it against an in-process server with the CLI as the phone (both ways, a lost ack,
  upload and download resumed after a kill, a server restart, retry to a re-paired phone,
  cancel); it is skipped until `Tests.exe` is built. `windows/tests/make_qr_vectors.py`
  writes the QR vectors; segno pads differently from the standard when data ends on a
  codeword boundary, so it patches in the standard padding.
- Decisions:
  - The first start turns on Start with Windows and the Send To entry (Settings turn them
    off). A second start of the exe shows the running copy's history window.
  - Recipient: the device chosen in Settings; otherwise, on the first device, the newest
    device paired after it (the phone it invited, not an old entry of another group).
  - Queueing writes the item into `outbox\.<id>.new` and renames the folder, so the worker
    never sees half an item. JSON and the pairing are replaced by a MoveFileEx rename over
    the old file, and readers share delete access, so a reader never sees a missing or
    partial file and never blocks a writer (`File.Replace` did both).
  - A received file is decrypted into a hidden `.<name>.<id8>.t7drop-part` in the download
    folder and renamed when complete.
  - Waking (Kernel-Power 507 or PowerModeChanged, 1 s after the last) and network changes
    (2 s after the last) call `Worker.Restart`: a long poll on a connection that died in
    standby would otherwise hang until its 70 s time-out.
  - A text's notification opens it if the whole text is one http(s) link, else copies it.
    Only http(s) links are ever opened. Auto-copy is a setting, off by default.
  - Send clipboard takes copied files first, then text, then a picture (as PNG): Office puts
    a picture of copied text beside it.
- Trying it (the recipes and this machine's paths are in `AGENTS.local.md`): a local server,
  a test copy of `dist/T7Drop.exe` with `T7DROP_HOME`, the CLI as the phone; `T7Drop.exe
  --exit` ends a copy. WinForms text boxes show as panes in UI Automation, whose automation
  id is the window handle: WM_SETTEXT and BM_CLICK work on them. Self-update:
  `windows/scripts/fake-release-server.js` serves a `latest.json` and a higher-version exe
  with its `.sig`; start a copy with `T7DROP_RELEASES_URL=http://127.0.0.1:<port>/latest`;
  the check runs a minute after start. The higher version: compile `src/*.cs` with csc as
  `build.ps1` does, with a `Version.cs` of its own; sign it with the real release key
  (`Tests.exe sign`; there is no test key in the app). Tried that way on 2026-10-25: a signed
  0.2.1 installed itself, a damaged signature was refused at download, and a release without
  `.sig` was not offered.

## Code rules

- Server code must run on Ubuntu 24.04's Python 3.12 with `cryptography` 41.0.7, the
  versions on the VPS: no newer syntax or APIs. `requirements-dev.txt` pins that version.
- A rollback (`deploy.sh`) brings back older code over the same data and `server.conf`: a
  new server version must keep `devices.json` and `invites.json` readable by the previous
  one, and give any new setting a default.
- A response without `X-T7drop-Instance` is not from t7drop. Clients treat it as a
  temporary failure, never as "unpaired" or "item gone".
- Tests: `python -m pytest` in a venv with `requirements-dev.txt` (where it is on this
  machine: `AGENTS.local.md`); `tests/test_windows.py` needs `windows/scripts/build.ps1` first.
  `tests/test_deploy.py` runs only on Linux (WSL, as an ordinary user, in `/tmp`): it runs
  the deploy scripts with stand-ins for systemctl, runuser and nginx.

## Toolchains

The notes for the Android and Windows toolchains are in `../android/AGENTS.md` and
`../windows/AGENTS.md` (outside this repository). `android/CLAUDE.md` and
`windows/CLAUDE.md` should import them when those folders are created, and the Gradle
output folders of `android/` need lines in the Syncthing ignore file, like quick-alarms
has (`AGENTS.local.md` says where it is).
