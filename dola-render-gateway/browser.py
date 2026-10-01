"""Patchright persistent context launcher: Explicit proxy and anti-detection parameters."""
import asyncio
from pathlib import Path

import config

LAUNCH_ARGS = [
    "--disable-blink-features=AutomationControlled",
    "--no-first-run",
    "--no-default-browser-check",
]


async def launch_account_context(p, account: str, headless: bool = None, use_extension: bool = False):
    """Launches accounts/<account> profile, returns BrowserContext. Caller must close.

    p: async_playwright() instance
    headless: None = uses config.HEADLESS
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
    kwargs = {
        "headless": launch_headless,
        "args": args,
        "locale": "ja-JP",
        "timezone_id": "Asia/Tokyo",
    }
    # AIOMMO: proxy riêng của tài khoản (accounts/<tên>/proxy.txt); không có thì mới dùng DOLA_PROXY chung
    from aiommo_compat import proxy_for_account
    proxy = proxy_for_account(account)
    if proxy:
        kwargs["proxy"] = proxy
    context = await p.chromium.launch_persistent_context(str(profile_dir), **kwargs)

    # AIOMMO: phiên Dola của tài khoản nằm trong accounts/<tên>/cookie.txt (app ghi khi đăng nhập / nhập cookie). Cookie nạp
    # bằng script hay đăng nhập qua cookie thường không được Chromium giữ lại trong profile, nên gateway gốc (chỉ tin cookie
    # trong profile) thấy "chưa đăng nhập". Nạp lại cookie.txt mỗi lần mở trình duyệt để mọi tài khoản đều dùng được.
    cookie_file = profile_dir / "cookie.txt"
    if cookie_file.exists():
        try:
            from add_account_cookie import parse_cookie_string
            raw_cookie = cookie_file.read_text(encoding="utf-8").strip()
            if raw_cookie:
                cookies = parse_cookie_string(raw_cookie)
                if cookies:
                    await context.add_cookies(cookies)
        except Exception:
            pass
    return context


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

            # AIOMMO: bản gốc chờ cố định 5 giây rồi đòi có ô nhập chat -> qua proxy chậm trang chưa tải xong thì báo nhầm
            # "chưa đăng nhập". Chờ câu trả lời của chính Dola (/alice/user/launch, is_login) tối đa ~25 giây.
            from aiommo_compat import USER_LAUNCH_SUFFIX, parse_user_launch
            server_state = {"login": None}

            async def on_response(resp):
                if resp.url.split("?")[0].endswith(USER_LAUNCH_SUFFIX):
                    try:
                        server_state["login"] = parse_user_launch(await resp.text())
                    except Exception:
                        pass

            page.on("response", lambda r: asyncio.ensure_future(on_response(r)))
            await page.goto("https://www.dola.com/chat", timeout=60000, wait_until="domcontentloaded")
            for _ in range(50):
                if server_state["login"] is not None:
                    break
                await page.wait_for_timeout(500)

            cookies = await context.cookies("https://www.dola.com")
            if not cookie_value(cookies, "sessionid"):
                return False
            if server_state["login"] is not None:
                return bool(server_state["login"])

            # Dola không trả lời: quay lại cách của bản gốc nhưng chờ ô nhập chat tới ~20 giây thay vì 5 giây
            for _ in range(40):
                if await page.evaluate(
                    """() => !!(document.querySelector('textarea')
                            || document.querySelector('[contenteditable="true"]')
                            || document.querySelector('input[type="text"]'))"""
                ):
                    return True
                await page.wait_for_timeout(500)
            return False
        finally:
            await context.close()
