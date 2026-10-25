"""server/deploy/deploy.sh and nginx-unhook.sh, run with stand-ins for systemd and nginx.

Linux only (in WSL, as AGENTS.local.md says), as an ordinary user: T7DROP_TEST_ROOT moves the
scripts' /opt, /etc and /root into a scratch folder, and T7DROP_TEST_BIN puts the stand-ins
below at the front of their PATH. The stand-in systemctl runs the unit's ExecStart, its paths
moved into the scratch folder, as a background process with the unit's LimitCORE; so the
checks of deploy.sh meet a real t7drop server, its /proc files and its admin command.
"""

import http.client
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tarfile
import time

import pytest

import helpers


def _can_run():
    if not sys.platform.startswith("linux") or not shutil.which("curl") or not shutil.which("bash"):
        return False
    return subprocess.run(["/usr/bin/python3", "-c", "import cryptography"], capture_output=True).returncode == 0


pytestmark = pytest.mark.skipif(not _can_run(), reason="the deploy scripts run on Linux (WSL), with curl and "
                                                        "/usr/bin/python3 with cryptography")

DEPLOY = os.path.join(helpers.ROOT, "server", "deploy")
MARKER = "# t7drop: the next line is t7drop's; its uninstall script removes both lines."
INCLUDE = "include /etc/nginx/t7drop/*.conf;"

STANDINS = {
    "systemctl": r'''#!/bin/bash
# stand-in for systemctl: t7drop.service as a background process; a reload of nginx does nothing
R="$T7DROP_TEST_ROOT"
pidfile="$R/run/t7drop.pid"
echo "systemctl $*" >> "$R/run/systemctl.log"
alive() {
    local pid
    pid=$(cat "$pidfile" 2>/dev/null) || return 1
    kill -0 "$pid" 2>/dev/null && [ "$(awk '/^State:/ { print $2 }' "/proc/$pid/status" 2>/dev/null)" != Z ]
}
stop() {
    if alive; then
        kill "$(cat "$pidfile")"
        for _ in $(seq 100); do alive || break; sleep 0.05; done
    fi
    rm -f "$pidfile"
}
args=()
for a in "$@"; do [ "$a" = --quiet ] || args+=("$a"); done
set -- "${args[@]}"
case "$1" in
    daemon-reload | enable | reset-failed) ;;
    reload) [ "$2" = nginx ] ;;
    restart)
        stop
        unit="$R/etc/systemd/system/t7drop.service"
        command=$(sed -n 's/^ExecStart=//p' "$unit" | sed -e "s# /opt/# $R/opt/#g" -e "s# /etc/# $R/etc/#g")
        core=$(sed -n 's/^LimitCORE=//p' "$unit")
        prlimit --core="${core:-0}:" $command >> "$R/run/journal.log" 2>&1 < /dev/null &  # bytes, as systemd
        echo $! > "$pidfile"
        ;;
    stop) stop ;;
    is-active) alive ;;
    show)
        case "$3" in
            MainPID) if alive; then cat "$pidfile"; else echo 0; fi ;;
            *) echo "[not set]" ;;
        esac
        ;;
    *) echo "stand-in systemctl: $*" >&2; exit 1 ;;
esac
''',
    "journalctl": r'''#!/bin/bash
tail -n 30 "$T7DROP_TEST_ROOT/run/journal.log" 2>/dev/null
true
''',
    "runuser": r'''#!/bin/bash
# stand-in for runuser: the command runs as the current user
while [ $# -gt 0 ] && [ "$1" != -- ]; do shift; done
shift
exec "$@"
''',
    "id": r'''#!/bin/bash
if [ "$1" = -u ]; then echo 0; else exec /usr/bin/id "$@"; fi
''',
    "chown": "#!/bin/bash\ntrue\n",
    "install": r'''#!/bin/bash
# stand-in for install: /usr/bin/install without -o and -g
args=()
while [ $# -gt 0 ]; do
    case "$1" in
        -o | -g) shift 2 ;;
        *) args+=("$1"); shift ;;
    esac
done
exec /usr/bin/install "${args[@]}"
''',
    "t7drop-admin": r'''#!/bin/bash
exec /usr/bin/python3 -E -s "$T7DROP_TEST_ROOT/opt/t7drop/server/admin.py" \
    --config "$T7DROP_TEST_ROOT/etc/t7drop/server.conf" "$@"
''',
    "nginx": r'''#!/bin/bash
# stand-in for nginx: -t exits with the next status listed in run/nginx-t, or 0
f="$T7DROP_TEST_ROOT/run/nginx-t"
echo "nginx $*" >> "$T7DROP_TEST_ROOT/run/nginx.log"
if [ "$1" = -t ]; then
    status=0
    if [ -s "$f" ]; then
        status=$(head -n 1 "$f")
        sed -i 1d "$f"
    fi
    echo "nginx: the configuration test says $status" >&2
    exit "$status"
fi
''',
}

