"""Single entry point for the packaged gateway (dola-gateway.exe) - also works as `python gateway_main.py ...`.

  dola-gateway.exe serve [--host 127.0.0.1] [--port 8000]   run the API server (what DolaCoordinator starts in the background)
  dola-gateway.exe open-profile <account> [--login ...]     open / log in an account profile (see open_profile.py)
  dola-gateway.exe install-browser                          download the Chromium build used by patchright (once per machine)

The working directory is the data directory: accounts/, downloads/, *.db, web/ and extensions/ live there.
"""
import argparse
import os
import runpy
import sys


def _serve(argv: list[str]) -> None:
    ap = argparse.ArgumentParser(prog="dola-gateway serve")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8000)
    args = ap.parse_args(argv)

    import uvicorn
    import server  # noqa: F401  (imported here so `install-browser` / `open-profile` stay light)

    uvicorn.run(server.app, host=args.host, port=args.port, log_level="info")


# Chromium build that patchright 1.63 expects (revision 1243). A mirror of the folders in %LOCALAPPDATA%\ms-playwright
# is published as a GitHub release asset: it is used when Playwright's own CDN is unreachable (proxy, firewall, ISP).
FALLBACK_BROWSER_URL = os.environ.get(
    "DOLA_BROWSER_URL",
    "https://github.com/xtraiter/AIOMMO_DolaAI/releases/download/browser-1243/dola-browser-1243.zip",
)
EXPECTED_BROWSER_DIR = "chromium-1243"


def _browsers_dir() -> str:
    return os.environ["PLAYWRIGHT_BROWSERS_PATH"]


def _chromium_ready() -> bool:
    return os.path.isfile(os.path.join(_browsers_dir(), EXPECTED_BROWSER_DIR, "INSTALLATION_COMPLETE"))


def _install_from_release() -> bool:
    """Download the browser bundle from GitHub and unpack it into the Playwright cache."""
    import shutil
    import tempfile
    import urllib.request
    import zipfile

    target = _browsers_dir()
    os.makedirs(target, exist_ok=True)
    print(f"Downloading browser bundle: {FALLBACK_BROWSER_URL}", flush=True)
    fd, tmp = tempfile.mkstemp(suffix=".zip", dir=target)
    os.close(fd)
    try:
        req = urllib.request.Request(FALLBACK_BROWSER_URL, headers={"User-Agent": "dola-gateway"})
        with urllib.request.urlopen(req, timeout=60) as resp, open(tmp, "wb") as out:
            total = int(resp.headers.get("Content-Length") or 0)
            done = 0
            last = -1
            while chunk := resp.read(1 << 20):
                out.write(chunk)
                done += len(chunk)
                pct = int(done * 100 / total) if total else done >> 20
                if pct != last and (not total or pct % 5 == 0):
                    last = pct
                    print(f"  {pct}%" if total else f"  {pct} MB", flush=True)

        root = os.path.realpath(target) + os.sep
        with zipfile.ZipFile(tmp) as zf:
            for member in zf.namelist():  # refuse entries that would land outside the cache (zip-slip)
                if not os.path.realpath(os.path.join(target, member)).startswith(root):
                    raise RuntimeError(f"unsafe path in browser bundle: {member}")
            zf.extractall(target)
        return _chromium_ready()
    except Exception as ex:  # noqa: BLE001 - report and let the caller decide
        print(f"Browser bundle download failed: {ex}", file=sys.stderr, flush=True)
        return False
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass


def _install_browser() -> int:
    from patchright._impl._driver import compute_driver_executable, get_driver_env  # bundled node driver

    import subprocess

    if _chromium_ready():
        return 0

    driver_executable, driver_cli = compute_driver_executable()
    for attempt in (1, 2):
        rc = subprocess.call([driver_executable, driver_cli, "install", "chromium"], env=get_driver_env())
        if rc == 0:
            return 0
        print(f"Playwright download failed (attempt {attempt}, code {rc}).", file=sys.stderr, flush=True)

    print("Trying the GitHub browser bundle instead...", flush=True)
    if _install_from_release():
        print("Browser installed from the GitHub bundle.", flush=True)
        return 0
    print("Could not download the browser. Check the internet connection / proxy, or set DOLA_BROWSER_URL "
          "to a reachable copy of dola-browser-1243.zip.", file=sys.stderr, flush=True)
    return 1


def _use_shared_browser_cache() -> None:
    # patchright defaults to a browser folder INSIDE the frozen app (PLAYWRIGHT_BROWSERS_PATH=0). Use the normal
    # per-user cache instead so Chromium is downloaded once per machine and shared with a `pip`-based install.
    if "PLAYWRIGHT_BROWSERS_PATH" not in os.environ:
        base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
        os.environ["PLAYWRIGHT_BROWSERS_PATH"] = os.path.join(base, "ms-playwright")


def main() -> int:
    _use_shared_browser_cache()
    if sys.platform == "win32":
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    cmd = sys.argv[1] if len(sys.argv) > 1 else "serve"
    rest = sys.argv[2:]

    if cmd == "serve":
        _serve(rest)
        return 0
    if cmd == "open-profile":
        sys.argv = ["open_profile.py", *rest]
        runpy.run_module("open_profile", run_name="__main__")
        return 0
    if cmd == "install-browser":
        return _install_browser()

    print(f"Unknown command '{cmd}'. Use: serve | open-profile | install-browser", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
