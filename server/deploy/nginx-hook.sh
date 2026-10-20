#!/bin/bash
# t7drop, deployment step 2 (docs/deployment.md): hooks t7drop into nginx.
#
#   nginx-hook.sh <web hostname> <prefix> <web server port> [<second hostname>]
#
# Run as root on the VPS through `run-on-vps.sh --upload-code`, which puts the tarball
# path and the commit id first (the location template comes from the tarball).
#
# What it changes: writes /etc/nginx/t7drop/t7drop.conf (the location block, from
# nginx-location.conf) and appends two lines (a marker comment and an include) to
# /etc/nginx/snippets/includes.conf, after copying that file to /root/t7drop-backup/.
# Then nginx -t and a reload. Before and after, it records the HTTP status of the site's
# root and of every path in includes.conf and the web server block, through port 443 as
# clients come (and with the second hostname, whose server includes the same snippet).
# If nginx -t fails, a
# status changes, or t7drop does not answer through nginx, it puts everything back and
# reloads. Undo later: uninstall.sh.

set -u
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

TARBALL="${1:?tarball}"
COMMIT="${2:?commit}"
HOST="${3:?usage: nginx-hook.sh <tarball> <commit> <web hostname> <prefix> <port> [<second hostname>]}"
PREFIX="${4:?prefix}"
PORT="${5:?port}"
OTHER="${6:-}"

SNIPPET=/etc/nginx/snippets/includes.conf
SERVER_FILE="/etc/nginx/sites-enabled/$HOST"
MARKER="# t7drop: the next line is t7drop's; its uninstall script removes both lines."
INCLUDE="include /etc/nginx/t7drop/*.conf;"
CONF_DIR=/etc/nginx/t7drop
BACKUP_DIR=/root/t7drop-backup
PROBE="/t7drop-probe-$$"
WORK="$(mktemp -d)"

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*"; rm -rf "$WORK"; exit 1; }

say "Checks"
[ "$(id -u)" = 0 ] || fail "run as root"
[[ "$PREFIX" =~ ^[A-Za-z0-9]{8,40}$ ]] && ! [[ "$PREFIX" =~ ^[0-9]+$ ]] || fail "the prefix must be 8-40 letters and digits, not digits only"
[[ "$PORT" =~ ^[0-9]+$ ]] || fail "bad port"
[ -f "$SNIPPET" ] || fail "no $SNIPPET"
[ -f "$SERVER_FILE" ] || fail "no $SERVER_FILE"
grep -q "listen $PORT " "$SERVER_FILE" || fail "$SERVER_FILE does not listen on $PORT"
systemctl is-active --quiet t7drop || fail "t7drop.service does not run (step 1)"
nginx -t 2>/dev/null || fail "nginx -t fails already, before any change; nothing done"
tar -xzf "$TARBALL" -C "$WORK" server/deploy/nginx-location.conf || fail "no template in $TARBALL"
rm -f "$TARBALL"
echo "ok (code $COMMIT)"

paths() {
    echo /
    echo /404
    echo "$PROBE"
    grep -hE '^\s*location\s' "$SNIPPET" "$SERVER_FILE" | sed -E 's/^\s*location\s+(=\s*|\^~\s*)?//' \
        | grep -E '^/' | awk '{print $1}'
    grep -hoE '^\s*location\s+~\*?\s+\^/[A-Za-z0-9_-]+' "$SNIPPET" "$SERVER_FILE" | grep -oE '/[A-Za-z0-9_-]+$'
}

statuses() {
    paths | sort -u | while read -r path; do
        a=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 --resolve "$HOST:443:127.0.0.1" "https://$HOST$path")
        b=-
        if [ -n "$OTHER" ]; then
            b=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 --resolve "$OTHER:443:127.0.0.1" "https://$OTHER$path")
        fi
        printf '%s %s %s\n' "$a" "$b" "$path"
    done
}

say "Status codes before (web hostname, second hostname, path)"
statuses > "$WORK/before"
cat "$WORK/before"

say "Change"
mkdir -p "$BACKUP_DIR"
backup="$BACKUP_DIR/includes.conf.$(date +%Y%m%d-%H%M%S)"
cp -a "$SNIPPET" "$backup"
echo "backup: $backup"

rollback() {
    cat "$backup" > "$SNIPPET"
    rm -rf "$CONF_DIR"
    if nginx -t 2>/dev/null; then
        systemctl reload nginx
        echo "ROLLED BACK: $SNIPPET restored, $CONF_DIR removed, nginx reloaded"
    else
        echo "ROLLED BACK, BUT nginx -t still fails: check by hand (backup: $backup)"
    fi
}

install -d -m 0755 -o root -g root "$CONF_DIR"
sed -e "s/@PREFIX@/$PREFIX/g" -e "s/@PORT@/$PORT/g" "$WORK/server/deploy/nginx-location.conf" > "$CONF_DIR/t7drop.conf"
chmod 0644 "$CONF_DIR/t7drop.conf"
echo "wrote $CONF_DIR/t7drop.conf"
if grep -qxF "$INCLUDE" "$SNIPPET"; then
    echo "the include line is in $SNIPPET already"
else
    [ -z "$(tail -c 1 "$SNIPPET")" ] || echo >> "$SNIPPET"
    printf '%s\n%s\n' "$MARKER" "$INCLUDE" >> "$SNIPPET"
    echo "appended two lines to $SNIPPET"
fi
if ! nginx -t 2>&1; then
    rollback
    fail "nginx -t failed with the change"
fi
systemctl reload nginx || { rollback; fail "nginx reload failed"; }
echo "nginx reloaded"
sleep 3

say "Status codes after"
statuses > "$WORK/after"
cat "$WORK/after"
if ! diff -u "$WORK/before" "$WORK/after"; then
    rollback
    fail "status codes changed (above)"
fi
echo "same as before"

say "t7drop through nginx"
code=$(t7drop-admin check-code)
headers=$(curl -s -D - -o /dev/null --max-time 10 --resolve "$HOST:443:127.0.0.1" \
    -H "Authorization: Check $code" "https://$HOST/$PREFIX/api/v1/ping")
echo "$headers" | grep -i -e '^HTTP' -e '^x-t7drop-instance'
if ! echo "$headers" | grep -qi '^x-t7drop-instance'; then
    rollback
    fail "t7drop did not answer at https://$HOST/$PREFIX/"
fi
echo "without a token: HTTP $(curl -s -o /dev/null -w '%{http_code}' --resolve "$HOST:443:127.0.0.1" "https://$HOST/$PREFIX/api/v1/sync") (expected: 404)"
if [ -n "$OTHER" ]; then
    code=$(t7drop-admin check-code)
    headers=$(curl -s -D - -o /dev/null --max-time 10 --resolve "$OTHER:443:127.0.0.1" \
        -H "Authorization: Check $code" "https://$OTHER/$PREFIX/api/v1/ping")
    echo "with the second hostname: $(echo "$headers" | head -1 | tr -d '\r') (expected: 404, t7drop not reached)"
    if echo "$headers" | grep -qi '^x-t7drop-instance'; then
        echo "warning: t7drop answered with the second hostname too"
    fi
fi
nginx -T 2>/dev/null | grep -n -e 't7drop' | head

rm -rf "$WORK"
say "Done"
echo "t7drop answers at https://$HOST/$PREFIX/ (clients add /api/v1/...)."
echo "Undo: uninstall.sh through run-on-vps.sh (removes the service too), or restore $backup,"
echo "delete $CONF_DIR, nginx -t, systemctl reload nginx."
