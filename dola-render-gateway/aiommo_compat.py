"""Phần bổ sung của AIOMMO DolaAI, tách khỏi các file gốc của gateway (browser.py... giữ nguyên bản gốc).

Chỉ các file đăng nhập thêm vào (open_profile.py, fb_to_dola.py, add_account_cookie.py) dùng module này:
proxy riêng theo tài khoản (accounts/<tên>/proxy.txt), mở profile hiển thị, đọc trạng thái đăng nhập của Dola.
Việc tạo video do gateway gốc làm, không dùng module này.
"""
import asyncio
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit

import config
from browser import LAUNCH_ARGS, cookie_value  # noqa: F401  (re-export cho các file đăng nhập)

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
                text = f.read_text(encoding="utf-8")
                found = _proxy_from_url(text)
                if found:
                    return found
                if text.strip():
                    # Có proxy được gán nhưng không đọc được: dừng, KHÔNG âm thầm đi thẳng bằng IP máy bạn
                    raise RuntimeError(f"proxy.txt của tài khoản '{account}' sai định dạng — sửa hoặc gỡ proxy rồi thử lại.")
        except OSError as ex:
            raise RuntimeError(f"Không đọc được proxy.txt của tài khoản '{account}': {ex}") from ex
    return _proxy_from_url(config.PROXY) if config.PROXY else None


def http_proxy_url_for_account(account: str | None) -> str | None:
    """Same proxy as proxy_for_account, as one URL for aiohttp (which only speaks http/https proxies).
    None = download directly (no proxy, or a socks5 proxy that aiohttp cannot use)."""
    px = proxy_for_account(account)
    if not px:
        return None
    server = px["server"]
    if not server.startswith(("http://", "https://")):
        return None
    if px.get("username"):
        from urllib.parse import quote, urlsplit
        parts = urlsplit(server)
        return f"{parts.scheme}://{quote(px['username'], safe='')}:{quote(px.get('password', ''), safe='')}@{parts.netloc}"
    return server


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
