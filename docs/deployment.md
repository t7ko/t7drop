# t7drop: deployment on the VPS

The plan agreed in milestone M0 for milestone M2. The VPS is in production for other
services behind nginx; nothing of them may break. Addresses, hostnames, the URL prefix
and the other services' details are not in this repository.

## What the VPS has

- nginx takes port 443 as a `stream` server that routes by SNI. The web hostname goes to
  an nginx `http` server on an internal port (with PROXY protocol); other names go to
  another service, which hands some connections on to a second `http` server.
- The web hostname's server block is not a normal file: another tool writes it from a
  fixed template every time that tool starts (at boot, daily, and when its settings
  change), then runs `systemctl restart nginx`. Anything added to that file disappears
  within a day.
- Both `http` servers include `/etc/nginx/snippets/includes.conf`, which nothing rewrites.
  That is where t7drop hooks in.
- The web server's own settings that a new location inherits: every 400-404 and 500-504
  error is turned into a 404 (`error_page` with `proxy_intercept_errors on`); bodies are
  limited to 1 MB; regex locations exist.
- Python 3.12 without pip or venv; `python3-cryptography` is installed from Ubuntu
  (certbot needs it). The firewall lets in only the ports in use; t7drop needs no new one.
  apport takes core dumps through a pipe. No swap.

## What t7drop adds, and nothing else

| Path | What |
| --- | --- |
| system user `t7drop` | Runs the service; no shell, no login |
| `/opt/t7drop/` | The server code; owned by root, read-only for the service. `deploy.sh` keeps the previous version in `/opt/t7drop.old`, and one that failed its check in `/opt/t7drop.failed` |
| `/etc/t7drop/server.conf` | Port, limits |
| `/var/lib/t7drop/` | `devices.json`, `invites.json`, `status.json` and the chunk files; mode 0700, owned by `t7drop` (`StateDirectory`) |
| `/etc/systemd/system/t7drop.service` | The service |
| `/usr/local/sbin/t7drop-admin` | The admin command, for root: runs `admin.py` as `t7drop` with `runuser` |
| `/etc/nginx/t7drop/t7drop.conf` | t7drop's `location` block |
| two lines at the end of `/etc/nginx/snippets/includes.conf` | A marker comment and `include /etc/nginx/t7drop/*.conf;` |
| `/root/t7drop-backup/` | Copies the nginx steps make before changing anything |

Nothing is installed with apt or pip. The other services, the tool that writes the web
server block, the stream configuration, the certificates, the firewall and SSH stay as
they are.

### The service

```ini
[Service]
User=t7drop
ExecStart=/usr/bin/python3 -E -s /opt/t7drop/server/main.py /etc/t7drop/server.conf
StateDirectory=t7drop
StateDirectoryMode=0700
UMask=0077
Restart=always
RestartSec=2
MemoryMax=200M
LimitCORE=1
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
IPAddressAllow=localhost
IPAddressDeny=any
```

- M5 added `SystemCallFilter=@system-service`, `SystemCallErrorNumber=EPERM`,
  `ProtectProc=invisible`, `ProcSubset=pid` and `RemoveIPC=yes` (`docs/security.md`, D1);
  `check.sh` prints `systemd-analyze security`'s rating.
- It listens on 127.0.0.1:8765 only, and cannot open connections to anywhere else.
- `-E -s`: Python ignores `PYTHON*` environment variables and the user's site packages;
  the script's own folder, owned by root, is the only addition to the import path.
- `LimitCORE=1`: for a piped `core_pattern` (apport) the kernel skips the dump when the
  limit is 1, and for a file it is too small for any dump; so the key that lives only in
  memory never lands on disk. The server also marks itself non-dumpable at start.

### The nginx location

```nginx
# t7drop: written by t7drop's deploy script; its uninstall step removes it.
location ^~ /<prefix>/ {
    if ($server_port != <web server port>) { return 404; }
    proxy_pass http://127.0.0.1:8765/;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $proxy_protocol_addr;
    proxy_intercept_errors off;
    client_max_body_size 5m;
    client_body_buffer_size 5m;
    proxy_max_temp_file_size 0;
    proxy_read_timeout 90s;
    proxy_send_timeout 90s;
}
```

