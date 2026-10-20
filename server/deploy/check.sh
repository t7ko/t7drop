#!/bin/bash
# t7drop: shows the state of the deployment on the VPS. Changes nothing, except that it
# uses up one check code for the URL test.
#
# Run as root through run-on-vps.sh: `check.sh [<web hostname> <prefix>]`. With the
# hostname and the prefix it also asks t7drop through nginx, as a client would.

export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
HOST="${1:-}"
PREFIX="${2:-}"

say() { printf '\n== %s\n' "$*"; }

say "Service"
systemctl status t7drop --no-pager -n 0
echo "code: $(cat /opt/t7drop/COMMIT 2>/dev/null || echo 'not installed')"
for kind in Current Peak; do
    memory=$(systemctl show -p "Memory$kind" --value t7drop)
    case "$memory" in
        *[!0-9]* | "") echo "memory ($kind): $memory" ;;
        *) echo "memory ($kind): $((memory / 1024 / 1024)) MB (limit 200 MB)" ;;
    esac
done
pid=$(systemctl show -p MainPID --value t7drop)
if [ "${pid:-0}" != 0 ]; then
    grep -E '^Vm(HWM|RSS):' "/proc/$pid/status" | sed 's/^/process /'
    echo "(VmHWM: the process's peak resident memory since it started)"
fi
grep -E '^(anon|file) ' /sys/fs/cgroup/system.slice/t7drop.service/memory.stat 2>/dev/null \
    | awk '{printf "cgroup %s: %d MB\n", $1, $2 / 1048576}'
journalctl -u t7drop -n 10 --no-pager -o short-iso

say "Devices and queue"
t7drop-admin devices
t7drop-admin status

say "nginx"
if grep -qxF 'include /etc/nginx/t7drop/*.conf;' /etc/nginx/snippets/includes.conf 2>/dev/null; then
    echo "the include line is in /etc/nginx/snippets/includes.conf"
else
    echo "the include line is NOT in /etc/nginx/snippets/includes.conf"
fi
ls -la /etc/nginx/t7drop 2>&1
nginx -t 2>&1

if [ -n "$HOST" ] && [ -n "$PREFIX" ]; then
    say "Through nginx: https://$HOST/$PREFIX/"
    code=$(t7drop-admin check-code)
    curl -s -D - -o /dev/null --resolve "$HOST:443:127.0.0.1" -H "Authorization: Check $code" \
        "https://$HOST/$PREFIX/api/v1/ping" | grep -i -e '^HTTP' -e '^x-t7drop-instance'
    echo "without a token: HTTP $(curl -s -o /dev/null -w '%{http_code}' --resolve "$HOST:443:127.0.0.1" "https://$HOST/$PREFIX/api/v1/sync") (expected: 404)"
fi
