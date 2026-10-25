#!/bin/bash
# t7drop: upgrades the server on the VPS, checks it, and rolls back by itself when the
# check fails (docs/deployment.md, "Upgrades and rollback"). Run as root through
# run-on-vps.sh:
#
#   run-on-vps.sh --upload-code root@<VPS> deploy.sh [<web hostname> <prefix>]
#       upgrades to the committed server/ folder (the tarball and the commit id come first)
#   run-on-vps.sh root@<VPS> deploy.sh --rollback [<web hostname> <prefix>]
#       goes back to the previous version in /opt/t7drop.old; a second --rollback goes
#       forward again
#
# An upgrade unpacks the code into /opt/t7drop.new and checks that it compiles, and that it
# imports and reads /etc/t7drop/server.conf with the VPS's Python as the user t7drop. Until
# then nothing has changed. Then the running code becomes /opt/t7drop.old, the new code
# /opt/t7drop; the unit and t7drop-admin are installed from it, and the service restarts.
#
# The check: the service runs and keeps the same process for some seconds, is not dumpable
# and has a core limit of 1, answers 404 without a token and 200 with t7drop's header to a
# check code, and t7drop-admin reads the devices; with the hostname and prefix, the check
# code is also tried through nginx. The running version is checked first: if it fails only
# through nginx, the script stops before any change (nginx needs fixing, not t7drop).
#
# When the check fails, the previous code, unit and admin command come back (the failed code
# stays in /opt/t7drop.failed), the service restarts and is checked again. --rollback swaps
# /opt/t7drop and /opt/t7drop.old with the same check, and swaps back if it fails.
#
# Every restart empties the queue; clients upload again. It touches nothing else: not nginx,
# not /etc/t7drop/server.conf, not the data in /var/lib/t7drop, not the other services.
#
# T7DROP_TEST_ROOT and T7DROP_TEST_BIN are for tests/test_deploy.py only: a scratch folder in
# place of /, and stand-ins for systemctl and the like at the front of PATH.

export LC_ALL=C
export PATH="${T7DROP_TEST_BIN:+$T7DROP_TEST_BIN:}/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
R="${T7DROP_TEST_ROOT:-}"

CODE="$R/opt/t7drop"
OLD="$CODE.old"
NEW="$CODE.new"
OLDER="$CODE.older"
SWAP="$CODE.swap"
FAILED="$CODE.failed"
CONF="$R/etc/t7drop/server.conf"
UNIT="$R/etc/systemd/system/t7drop.service"
ADMIN="$R/usr/local/sbin/t7drop-admin"
STABLE_SECONDS=5
HOST=""
PREFIX=""

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*"; exit 1; }
commit_of() { cat "$1/COMMIT" 2>/dev/null || echo unknown; }

port() {
    local port
    port=$(sed -n 's/^[[:space:]]*listen[[:space:]]*=.*:\([0-9][0-9]*\)[[:space:]]*$/\1/p' "$CONF" 2>/dev/null | tail -1)
    echo "${port:-8765}"
}