# Broken versions: (file in the tarball, text, replacement)
SYNTAX_ERROR = ("server/t7drop_server/hub.py", b'"""The queue', b'def (:\n"""The queue')
IMPORT_ERROR = ("server/t7drop_server/app.py", b"import json\n", b"import json\nimport t7drop_no_such_module\n")
DIES = ("server/main.py", b"    server.serve_forever()\n",
        b"    threading.Timer(2, os._exit, [3]).start()\n    server.serve_forever()\n")
ADMIN_BROKEN = ("server/admin.py", b'print(format_code(registry.create_invite("check", ttl=120)))',
                b'raise SystemExit("broken")')
DUMPABLE = ("server/main.py", b"    make_not_dumpable()\n", b"")
NO_CORE_LIMIT = ("server/deploy/t7drop.service", b"LimitCORE=1\n", b"")


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def make_tarball(path, changes=()):
    """The server/ folder as run-on-vps.sh uploads it, with some texts replaced."""
    with tarfile.open(path, "w:gz") as tar:
        for folder, dirs, files in os.walk(os.path.join(helpers.ROOT, "server")):
            dirs[:] = sorted(d for d in dirs if d != "__pycache__")
            for name in sorted(files):
                if name.endswith(".pyc"):
                    continue
                full = os.path.join(folder, name)
                arcname = os.path.relpath(full, helpers.ROOT).replace(os.sep, "/")
                with open(full, "rb") as f:
                    data = f.read()
                for target, old, new in changes:
                    if target == arcname:
                        assert data.count(old) == 1, (target, old)
                        data = data.replace(old, new)
                info = tarfile.TarInfo(arcname)
                info.size = len(data)
                info.mode = 0o644
                tar.addfile(info, io.BytesIO(data))


