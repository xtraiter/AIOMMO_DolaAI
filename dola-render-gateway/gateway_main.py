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

    # Desktop defaults (dola-pool is written for a server; the environment always wins over these).
    os.environ.setdefault("DOLA_PORT", str(args.port))           # PUBLIC_BASE (video_url) follows the real port
    os.environ.setdefault("DOLA_PURE_API", "0")                  # the HTTP-signing path needs Node.js; the browser path does not
    os.environ.setdefault("DOLA_VIDEO_TIMEOUT", "1800")          # 30 s videos render for ~15 min (the wait_for cap is TASK_DEADLINE + this)
    os.environ.setdefault("DOLA_TASK_DEADLINE", "1800")
    os.environ.setdefault("DOLA_CORS_ORIGINS", "http://127.0.0.1")  # only local pages may call the API from a browser

    import uvicorn
    import server  # noqa: F401  (imported here so `install-browser` / `open-profile` stay light)

    uvicorn.run(server.app, host=args.host, port=args.port, log_level="info")


# Chromium build that patchright 1.63 expects (revision 1243). A mirror of the folders in %LOCALAPPDATA%\ms-playwright
# is published as a GitHub release asset: it is used when Playwright's own CDN is unreachable (proxy, firewall, ISP).
FALLBACK_BROWSER_URL = os.environ.get(
    "DOLA_BROWSER_URL",
    "https://github.com/xtraiter/AIOMMO_DolaAI/releases/download/browser-1243/dola-browser-1243.zip",
)


def _browsers_dir() -> str:
    return os.environ["PLAYWRIGHT_BROWSERS_PATH"]


def _chromium_ready() -> bool:
    """Both the full Chromium (headed profiles) and the headless shell (session checks) must be installed."""
    import glob

    def complete(pattern: str) -> bool:
        return any(os.path.isfile(os.path.join(d, "INSTALLATION_COMPLETE"))
                   for d in glob.glob(os.path.join(_browsers_dir(), pattern)))

    return complete("chromium-*") and complete("chromium_headless_shell-*")


def _download(url: str, dest: str, label: str) -> None:
    """Download with urllib: it follows Windows' system proxy settings, like a web browser does
    (Playwright's own Node downloader only honours the HTTPS_PROXY environment variable)."""
    import urllib.request

    print(f"Downloading {label}: {url}", flush=True)
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 dola-gateway"})
    with urllib.request.urlopen(req, timeout=60) as resp, open(dest, "wb") as out:
        total = int(resp.headers.get("Content-Length") or 0)
        done = 0
        last = -1
        while chunk := resp.read(1 << 20):
            out.write(chunk)
            done += len(chunk)
            pct = int(done * 100 / total) if total else done >> 20
            if pct != last and (not total or pct % 5 == 0):
                last = pct
                print(f"  {label}: {pct}%" if total else f"  {label}: {pct} MB", flush=True)


def _safe_extract(zip_path: str, target: str) -> None:
    import zipfile

    root = os.path.realpath(target) + os.sep
    with zipfile.ZipFile(zip_path) as zf:
        for member in zf.namelist():  # refuse entries that would land outside the target (zip-slip)
            if not os.path.realpath(os.path.join(target, member)).startswith(root):
                raise RuntimeError(f"unsafe path in archive: {member}")
        zf.extractall(target)


def _driver_browser_plan() -> list[tuple[str, str, list[str], bool]]:
    """(label, cache folder name, candidate URLs, required) for every component `patchright install chromium` fetches on Windows."""
    import json

    from patchright._impl._driver import compute_driver_executable

    _, driver_cli = compute_driver_executable()
    with open(os.path.join(os.path.dirname(driver_cli), "browsers.json"), encoding="utf-8") as f:
        info = {b["name"]: b for b in json.load(f)["browsers"]}

    chromium = info["chromium"]
    shell = info["chromium-headless-shell"]
    version = chromium["browserVersion"]
    cft = "chrome-for-testing-public/{v}/win64/{f}"
    hosts = ("https://storage.googleapis.com/", "https://cdn.playwright.dev/")
    playwright_hosts = ("https://cdn.playwright.dev/", "https://playwright.download.prss.microsoft.com/dbazure/download/playwright/")
    plan = [
        ("chromium", f"chromium-{chromium['revision']}",
         [h + cft.format(v=version, f="chrome-win64.zip") for h in hosts], True),
        ("headless-shell", f"chromium_headless_shell-{shell['revision']}",
         [h + cft.format(v=shell["browserVersion"], f="chrome-headless-shell-win64.zip") for h in hosts], True),
        ("ffmpeg", f"ffmpeg-{info['ffmpeg']['revision']}",
         [h + f"builds/ffmpeg/{info['ffmpeg']['revision']}/ffmpeg-win64.zip" for h in playwright_hosts], False),
    ]
    if "winldd" in info:
        plan.append(("winldd", f"winldd-{info['winldd']['revision']}",
                     [h + f"builds/winldd/{info['winldd']['revision']}/winldd-win64.zip" for h in playwright_hosts], False))
    return plan


