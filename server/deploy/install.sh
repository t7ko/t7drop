#!/bin/bash
# t7drop, deployment step 1 (docs/deployment.md): installs the service, once. Upgrades go
# through deploy.sh, which checks the new version and rolls back by itself; this script
# refuses to run once t7drop.service is installed.
#
# Run as root on the VPS through `run-on-vps.sh --upload-code <host> install.sh`, which
# first copies the committed server code to /root/t7drop-server.tar.gz and passes that
# path and the commit id.
#
# What it changes: creates the user t7drop (once); puts the code in /opt/t7drop; writes
# /etc/t7drop/server.conf only if it is missing; writes /etc/systemd/system/t7drop.service
# and /usr/local/sbin/t7drop-admin; enables and starts t7drop.service. It touches nothing
# else: not nginx, not the other services, not the firewall. Undo: uninstall.sh.

set -u
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

TARBALL="${1:?usage: install.sh <server tarball> <commit>}"
COMMIT="${2:-unknown}"
PORT=8765

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*"; exit 1; }

say "Checks"
[ "$(id -u)" = 0 ] || fail "run as root"
[ -f "$TARBALL" ] || fail "no $TARBALL"
[ ! -f /etc/systemd/system/t7drop.service ] \
    || fail "t7drop is installed already; upgrade it with deploy.sh (run-on-vps.sh --upload-code <host> deploy.sh)"
python3 -c 'import sys, cryptography; assert sys.version_info >= (3, 12); print("python", sys.version.split()[0], "cryptography", cryptography.__version__)' \
    || fail "python3 3.12 or newer with cryptography is needed"
command -v runuser >/dev/null || fail "runuser is missing"
if ss -ltnH "sport = :$PORT" | grep -q . && ! systemctl is-active --quiet t7drop; then
    fail "port $PORT is taken by another program"
fi
echo "ok"

say "User"
if getent passwd t7drop >/dev/null; then
    echo "user t7drop exists"
else
    useradd --system --user-group --home-dir /var/lib/t7drop --no-create-home --shell /usr/sbin/nologin t7drop \
        || fail "useradd"
    echo "created the system user t7drop"
fi

say "Code (commit $COMMIT)"
rm -rf /opt/t7drop.new
mkdir -p /opt/t7drop.new
tar -xzf "$TARBALL" -C /opt/t7drop.new || fail "cannot unpack $TARBALL"
[ -f /opt/t7drop.new/server/main.py ] || fail "no server/main.py in the upload"
echo "$COMMIT" > /opt/t7drop.new/COMMIT
chown -R root:root /opt/t7drop.new
chmod -R u=rwX,go=rX /opt/t7drop.new
if [ -d /opt/t7drop ]; then
    rm -rf /opt/t7drop.old
    mv /opt/t7drop /opt/t7drop.old
    echo "the previous version ($(cat /opt/t7drop.old/COMMIT 2>/dev/null)) is kept in /opt/t7drop.old"
fi
mv /opt/t7drop.new /opt/t7drop
python3 -m compileall -q /opt/t7drop/server >/dev/null || fail "the code does not compile"
rm -f "$TARBALL"
echo "installed /opt/t7drop"

say "Configuration"
install -d -m 0750 -o root -g t7drop /etc/t7drop
if [ -f /etc/t7drop/server.conf ]; then
    echo "kept /etc/t7drop/server.conf; differences from the default:"
    diff -u /etc/t7drop/server.conf /opt/t7drop/server/deploy/server.conf && echo "(none)"
else
    install -m 0640 -o root -g t7drop /opt/t7drop/server/deploy/server.conf /etc/t7drop/server.conf
    echo "wrote /etc/t7drop/server.conf"
fi
install -m 0644 -o root -g root /opt/t7drop/server/deploy/t7drop.service /etc/systemd/system/t7drop.service
install -m 0755 -o root -g root /opt/t7drop/server/deploy/t7drop-admin /usr/local/sbin/t7drop-admin
systemctl daemon-reload
echo "wrote /etc/systemd/system/t7drop.service and /usr/local/sbin/t7drop-admin"
systemd-analyze verify /etc/systemd/system/t7drop.service 2>&1 | grep -i t7drop

say "Service"
systemctl enable t7drop 2>&1
systemctl restart t7drop
for _ in $(seq 20); do
    if systemctl is-active --quiet t7drop && curl -s -o /dev/null "http://127.0.0.1:$PORT/"; then
        break
    fi
    sleep 0.5
done
if ! systemctl is-active --quiet t7drop; then
    journalctl -u t7drop -n 30 --no-pager
    fail "the service did not start"
fi
systemctl status t7drop --no-pager -n 0

say "Checks of the running service"
echo "without a token: HTTP $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/api/v1/sync") (expected: 404)"
code=$(t7drop-admin check-code) || fail "t7drop-admin check-code"
echo "with a check code:"
curl -s -D - -o /dev/null -H "Authorization: Check $code" "http://127.0.0.1:$PORT/api/v1/ping" \
    | grep -i -e '^HTTP' -e '^x-t7drop-instance'
pid=$(systemctl show -p MainPID --value t7drop)
echo "process $pid runs as $(ps -o user= -p "$pid"); /proc/$pid/status belongs to $(stat -c %U "/proc/$pid/status") (root: not dumpable)"
grep 'Max core file size' "/proc/$pid/limits"
memory=$(systemctl show -p MemoryCurrent --value t7drop)
case "$memory" in
    *[!0-9]* | "") echo "memory: $memory" ;;
    *) echo "memory: $((memory / 1024 / 1024)) MB (limit 200 MB)" ;;
esac
ls -la /var/lib/t7drop
journalctl -u t7drop -n 5 --no-pager -o cat

say "Done"
echo "t7drop $COMMIT runs on 127.0.0.1:$PORT. This step does not touch nginx."
echo "Undo: uninstall.sh, through run-on-vps.sh."
