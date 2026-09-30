"""Patchright persistent context launcher: Explicit proxy and anti-detection parameters."""
import asyncio
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit

import config

LAUNCH_ARGS = [
    "--disable-blink-features=AutomationControlled",
    "--no-first-run",
    "--no-default-browser-check",
]

def _proxy_from_url(raw: str) -> dict | None:
    """"scheme://user:pass@host:port" (or "host:port") -> Playwright proxy dict; None if it cannot be read."""
    raw = (raw or "").strip()
    if not raw:
        return None
    if "://" not in raw:
        raw = "http://" + raw
    parts = urlsplit(raw)
    if not parts.hostname:
        return None
    port = parts.port or (443 if parts.scheme == "https" else 80)
    proxy = {"server": f"{parts.scheme}://{parts.hostname}:{port}"}
    if parts.username:
        proxy["username"] = unquote(parts.username)
        proxy["password"] = unquote(parts.password or "")
    return proxy


def proxy_for_account(account: str | None) -> dict | None:
    """Proxy of one account: accounts/<account>/proxy.txt (written by DolaCoordinator's "Gán proxy", one line
    "scheme://user:pass@host:port") wins; otherwise the global DOLA_PROXY; otherwise no proxy."""
    if account:
        try:
            f = Path("accounts") / account / "proxy.txt"
            if f.is_file():
                found = _proxy_from_url(f.read_text(encoding="utf-8"))
                if found:
                    return found
        except OSError:
            pass
    return _proxy_from_url(config.PROXY) if config.PROXY else None


# "Hidden" render window. The Dola extension needs a HEADED Chromium, so instead of headless mode the window is
# opened far off-screen (and kept from being throttled as "occluded"). It behaves exactly like a normal window for the
# site, so it does not raise the bot-detection risk that real headless mode would.
HIDDEN_WINDOW_ARGS = [
    "--window-position=-32000,-32000",
    "--window-size=1280,800",
    "--disable-backgrounding-occluded-windows",
    "--disable-renderer-backgrounding",
    "--disable-background-timer-throttling",
    "--disable-features=CalculateNativeWinOcclusion",
]


async def launch_account_context(p, account: str, headless: bool = None, use_extension: bool = False,
                                 hide_window: bool = False):
    """Launches accounts/<account> profile, returns BrowserContext. Caller must close.

    p: async_playwright() instance
    headless: None = uses config.HEADLESS
    hide_window: open the (headed) window off-screen so nothing shows on the desktop while rendering
    """
    profile_dir = Path("accounts") / account
    if not profile_dir.exists():
        raise FileNotFoundError(
            f"Account profile does not exist: {profile_dir} (run python add_account.py {account} first)"
        )
    launch_headless = config.HEADLESS if headless is None else headless
    args = list(LAUNCH_ARGS)
    if use_extension:
        if not config.EXTENSION_ENABLED:
            raise RuntimeError("Dola extension is disabled (DOLA_EXTENSION_ENABLED=0)")
        extension_dir = Path(config.EXTENSION_DIR).resolve()
        if not extension_dir.exists():
            raise FileNotFoundError(f"Dola extension directory does not exist: {extension_dir}")
        # Chromium debugger extension requires headed window to intercept skill/action-bar responses
        launch_headless = False
        args.extend([
            f"--disable-extensions-except={extension_dir}",
            f"--load-extension={extension_dir}",
        ])
    if hide_window and not launch_headless:
        args.extend(HIDDEN_WINDOW_ARGS)
    kwargs = {
        "headless": launch_headless,
        "args": args,
        "locale": "ja-JP",
        "timezone_id": "Asia/Tokyo",
    }
    proxy = proxy_for_account(account)
    if proxy:
        kwargs["proxy"] = proxy
    context = await p.chromium.launch_persistent_context(str(profile_dir), **kwargs)

    # Automatically ensure persistent cookies from cookie.txt
    cookie_file = profile_dir / "cookie.txt"
    if cookie_file.exists():
        try:
            from add_account_cookie import parse_cookie_string
            raw_data = cookie_file.read_text(encoding="utf-8").strip()
            if raw_data:
                cks = parse_cookie_string(raw_data)
                if cks:
                    await context.add_cookies(cks)
        except Exception:
            pass

    return context


USER_LAUNCH_SUFFIX = "/alice/user/launch"


def parse_user_launch(text: str):
    """Dola's own answer to "am I logged in?" (response of /alice/user/launch on every page load).

    data.extra.is_login is "1"/"0" and data.sec_user_id is empty for guests. Returns True/False,
    or None when the body cannot be interpreted. More reliable than looking for a login button,
    which appears late on slow loads and made logged-out sessions look logged in.
    """
    try:
        data = (json.loads(text) or {}).get("data") or {}
    except (ValueError, AttributeError):
        return None
    flag = str((data.get("extra") or {}).get("is_login", "")).strip().lower()
    if flag in ("1", "true"):
        return True
    if flag in ("0", "false"):
        return False
    return True if data.get("sec_user_id") else None


def cookie_value(cookies: list, name: str) -> str:
    """Extracts cookie value from context.cookies() result."""
    return next((c["value"] for c in cookies if c["name"] == name and c["value"]), "")


async def check_login_state(account: str) -> bool:
    """Opens Dola in headless mode and checks whether session is active."""
    from patchright.async_api import async_playwright
    async with async_playwright() as p:
        context = await launch_account_context(p, account)
        try:
            page = context.pages[0] if context.pages else await context.new_page()

            server_state = {"login": None}

            async def on_response(resp):
                if resp.url.split("?")[0].endswith(USER_LAUNCH_SUFFIX):
                    try:
                        server_state["login"] = parse_user_launch(await resp.text())
                    except Exception:
                        pass

            page.on("response", lambda r: asyncio.ensure_future(on_response(r)))
            await page.goto("https://www.dola.com/chat", timeout=60000, wait_until="domcontentloaded")

            # Wait for Dola to say whether we are logged in (up to ~20s), instead of a fixed sleep
            for _ in range(40):
                if server_state["login"] is not None:
                    break
                await page.wait_for_timeout(500)

            cookies = await context.cookies("https://www.dola.com")
            if not cookie_value(cookies, "sessionid"):
                return False
            if server_state["login"] is not None:
                return bool(server_state["login"])

            # Fallback (endpoint unreachable/changed): original UI heuristic
            await page.wait_for_timeout(5000)
            for login_btn_sel in ('button:has-text("ログイン")', 'button:has-text("Log in")', 'button:has-text("登录")', 'a:has-text("ログイン")', 'a:has-text("Log in")'):
                btn = page.locator(login_btn_sel).first
                if await btn.count() and await btn.is_visible():
                    return False
            return True
        finally:
            await context.close()
