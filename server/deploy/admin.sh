#!/bin/bash
# t7drop: runs t7drop-admin on the VPS, such as `admin.sh invite laptop` or
# `admin.sh devices`. Run as root through run-on-vps.sh. It changes what the admin command
# changes: pairing codes and devices (see `t7drop-admin --help`).
exec /usr/local/sbin/t7drop-admin "$@"
