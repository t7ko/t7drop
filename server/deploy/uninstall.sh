#!/bin/bash
# t7drop: removes all that t7drop added to the VPS (docs/deployment.md), in reverse order:
# its two lines in /etc/nginx/snippets/includes.conf and /etc/nginx/t7drop (checked with
# nginx -t, then a reload), the service, its code and data, the user. Paired devices are
# lost with the data. /root/t7drop-backup stays. Nothing else is touched.
#
# Run as root through run-on-vps.sh.

set -u
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

SNIPPET=/etc/nginx/snippets/includes.conf
MARKER="# t7drop: the next line is t7drop's; its uninstall script removes both lines."
INCLUDE="include /etc/nginx/t7drop/*.conf;"

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*"; exit 1; }

[ "$(id -u)" = 0 ] || fail "run as root"

say "nginx"
if grep -qxF -e "$MARKER" -e "$INCLUDE" "$SNIPPET" 2>/dev/null; then
    nginx -t 2>/dev/null || fail "nginx -t fails already, before any change; nothing done"
    mkdir -p /root/t7drop-backup
    backup="/root/t7drop-backup/includes.conf.$(date +%Y%m%d-%H%M%S)"
    cp -a "$SNIPPET" "$backup"
    grep -vxF -e "$MARKER" -e "$INCLUDE" "$backup" > "$SNIPPET.t7drop-tmp"
    cat "$SNIPPET.t7drop-tmp" > "$SNIPPET"
    rm -f "$SNIPPET.t7drop-tmp"
    if nginx -t 2>&1; then
        systemctl reload nginx && echo "removed the two lines; nginx reloaded (backup: $backup)"
    else
        cat "$backup" > "$SNIPPET"
        fail "nginx -t failed without the lines; $SNIPPET restored, nothing else done"
    fi
else
    echo "no t7drop lines in $SNIPPET"
fi
if [ -d /etc/nginx/t7drop ]; then
    rm -rf /etc/nginx/t7drop
    echo "removed /etc/nginx/t7drop"
fi

say "Service"
if [ -f /etc/systemd/system/t7drop.service ]; then
    systemctl disable --now t7drop 2>&1
    rm -f /etc/systemd/system/t7drop.service
    systemctl daemon-reload
    echo "stopped and removed t7drop.service"
fi
rm -f /usr/local/sbin/t7drop-admin
rm -rf /opt/t7drop /opt/t7drop.old /opt/t7drop.new /etc/t7drop /var/lib/t7drop /root/t7drop-server.tar.gz
echo "removed /opt/t7drop, /etc/t7drop, /var/lib/t7drop, /usr/local/sbin/t7drop-admin"
if getent passwd t7drop >/dev/null; then
    userdel t7drop && echo "removed the user t7drop"
fi
if getent group t7drop >/dev/null; then
    groupdel t7drop && echo "removed the group t7drop"
fi

say "Done"
