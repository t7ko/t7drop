"""Drive the app on the emulator (or a phone) over adb, for manual tests.

Works before the first unlock after a reboot: it dumps the UI to /data/local/tmp,
because /sdcard does not exist then. Run it with Windows Python; from Git Bash,
call adb yourself only with MSYS_NO_PATHCONV=1, or /sdcard/... paths get mangled.

emulator-ui.py texts               list visible texts / descriptions with their bounds
emulator-ui.py tap TEXT [N]        tap the Nth (default 0) node whose text or content-desc equals TEXT
emulator-ui.py tapsub TEXT [N]     same, but substring match
emulator-ui.py long TEXT [N]       long-press it
emulator-ui.py shot NAME           save a screenshot to build/shots/NAME.png (git-ignored)
"""
import os, re, subprocess, sys
import xml.etree.ElementTree as ET



def find_adb():
    """adb from sdk.dir in local.properties, else ANDROID_HOME, else the PATH."""
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    try:
        with open(os.path.join(root, "local.properties"), encoding="utf-8") as f:
            for line in f:
                if line.startswith("sdk.dir="):
                    sdk = line.split("=", 1)[1].strip().replace(chr(92) + ":", ":")
    except OSError:
        pass
    if not sdk:
        return "adb"
    return os.path.join(sdk, "platform-tools", "adb.exe" if os.name == "nt" else "adb")


ADB = find_adb()
SHOTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "build", "shots")


def adb(*args, binary=False):
    out = subprocess.run([ADB, *args], capture_output=True, check=True).stdout
    return out if binary else out.decode("utf-8", "replace")


def nodes():
    adb("shell", "uiautomator", "dump", "/data/local/tmp/ui.xml")
    xml = adb("exec-out", "cat", "/data/local/tmp/ui.xml")
    root = ET.fromstring(xml[xml.index("<"):])
    for n in root.iter("node"):
        b = [int(v) for v in re.findall(r"\d+", n.get("bounds", ""))]
        yield n.get("text", ""), n.get("content-desc", ""), b, n.get("clickable") == "true"


def find(text, index, sub):
    hits = [(t, d, b) for t, d, b, _ in nodes()
            if (text in (t, d)) or (sub and (text in t or text in d))]
    if len(hits) <= index:
        sys.exit(f"not found: {text!r} (matches: {len(hits)})")
    t, d, b = hits[index]
    return (b[0] + b[2]) // 2, (b[1] + b[3]) // 2


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = sys.argv[1]
    if cmd == "texts":
        for t, d, b, c in nodes():
            if t or d:
                print(("* " if c else "  ") + repr(t or d), b)
    elif cmd in ("tap", "tapsub", "long"):
        x, y = find(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 0, cmd == "tapsub")
        if cmd == "long":
            adb("shell", "input", "swipe", str(x), str(y), str(x), str(y), "900")
        else:
            adb("shell", "input", "tap", str(x), str(y))
        print("tapped", x, y)
    elif cmd == "shot":
        os.makedirs(SHOTS, exist_ok=True)
        path = os.path.join(SHOTS, sys.argv[2] + ".png")
        open(path, "wb").write(adb("exec-out", "screencap", "-p", binary=True))
        print(path)


main()
