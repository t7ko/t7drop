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
- `server/`: the relay. `main.py` runs it, `admin.py` is `t7drop-admin`; the package
  `t7drop_server/`: `protocol` (constants), `config`, `registry` (devices and codes in
  files), `blobs` (chunk files under the in-memory key), `hub` (the queue in memory),
  `api` (HTTP), `app` (wiring). It needs only the standard library and `cryptography`.
- `server/deploy/`: scripts the user runs against the VPS through `run-on-vps.sh`:
  `install.sh` (step 1) and `nginx-hook.sh` (step 2), both with `--upload-code`,
  `check.sh`, `admin.sh`, `restart.sh`, `uninstall.sh`; and what they install:
  `t7drop.service`, `t7drop-admin`, `server.conf`, `nginx-location.conf`.
  `docs/deployment.md` lists them. Scripts that name the VPS's other services (the M0
  inspections) are in the git-ignored `local/scripts/`; `run-on-vps.sh` finds them there.- `cli/`: the command-line client, `t7drop.py`; package `t7drop_cli/`: `crypto`
  (secretstream), `pairing` (the invitation URI), `api`, `state` (the profile folder),
  `agent` (sending, receiving, retries), `files` (safe names), `main` (commands).
- `tests/`: pytest; `helpers.py` starts servers in-process. `make_vectors.py` wrote
  `testdata/vectors/`, which every client must decrypt (and whose listed changes it must
  refuse); the Android and Windows apps test against the same files.
- `tools/scan_private.py`: the check for private data before commits and pushes (see the
  top of this file).
- `android/` (M3), `windows/` (M4): the apps.

## Code rules

- Server code must run on Ubuntu 24.04's Python 3.12 with `cryptography` 41.0.7, the
  versions on the VPS: no newer syntax or APIs. `requirements-dev.txt` pins that version.
- A response without `X-T7drop-Instance` is not from t7drop. Clients treat it as a
  temporary failure, never as "unpaired" or "item gone".
- Tests: `python -m pytest` in a venv with `requirements-dev.txt` (where it is on this
  machine: `AGENTS.local.md`).

## Toolchains

The notes for the Android and Windows toolchains are in `../android/AGENTS.md` and
`../windows/AGENTS.md` (outside this repository). `android/CLAUDE.md` and
`windows/CLAUDE.md` should import them when those folders are created, and the Gradle
output folders of `android/` need lines in the Syncthing ignore file, like quick-alarms
has (`AGENTS.local.md` says where it is).