- `^~` keeps the regex locations away; the prefix is random letters and digits, not
  digits only.
- The `if` answers 404 when the request reached the second `http` server, which
  includes the same snippet: t7drop answers only on the web hostname.
- `proxy_intercept_errors off`: t7drop's own status codes reach the clients. Errors nginx
  itself makes (t7drop down, for example) still become the site's 404, without
  `X-T7drop-Instance`, so clients treat them as temporary. A chunk upload that meets t7drop
  down comes back as 413 instead: the site's error page sends the 4 MiB request on to a
  location with the 1 MB limit. Also without the header, so also temporary (seen during
  the restart test of M2).
- `client_body_buffer_size 5m` and `proxy_max_temp_file_size 0`: chunks pass through
  nginx in memory, never through its temporary files on disk.
- `proxy_read_timeout 90s`: long polls hold for 50 s.

## Steps

Each step is a script in `server/deploy/` that the user runs through `run-on-vps.sh`,
after the agent has said what it changes and how to undo it, and the user said yes. Each
one prints what it did and checks the result. Code reaches the VPS as a tar file of the
committed `server/` folder (`run-on-vps.sh --upload-code`), copied with scp in the same
SSH agent session.

| Script | Step |
| --- | --- |
| `install.sh` | 1: install the service, once; with `--upload-code` |
| `nginx-hook.sh <hostname> <prefix> <port> [<second hostname>]` | 2: the nginx location and include line; with `--upload-code` (the template `nginx-location.conf` comes with the code) |
| `deploy.sh [<hostname> <prefix>]` | Upgrades the code, checks it, rolls back by itself if the check fails; with `--upload-code` (see "Upgrades and rollback") |
| `deploy.sh --rollback [<hostname> <prefix>]` | Back to the previous version, with the same check; without `--upload-code` |
| `nginx-unhook.sh [<hostname> <prefix> [<second hostname>]]` | Undoes step 2 only: t7drop leaves nginx, the service keeps running |
| `admin.sh <arguments>` | Runs `t7drop-admin`, such as `admin.sh invite laptop` |
| `restart.sh` | Restarts `t7drop.service` only; its queue is lost, clients upload again |
| `check.sh [<hostname> <prefix>]` | Any time: service, devices, queue, nginx lines; through nginx if given the hostname and prefix |
| `uninstall.sh` | Undo everything: the nginx lines and file, the service, code, data, user |
| `t7drop.service`, `t7drop-admin`, `server.conf` | Installed by `install.sh` |

1. **Install the service** (`install.sh`). Creates the user, the folders, the config, the
   unit; starts the service. Check: the service is active, answers on 127.0.0.1:8765 (404
   without a token, 200 with a check code), is not dumpable, has a core limit of 1, uses
   under 100 MB. Nothing shared is touched. Once the service is installed, it refuses to
   run again: upgrades go through `deploy.sh`. Undo: `uninstall.sh`.
2. **Hook into nginx.**
   - Copies `includes.conf` to `/root/t7drop-backup/`.
   - Before the change, records the HTTP status of the site root and of every path in
     `includes.conf` and the web server block (requests to 127.0.0.1 with the right
     hostname; status codes only).
   - Writes `/etc/nginx/t7drop/t7drop.conf`, appends the two lines to `includes.conf`.
   - Runs `nginx -t`; if that fails, puts the backup back and stops.
   - Runs `systemctl reload nginx` (existing connections continue).
   - Records the statuses again; if any of them changed, puts the backup back, reloads and
     says so.
   - Checks t7drop through the public URL with a one-time check token from
     `t7drop-admin`.

   Undo: `nginx-unhook.sh` (see "Upgrades and rollback"); by hand: remove the two lines
   (or restore the backup), delete `/etc/nginx/t7drop/`, `nginx -t`, reload. The script
   (`nginx-hook.sh`) undoes its change by itself when `nginx -t`
   fails, a status changes, or t7drop does not answer through nginx. It records the
   statuses through port 443 both with the web hostname and with a hostname of the other
   service,
   whose fallback server includes the same snippet.