def _install_direct() -> bool:
    """Fetch the same files Playwright would, but through urllib (system proxy aware), and lay them out identically."""
    import shutil
    import tempfile

    target = _browsers_dir()
    os.makedirs(target, exist_ok=True)
    try:
        plan = _driver_browser_plan()
    except Exception as ex:  # noqa: BLE001
        print(f"Cannot read the browser list: {ex}", file=sys.stderr, flush=True)
        return False

    for label, folder, urls, required in plan:
        final = os.path.join(target, folder)
        if os.path.isfile(os.path.join(final, "INSTALLATION_COMPLETE")):
            continue
        fd, tmp_zip = tempfile.mkstemp(suffix=".zip", dir=target)
        os.close(fd)
        staging = tempfile.mkdtemp(prefix="dola-", dir=target)
        try:
            error = None
            for url in urls:
                try:
                    _download(url, tmp_zip, label)
                    error = None
                    break
                except Exception as ex:  # noqa: BLE001 - try the next mirror
                    error = ex
                    print(f"  {label}: {url} failed: {ex}", file=sys.stderr, flush=True)
            if error is not None:
                if required:
                    return False
                print(f"  {label} is optional (only needed for video recording) - skipped.", flush=True)
                continue
            print(f"Extracting {label}...", flush=True)
            _safe_extract(tmp_zip, staging)
            if os.path.isdir(final):
                shutil.rmtree(final, ignore_errors=True)
            os.replace(staging, final)
            open(os.path.join(final, "INSTALLATION_COMPLETE"), "w").close()
        except Exception as ex:  # noqa: BLE001
            print(f"Installing {label} failed: {ex}", file=sys.stderr, flush=True)
            return False
        finally:
            shutil.rmtree(staging, ignore_errors=True)
            try:
                os.remove(tmp_zip)
            except OSError:
                pass
    return _chromium_ready()


def _install_from_release() -> bool:
    """Last resort: a bundle of the four folders published as a GitHub release asset."""
    import tempfile

    target = _browsers_dir()
    os.makedirs(target, exist_ok=True)
    fd, tmp = tempfile.mkstemp(suffix=".zip", dir=target)
    os.close(fd)
    try:
        _download(FALLBACK_BROWSER_URL, tmp, "browser bundle")
        _safe_extract(tmp, target)
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
    import subprocess
    import urllib.request

    from patchright._impl._driver import compute_driver_executable, get_driver_env  # bundled node driver

    if _chromium_ready():
        return 0

    # 1) Playwright's own installer, told about the Windows system proxy (its Node downloader ignores it otherwise)
    env = get_driver_env()
    for scheme, var in (("https", "HTTPS_PROXY"), ("http", "HTTP_PROXY")):
        proxy = urllib.request.getproxies().get(scheme)
        if proxy and not env.get(var) and not env.get(var.lower()):
            env[var] = proxy
            print(f"Using system proxy for {scheme}: {proxy}", flush=True)
    driver_executable, driver_cli = compute_driver_executable()
    rc = subprocess.call([driver_executable, driver_cli, "install", "chromium"], env=env)
    if rc == 0:
        return 0
    print(f"Playwright downloader failed (code {rc}).", file=sys.stderr, flush=True)

    # 2) Same files, downloaded by Python (works wherever a web browser can download them)
    print("Trying a direct download instead...", flush=True)
    if _install_direct():
        print("Browser installed (direct download).", flush=True)
        return 0

    # 3) GitHub release bundle
    print("Trying the GitHub browser bundle...", flush=True)
    if _install_from_release():
        print("Browser installed from the GitHub bundle.", flush=True)
        return 0

    print("Could not download the browser. Check the internet connection / proxy / VPN / antivirus, "
          "or set DOLA_BROWSER_URL to a reachable copy of dola-browser-1243.zip.", file=sys.stderr, flush=True)
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
