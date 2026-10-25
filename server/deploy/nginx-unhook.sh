#!/bin/bash
# t7drop: takes t7drop out of nginx and leaves the service running; the undo of step 2,
# nginx-hook.sh (docs/deployment.md). Run as root through run-on-vps.sh, without
# --upload-code:
#
#   nginx-unhook.sh [<web hostname> <prefix> [<second hostname>]]
#
# It copies /etc/nginx/snippets/includes.conf and /etc/nginx/t7drop/ to /root/t7drop-backup/,
# removes t7drop's two lines from includes.conf and the folder /etc/nginx/t7drop/, runs
# nginx -t and reloads nginx. With the hostname and prefix it also records the HTTP status
# of the site's paths before and after, as nginx-hook.sh does, and checks that t7drop no
# longer answers at the prefix. If nginx -t fails or a status changes, it puts both back and
# reloads. Undo: nginx-hook.sh again; or copy the two backups back, nginx -t, systemctl
# reload nginx.
#
# T7DROP_TEST_ROOT and T7DROP_TEST_BIN are for tests/test_deploy.py only (see deploy.sh).

set -u
export LC_ALL=C
export PATH="${T7DROP_TEST_BIN:+$T7DROP_TEST_BIN:}/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
R="${T7DROP_TEST_ROOT:-}"

HOST="${1:-}"
PREFIX="${2:-}"
OTHER="${3:-}"
SNIPPET="$R/etc/nginx/snippets/includes.conf"
SERVER_FILE="$R/etc/nginx/sites-enabled/$HOST"
MARKER="# t7drop: the next line is t7drop's; its uninstall script removes both lines."
INCLUDE="include /etc/nginx/t7drop/*.conf;"
CONF_DIR="$R/etc/nginx/t7drop"
BACKUP_DIR="$R/root/t7drop-backup"
PROBE="/t7drop-probe-$$"
WORK="$(mktemp -d)"

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*"; rm -rf "$WORK"; exit 1; }

say "Checks"
[ "$(id -u)" = 0 ] || fail "run as root"
if [ -n "$HOST" ] || [ -n "$PREFIX" ] || [ -n "$OTHER" ]; then
    [[ "$HOST" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]] || fail "bad hostname: $HOST"
    [[ "$PREFIX" =~ ^[A-Za-z0-9]{8,40}$ ]] || fail "give the hostname and the prefix (8-40 letters and digits)"
    [ -f "$SERVER_FILE" ] || fail "no $SERVER_FILE"
fi
[ -f "$SNIPPET" ] || fail "no $SNIPPET"
if ! grep -qxF -e "$MARKER" -e "$INCLUDE" "$SNIPPET" && [ ! -e "$CONF_DIR" ]; then
    say "Nothing to do"
    echo "no t7drop lines in $SNIPPET, and no $CONF_DIR"
    rm -rf "$WORK"
    exit 0
fi
nginx -t 2>/dev/null || fail "nginx -t fails already, before any change; nothing done"
echo "ok"

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

if [ -n "$HOST" ]; then
    say "Status codes before (web hostname, second hostname, path)"
    statuses > "$WORK/before"
    cat "$WORK/before"
fi

say "Change"
mkdir -p "$BACKUP_DIR"
stamp="$(date +%Y%m%d-%H%M%S)"
backup="$BACKUP_DIR/includes.conf.$stamp"
cp -a "$SNIPPET" "$backup"
echo "backup: $backup"
conf_backup=""
if [ -e "$CONF_DIR" ]; then
    conf_backup="$BACKUP_DIR/nginx-t7drop.$stamp"
    cp -a "$CONF_DIR" "$conf_backup"
    echo "backup: $conf_backup"
fi

restore() {
    cat "$backup" > "$SNIPPET"
    if [ -n "$conf_backup" ]; then
        rm -rf "$CONF_DIR"
        cp -a "$conf_backup" "$CONF_DIR"
    fi
    if nginx -t 2>/dev/null; then
        systemctl reload nginx
        echo "PUT BACK: $SNIPPET and $CONF_DIR as they were, nginx reloaded"
    else
        echo "PUT BACK, BUT nginx -t still fails: check by hand (backups: $backup ${conf_backup:-})"
    fi
}

grep -vxF -e "$MARKER" -e "$INCLUDE" "$backup" > "$WORK/includes.conf"
cat "$WORK/includes.conf" > "$SNIPPET"
rm -rf "$CONF_DIR"
echo "removed t7drop's lines from $SNIPPET, and $CONF_DIR"
if ! nginx -t 2>&1; then
    restore
    fail "nginx -t failed without t7drop's lines"
fi
systemctl reload nginx || { restore; fail "nginx reload failed"; }
echo "nginx reloaded"

if [ -n "$HOST" ]; then
    sleep 3
    say "Status codes after"
    statuses > "$WORK/after"
    cat "$WORK/after"
    if ! diff -u "$WORK/before" "$WORK/after"; then
        restore
        fail "status codes changed (above)"
    fi
    echo "same as before"

    say "t7drop through nginx"
    if code=$(t7drop-admin check-code 2>/dev/null); then
        headers=$(curl -s -D - -o /dev/null --max-time 10 --resolve "$HOST:443:127.0.0.1" \
            -H "Authorization: Check $code" "https://$HOST/$PREFIX/api/v1/ping")
        echo "with a check code: $(echo "$headers" | head -1 | tr -d '\r')"
        if echo "$headers" | grep -qi '^x-t7drop-instance'; then
            fail "t7drop still answers at https://$HOST/<prefix>/: did the reload go through?"
        fi
        echo "t7drop no longer answers at the prefix"
    else
        echo "t7drop-admin is not there: not checked"
    fi
fi

rm -rf "$WORK"
say "Done"
echo "t7drop.service still runs, on loopback only. To hook it in again: nginx-hook.sh (with"
echo "--upload-code), or put back $backup${conf_backup:+ and $conf_backup}, nginx -t, systemctl reload nginx."