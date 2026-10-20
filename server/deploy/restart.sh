#!/bin/bash
# t7drop: restarts t7drop.service on the VPS, and nothing else. Its queue and stored
# chunks are lost, as at any restart; clients upload again. Run as root through
# run-on-vps.sh.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
date -u
systemctl restart t7drop && sleep 1 && systemctl status t7drop --no-pager -n 3