target() {
    HOST="${1:-}"
    PREFIX="${2:-}"
    [ $# -le 2 ] || fail "too many arguments: $*"
    if [ -n "$HOST" ] || [ -n "$PREFIX" ]; then
        [[ "$HOST" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]] || fail "bad hostname: $HOST"
        [[ "$PREFIX" =~ ^[A-Za-z0-9]{8,40}$ ]] || fail "give the hostname and the prefix (8-40 letters and digits)"
    fi
}

# Whether a check code gets 200 and t7drop's header at a URL (curl's other arguments first).
answers() {
    local code headers
    code=$(t7drop-admin check-code) || { echo "t7drop-admin check-code failed"; return 1; }
    headers=$(curl -s -D - -o /dev/null --max-time 10 -H "Authorization: Check $code" "$@")
    echo "$headers" | grep -i -e '^HTTP' -e '^x-t7drop-instance' | tr -d '\r'
    echo "$headers" | grep -q '^HTTP/[0-9.]* 200' && echo "$headers" | grep -qi '^x-t7drop-instance:'
}

# Checks the running service; waits $1 seconds for it to prove stable. Returns 1 when t7drop
# fails, 2 when only the request through nginx fails.
check() {
    local stable="$1" port pid status started devices memory
    port=$(port)
    started=$SECONDS
    for _ in $(seq 30); do
        if systemctl is-active --quiet t7drop && curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/"; then
            break
        fi
        sleep 0.5
    done
    if ! systemctl is-active --quiet t7drop; then
        echo "check: the service is not running"
        return 1
    fi
    pid=$(systemctl show -p MainPID --value t7drop)
    memory=$(systemctl show -p MemoryCurrent --value t7drop)
    case "$memory" in
        *[!0-9]* | "") ;;
        *) memory="$((memory / 1024 / 1024)) MB (limit 200 MB)" ;;
    esac
    echo "process $pid; memory: $memory"
    status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port/api/v1/sync")
    echo "without a token: HTTP $status"
    [ "$status" = 404 ] || { echo "check: expected 404"; return 1; }
    echo "with a check code:"
    answers "http://127.0.0.1:$port/api/v1/ping" || { echo "check: no 200 with t7drop's header"; return 1; }
    devices=$(t7drop-admin devices) || { echo "check: t7drop-admin devices failed"; return 1; }
    echo "t7drop-admin devices: $(echo "$devices" | grep -c '^[0-9a-f]\{12\} ') paired"
    if [ "$(stat -c %U "/proc/$pid/status" 2>/dev/null)" != root ]; then
        echo "check: the process is dumpable (its /proc/$pid/status is not root's)"
        return 1
    fi
    if [ "$(awk '/^Max core file size/ { print $5 }' "/proc/$pid/limits" 2>/dev/null)" != 1 ]; then
        echo "check: the process's core limit is not 1: $(grep 'Max core file size' "/proc/$pid/limits" 2>&1)"
        return 1
    fi
    echo "not dumpable, core limit 1"
    while [ $((SECONDS - started)) -lt "$stable" ]; do
        sleep 1
    done
    if ! systemctl is-active --quiet t7drop || [ "$(systemctl show -p MainPID --value t7drop)" != "$pid" ]; then
        echo "check: the service stopped or restarted within $stable seconds"
        return 1
    fi
    [ "$stable" = 0 ] || echo "still the same process after $stable seconds"
    if [ -n "$HOST" ]; then
        echo "through nginx, https://$HOST/<prefix>/:"
        answers --resolve "$HOST:443:127.0.0.1" "https://$HOST/$PREFIX/api/v1/ping" \
            || { echo "check: t7drop does not answer through nginx"; return 2; }
    fi
    return 0
}

# Installs the unit and the admin command from the code in /opt/t7drop and restarts.
activate() {
    install -m 0644 -o root -g root "$CODE/server/deploy/t7drop.service" "$UNIT" \
        && install -m 0755 -o root -g root "$CODE/server/deploy/t7drop-admin" "$ADMIN" \
        && systemctl daemon-reload \
        && { systemctl reset-failed t7drop 2>/dev/null; true; } \
        && systemctl restart t7drop \
        && echo "installed the unit and t7drop-admin of $(commit_of "$CODE"); restarted t7drop"
}

preflight() {
    [ "$(id -u)" = 0 ] || fail "run as root"
    [ -d "$CODE" ] && [ -f "$UNIT" ] || fail "t7drop is not installed; install.sh does the first installation"
    for leftover in "$OLDER" "$SWAP"; do
        [ ! -e "$leftover" ] || fail "$leftover is left from an interrupted run; look at $CODE* by hand"
    done
    say "The running version: $(commit_of "$CODE")"
    check 0
    case $? in
        0) ;;
        2) fail "t7drop does not answer through nginx even now; nothing changed. Fix that first (check.sh, nginx-hook.sh), or leave out the hostname and prefix." ;;
        *) echo "WARNING: the running version fails the check; going on, but a rollback can only bring back a failing version" ;;
    esac
}

