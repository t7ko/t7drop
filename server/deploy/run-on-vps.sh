#!/bin/bash
# t7drop: runs one of the scripts in this folder on the VPS over SSH, and saves its output
# in the git-ignored local/ folder of the repository. Each script says in its header what
# it does and whether it changes anything.
#
# Usage, in the user's terminal (PowerShell):
#   & "C:\Program Files\Git\bin\bash.exe" <repository>/server/deploy/run-on-vps.sh [--upload-code] root@<VPS address> <script> [arguments]
# <script> is a file name in this folder, such as check.sh, or in the git-ignored
# local/scripts/ for scripts that name the VPS's other services; arguments must not
# contain spaces. --upload-code first copies the committed server/ folder (git archive of HEAD;
# refused while server/ has uncommitted changes) to /root/t7drop-server.tar.gz and puts
# that path and the commit id before the arguments; install.sh, deploy.sh (except with
# --rollback) and nginx-hook.sh need it.
#
# The SSH key's passphrase is asked once, in a Windows dialog, through with-temp-agent.sh
# from the t7helpers shared scripts (T7_SSH_KEY picks another key). Without those scripts,
# start an ssh-agent with the key yourself first.

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

upload=0
if [ "${1:-}" = "--upload-code" ]; then
    upload=1
    shift
fi
if [ $# -lt 2 ]; then
    echo "usage: run-on-vps.sh [--upload-code] user@host <script in server/deploy> [arguments]" >&2
    exit 2
fi
target="$1"
script="$2"
shift 2
case "$script" in
    */* | "") echo "give the name of a script in $here" >&2; exit 2 ;;
esac
script_path="$here/$script"
if [ ! -f "$script_path" ]; then
    script_path="$repo/local/scripts/$script"
fi
if [ ! -f "$script_path" ]; then
    echo "no such script in $here or $repo/local/scripts: $script" >&2
    exit 2
fi

if [ $upload = 1 ] && [ -n "$(git -C "$repo" status --porcelain -- server)" ]; then
    echo "server/ has uncommitted changes; commit them first:" >&2
    git -C "$repo" status --short -- server >&2
    exit 2
fi

# Everything checked: now the passphrase, once for all connections below.
agent_script="$here/../../../scripts/with-temp-agent.sh"
if [ -z "${SSH_AUTH_SOCK:-}" ] && [ -x "$agent_script" ]; then
    if [ $upload = 1 ]; then
        exec "$agent_script" "$0" --upload-code "$target" "$script" "$@"
    fi
    exec "$agent_script" "$0" "$target" "$script" "$@"
fi

mkdir -p "$repo/local"
out="$repo/local/${script%.sh}-$(date +%Y%m%d-%H%M%S).txt"

if [ $upload = 1 ]; then
    commit="$(git -C "$repo" rev-parse --short HEAD)"
    tarball="$repo/local/t7drop-server-$commit.tar.gz"
    git -C "$repo" archive --format=tar.gz -o "$tarball" HEAD server || exit 1
    echo "Copying the server code of commit $commit to $target ..."
    scp -q -o ConnectTimeout=20 "$tarball" "$target:/root/t7drop-server.tar.gz" || exit 1
    set -- /root/t7drop-server.tar.gz "$commit" "$@"
fi

echo "Running $script on $target ..."
ssh -o ConnectTimeout=20 "$target" bash -s -- "$@" < "$script_path" > "$out" 2>&1
status=$?
echo "ssh exit status: $status"
echo "Output: $out ($(wc -l < "$out") lines)"