3. **Pair two CLI devices and transfer** (M2's tests).

A `check` script verifies at any time that the include line is there, the service runs
and the public URL answers. An `uninstall` script undoes steps 2 and 1.

## Upgrades and rollback

- **Upgrade:** `run-on-vps.sh --upload-code root@<VPS> deploy.sh <hostname> <prefix>`.
  It checks the running version first; if t7drop answers locally but not through nginx, it
  stops before any change, since then nginx needs fixing, not t7drop. It unpacks the new
  code into `/opt/t7drop.new` and makes sure it compiles, imports and reads
  `/etc/t7drop/server.conf` with the VPS's Python as the user `t7drop`; a failure here
  changes nothing. Then the running code becomes `/opt/t7drop.old`, the new code
  `/opt/t7drop`, the unit and `t7drop-admin` are installed from it, and the service
  restarts.
- **The check**, before and after: the service runs; it answers 404 without a token, and 200
  with `X-T7drop-Instance` to a one-time check code, locally and through nginx; `t7drop-admin`
  reads the devices; the process is not dumpable and has a core limit of 1; after the
  restart it keeps the same process for 5 seconds.
- **Automatic rollback:** if the new version fails the check, the previous code, unit and
  admin command come back (the failed code stays in `/opt/t7drop.failed`, for a look), the
  service restarts and is checked again. The script says which version runs at the end.
- **Rollback by hand**, for a problem the check cannot see (the apps fail, say):
  `run-on-vps.sh root@<VPS> deploy.sh --rollback <hostname> <prefix>` swaps `/opt/t7drop` and
  `/opt/t7drop.old`, unit and admin command included, with the same check; it swaps back if
  the check fails. A second `--rollback` goes forward again.
- **nginx:** `run-on-vps.sh root@<VPS> nginx-unhook.sh <hostname> <prefix> <second hostname>`
  copies `includes.conf` and `/etc/nginx/t7drop/` to `/root/t7drop-backup/`, removes the two
  lines and the folder, runs `nginx -t` and reloads. Like `nginx-hook.sh`, it records the
  statuses of the site's paths before and after, and puts everything back if `nginx -t`
  fails or a status changes. The service keeps running on loopback, unreachable from
  outside. To hook it in again: `nginx-hook.sh`, or copy the two backups back, `nginx -t`,
  `systemctl reload nginx`.
- Every restart empties the queue: senders upload again (see `docs/design.md`,
  "Reliability"). A rollback does not touch `/etc/t7drop/server.conf` or the data in
  `/var/lib/t7drop`, so **a new server version must keep `devices.json` and `invites.json`
  readable by the previous one**, and accept settings the previous one did not know only
  with defaults.
- `deploy.sh` reads the whole script before it starts and ignores a dropped SSH connection,
  so an upgrade or a rollback never stops half-way. `tests/test_deploy.py` runs it and
  `nginx-unhook.sh` in a scratch folder with stand-ins for systemctl, runuser and nginx
  (Linux only: WSL): a good upgrade, code that does not compile or import, versions that
  die after 2 seconds, have a broken admin command, are dumpable or lack the core limit, a
  rollback there and back, and nginx not passing t7drop on.

## Risks

- **A broken t7drop file in nginx would stop nginx at the next restart by the other tool
  (at the latest the next day), and with it the other services.** Only the deploy scripts
  write that file, always followed by `nginx -t`. When the file is removed, the include pattern matches nothing,
  which nginx accepts.
- **Reinstalling or updating the other tool may rewrite `includes.conf`.** Then
  t7drop stops answering; the other services are unaffected. `check` notices; step 2 runs again.
- **nginx restarts daily with the other tool**, so transfers in flight break then.
  Clients resume them.
- **Memory:** about 50 MB of the 640 MB available, at most 200 MB.