upgrade() {
    local tarball="${1:-}" commit="${2:-unknown}" before
    [ -n "$tarball" ] || fail "usage: deploy.sh <tarball> <commit> [<web hostname> <prefix>] (through run-on-vps.sh --upload-code), or deploy.sh --rollback [...]"
    shift $(($# < 2 ? $# : 2))
    target "$@"
    say "Checks"
    [ -f "$tarball" ] || fail "no $tarball"
    preflight

    say "The new code (commit $commit)"
    rm -rf "$NEW"
    mkdir -p "$NEW"
    tar -xzf "$tarball" -C "$NEW" || { rm -rf "$NEW"; fail "cannot unpack $tarball; nothing changed"; }
    rm -f "$tarball"
    [ -f "$NEW/server/main.py" ] || { rm -rf "$NEW"; fail "no server/main.py in the upload; nothing changed"; }
    echo "$commit" > "$NEW/COMMIT"
    chown -R root:root "$NEW"
    chmod -R u=rwX,go=rX "$NEW"
    python3 -m compileall -q "$NEW/server" >/dev/null \
        || { rm -rf "$NEW"; fail "the new code does not compile; nothing changed"; }
    (cd / && runuser -u t7drop -- python3 -E -s -c \
        'import sys; sys.path.insert(0, sys.argv[1]); from t7drop_server import app, config; config.load(sys.argv[2])' \
        "$NEW/server" "$CONF") \
        || { rm -rf "$NEW"; fail "the new code does not import or cannot read $CONF; nothing changed"; }
    echo "unpacked into $NEW; it compiles, imports and reads $CONF"
    if ! diff -q "$CONF" "$NEW/server/deploy/server.conf" >/dev/null; then
        echo "$CONF stays as it is; differences from the new default:"
        diff -u "$CONF" "$NEW/server/deploy/server.conf"
    fi
    if diff -q "$UNIT" "$NEW/server/deploy/t7drop.service" >/dev/null; then
        echo "the unit stays the same"
    else
        echo "the unit changes:"
        diff -u "$UNIT" "$NEW/server/deploy/t7drop.service"
    fi

    say "Switch"
    before=$(commit_of "$CODE")
    [ ! -e "$OLD" ] || mv "$OLD" "$OLDER"
    mv "$CODE" "$OLD"
    mv "$NEW" "$CODE"
    echo "$CODE: $commit; $before is kept in $OLD"
    activate
    say "Check of $commit"
    if check "$STABLE_SECONDS"; then
        rm -rf "$OLDER"
        say "Done"
        echo "t7drop $commit runs and passes the check. Back to $before: deploy.sh --rollback."
        return 0
    fi

    say "ROLLING BACK to $before"
    journalctl -u t7drop -n 30 --no-pager -o cat
    rm -rf "$FAILED"
    mv "$CODE" "$FAILED"
    mv "$OLD" "$CODE"
    [ ! -e "$OLDER" ] || mv "$OLDER" "$OLD"
    activate
    say "Check of $before"
    if check "$STABLE_SECONDS"; then
        fail "$commit failed the check (above); ROLLED BACK: $before runs again and passes. The failed code is in $FAILED."
    fi
    fail "$commit failed the check, and ROLLED BACK to $before, BUT that fails the check too: see check.sh and journalctl -u t7drop."
}

swap() {
    mv "$CODE" "$SWAP" && mv "$OLD" "$CODE" && mv "$SWAP" "$OLD"
}

rollback() {
    local from to
    target "$@"
    say "Checks"
    [ -d "$OLD" ] || fail "there is no $OLD to go back to"
    preflight
    from=$(commit_of "$CODE")
    to=$(commit_of "$OLD")

    say "Rollback from $from to $to"
    swap || fail "could not swap $CODE and $OLD; look at $CODE* by hand"
    echo "$CODE: $to; $from is kept in $OLD"
    activate
    say "Check of $to"
    if check "$STABLE_SECONDS"; then
        say "Done"
        echo "t7drop $to runs and passes the check. Forward to $from again: deploy.sh --rollback."
        return 0
    fi

    say "$to FAILS THE CHECK: back to $from"
    journalctl -u t7drop -n 30 --no-pager -o cat
    swap || fail "could not swap $CODE and $OLD; look at $CODE* by hand"
    activate
    say "Check of $from"
    if check "$STABLE_SECONDS"; then
        fail "$to failed the check (above); $from runs again and passes."
    fi
    fail "$to failed the check, and so does $from again: see check.sh and journalctl -u t7drop."
}

main() {
    # The whole script is read before this runs, and a dropped SSH connection neither stops
    # it half-way nor makes its output kill it.
    trap '' HUP PIPE
    if [ "${1:-}" = --rollback ]; then
        shift
        rollback "$@"
    else
        upgrade "$@"
    fi
}

main "$@"