class Vps:
    def __init__(self, tmp_path):
        self.tmp = tmp_path
        self.root = tmp_path / "root"
        self.bin = tmp_path / "bin"
        self.code = self.root / "opt" / "t7drop"
        self.port = free_port()
        self.bin.mkdir()
        for name, text in STANDINS.items():
            path = self.bin / name
            path.write_text(text, encoding="utf-8")
            path.chmod(0o755)
        for folder in ("opt", "etc/t7drop", "etc/systemd/system", "usr/local/sbin", "var/lib", "root", "run"):
            (self.root / folder).mkdir(parents=True, exist_ok=True)
        (self.root / "etc/t7drop/server.conf").write_text(
            "[server]\nlisten = 127.0.0.1:%d\ndata_dir = %s\n" % (self.port, self.root / "var/lib/t7drop"),
            encoding="utf-8")

    def env(self):
        return dict(os.environ, T7DROP_TEST_ROOT=str(self.root), T7DROP_TEST_BIN=str(self.bin),
                    PYTHONDONTWRITEBYTECODE="1")

    def run(self, script, *args):
        result = subprocess.run(["bash", os.path.join(DEPLOY, script)] + list(args), env=self.env(),
                                stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=300)
        return result.returncode, result.stdout + result.stderr

    def deploy(self, *args):
        return self.run("deploy.sh", *args)

    def tarball(self, *changes):
        """Uploads a version, as run-on-vps.sh --upload-code does; returns its path."""
        path = self.root / "root" / "t7drop-server.tar.gz"
        make_tarball(path, changes)
        return str(path)

    def unpack(self, folder, commit, *changes):
        """A version as deploy.sh leaves it in /opt."""
        tarball = self.tmp / "unpack.tar.gz"
        make_tarball(tarball, changes)
        folder.mkdir(parents=True)
        with tarfile.open(tarball) as tar:
            tar.extractall(folder, filter="data")
        (folder / "COMMIT").write_text(commit + "\n", encoding="utf-8")

    def install(self, commit="aaaa", old=None, old_changes=()):
        """t7drop as install.sh leaves it, running; with `old`, also a previous version."""
        self.unpack(self.code, commit)
        if old:
            self.unpack(self.root / "opt" / "t7drop.old", old, *old_changes)
        shutil.copy(os.path.join(DEPLOY, "t7drop.service"), self.root / "etc/systemd/system/t7drop.service")
        self.systemctl("restart", "t7drop")
        for _ in range(100):
            try:
                socket.create_connection(("127.0.0.1", self.port), timeout=1).close()
                return
            except OSError:
                time.sleep(0.1)
        raise AssertionError("the server did not start: " + self.journal())

    def systemctl(self, *args):
        return subprocess.run([str(self.bin / "systemctl")] + list(args), env=self.env(), capture_output=True,
                              text=True, stdin=subprocess.DEVNULL, timeout=60)

    def pid(self):
        return self.systemctl("show", "-p", "MainPID", "--value", "t7drop").stdout.strip()

    def journal(self):
        path = self.root / "run" / "journal.log"
        return path.read_text(encoding="utf-8") if path.exists() else ""

    def commit_of(self, suffix=""):
        path = self.root / "opt" / ("t7drop" + suffix) / "COMMIT"
        return path.read_text(encoding="utf-8").strip() if path.exists() else None

    def admin(self, *args):
        return subprocess.run([str(self.bin / "t7drop-admin")] + list(args), env=self.env(), capture_output=True,
                              text=True, timeout=60)

    def request(self, method, path, token=None, body=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        headers = {"Authorization": "Bearer " + token} if token else {}
        data = json.dumps(body).encode("utf-8") if body is not None else None
        try:
            conn.request(method, "/api/v1" + path, body=data, headers=headers)
            response = conn.getresponse()
            return response.status, response.read()
        finally:
            conn.close()

    def pair(self):
        out = self.admin("invite", "laptop").stdout
        code = re.search(r"([2-9A-Z]{5}-[2-9A-Z]{5})", out).group(1)
        status, body = self.request("POST", "/pair", body={"code": code, "name": "laptop"})
        assert status == 200, body
        return json.loads(body)["token"]

    def serves(self, token):
        """Whether the running server answers a paired device."""
        return self.request("GET", "/devices", token)[0] == 200


@pytest.fixture
def vps(tmp_path):
    vps = Vps(tmp_path)
    yield vps
    vps.systemctl("stop", "t7drop")


def test_an_upgrade_that_passes_the_check_stays(vps):
    vps.install("aaaa")
    token = vps.pair()
    pid = vps.pid()

    code, out = vps.deploy(vps.tarball(), "bbbb")
    assert code == 0, out
    assert "t7drop bbbb runs and passes the check" in out
    assert "not dumpable, core limit 1" in out and "still the same process after 5 seconds" in out
    assert "the unit stays the same" in out
    assert (vps.commit_of(), vps.commit_of(".old")) == ("bbbb", "aaaa")
    assert vps.pid() != pid and vps.serves(token), "a new process, and the devices are kept"
    assert not (vps.root / "root" / "t7drop-server.tar.gz").exists(), "the upload is deleted"
    for leftover in (".new", ".older", ".swap", ".failed"):
        assert vps.commit_of(leftover) is None

    code, out = vps.deploy(vps.tarball(), "cccc")
    assert code == 0, out
    assert (vps.commit_of(), vps.commit_of(".old"), vps.commit_of(".older")) == ("cccc", "bbbb", None)


@pytest.mark.parametrize("change, says", [(SYNTAX_ERROR, "does not compile"), (IMPORT_ERROR, "does not import")])
def test_code_that_does_not_compile_or_import_changes_nothing(vps, change, says):
    vps.install("aaaa", old="zzzz")
    pid = vps.pid()
    code, out = vps.deploy(vps.tarball(change), "bbbb")
    assert code == 1, out
    assert says in out and "nothing changed" in out
    assert (vps.commit_of(), vps.commit_of(".old"), vps.commit_of(".new")) == ("aaaa", "zzzz", None)
    assert vps.pid() == pid


@pytest.mark.parametrize("change, says", [
    (DIES, "stopped or restarted within 5 seconds"),
    (ADMIN_BROKEN, "t7drop-admin check-code failed"),
    (DUMPABLE, "the process is dumpable"),
    (NO_CORE_LIMIT, "core limit is not 1"),
])
def test_a_version_that_fails_the_check_is_rolled_back(vps, change, says):
    vps.install("aaaa", old="zzzz")
    token = vps.pair()
    code, out = vps.deploy(vps.tarball(change), "bbbb")
    assert code == 1, out
    assert says in out
    assert ("the unit changes:" in out) == (change is NO_CORE_LIMIT)
    assert "ROLLED BACK: aaaa runs again and passes" in out
    assert (vps.commit_of(), vps.commit_of(".old"), vps.commit_of(".failed")) == ("aaaa", "zzzz", "bbbb")
    assert vps.commit_of(".older") is None
    unit = (vps.root / "etc/systemd/system/t7drop.service").read_text(encoding="utf-8")
    assert "LimitCORE=1" in unit, "the previous unit is back"
    assert vps.serves(token)


def test_rollback_swaps_with_the_previous_version_and_back(vps):
    vps.install("aaaa", old="zzzz")
    token = vps.pair()
    code, out = vps.deploy("--rollback")
    assert code == 0, out
    assert "t7drop zzzz runs and passes the check" in out
    assert (vps.commit_of(), vps.commit_of(".old")) == ("zzzz", "aaaa")
    assert vps.serves(token)

    code, out = vps.deploy("--rollback")
    assert code == 0, out
    assert (vps.commit_of(), vps.commit_of(".old")) == ("aaaa", "zzzz")
    assert vps.serves(token)


def test_a_rollback_to_a_version_that_fails_goes_back(vps):
    vps.install("aaaa", old="zzzz", old_changes=[DIES])
    code, out = vps.deploy("--rollback")
    assert code == 1, out
    assert "zzzz failed the check (above); aaaa runs again and passes" in out
    assert (vps.commit_of(), vps.commit_of(".old"), vps.commit_of(".swap")) == ("aaaa", "zzzz", None)


def test_without_a_previous_version_there_is_no_rollback(vps):
    vps.install("aaaa")
    pid = vps.pid()
    code, out = vps.deploy("--rollback")
    assert code == 1 and "no " in out and "to go back to" in out, out
    assert vps.commit_of() == "aaaa" and vps.pid() == pid


def test_when_nginx_does_not_pass_t7drop_on_nothing_changes(vps):
    vps.install("aaaa")
    pid = vps.pid()
    code, out = vps.deploy(vps.tarball(), "bbbb", "t7drop-test.invalid", "abcdefgh12")
    assert code == 1, out
    assert "does not answer through nginx even now; nothing changed" in out
    assert vps.commit_of() == "aaaa" and vps.commit_of(".old") is None and vps.pid() == pid


def test_bad_arguments_are_refused_before_anything(vps):
    vps.install("aaaa")
    pid = vps.pid()
    for args in [("--rollback", "only-a-hostname"), (vps.tarball(), "bbbb", "--rollback"),
                 (vps.tarball(), "bbbb", "host.example", "short")]:
        code, out = vps.deploy(*args)
        assert code == 1 and ("bad hostname" in out or "give the hostname and the prefix" in out), out
    assert vps.commit_of() == "aaaa" and vps.pid() == pid


def test_deploy_needs_an_installation(vps):
    code, out = vps.deploy(vps.tarball(), "bbbb")
    assert code == 1 and "install.sh does the first installation" in out, out


# nginx-unhook.sh

def nginx_files(vps, hooked=True):
    snippets = vps.root / "etc/nginx/snippets"
    snippets.mkdir(parents=True)
    text = "location /other/ {\n    return 200;\n}\n"
    if hooked:
        text += MARKER + "\n" + INCLUDE + "\n"
        conf = vps.root / "etc/nginx/t7drop"
        conf.mkdir()
        (conf / "t7drop.conf").write_text("location ^~ /abcdefgh12/ {\n}\n", encoding="utf-8")
    (snippets / "includes.conf").write_text(text, encoding="utf-8")
    return snippets / "includes.conf", text


def backups(vps):
    folder = vps.root / "root" / "t7drop-backup"
    return sorted(p.name.rsplit(".", 1)[0] for p in folder.iterdir()) if folder.exists() else []


def test_unhook_takes_out_the_two_lines_and_the_folder(vps):
    snippet, text = nginx_files(vps)
    code, out = vps.run("nginx-unhook.sh")
    assert code == 0, out
    assert snippet.read_text(encoding="utf-8") == "location /other/ {\n    return 200;\n}\n"
    assert not (vps.root / "etc/nginx/t7drop").exists()
    assert backups(vps) == ["includes.conf", "nginx-t7drop"]
    assert "systemctl reload nginx" in (vps.root / "run/systemctl.log").read_text(encoding="utf-8")


def test_unhook_puts_everything_back_when_nginx_t_fails(vps):
    snippet, text = nginx_files(vps)
    (vps.root / "run/nginx-t").write_text("0\n1\n0\n", encoding="utf-8")
    code, out = vps.run("nginx-unhook.sh")
    assert code == 1, out
    assert "nginx -t failed without t7drop's lines" in out and "PUT BACK" in out
    assert snippet.read_text(encoding="utf-8") == text
    assert (vps.root / "etc/nginx/t7drop/t7drop.conf").exists()


def test_unhook_with_nothing_hooked_does_nothing(vps):
    snippet, text = nginx_files(vps, hooked=False)
    code, out = vps.run("nginx-unhook.sh")
    assert code == 0 and "Nothing to do" in out, out
    assert snippet.read_text(encoding="utf-8") == text and backups(vps) == []