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


def _install_browser() -> int:
    from patchright._impl._driver import compute_driver_executable, get_driver_env  # bundled node driver

    import subprocess

    driver_executable, driver_cli = compute_driver_executable()
    return subprocess.call([driver_executable, driver_cli, "install", "chromium"], env=get_driver_env())


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
