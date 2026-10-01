"""Open an account profile in a visible Chromium window (patchright) - used by DolaCoordinator.

Uses the SAME launcher as the gateway (browser.launch_account_context): same Chromium build,
same args, locale ja-JP / timezone Asia/Tokyo, DOLA_PROXY, and cookie.txt re-injection.
So a session created here is exactly what the gateway will use when rendering.

Usage:
  py -3 open_profile.py <account_name> [--login google|facebook|facebook-cookie] [--after keep|close]

  --login  auto-login on Dola. Secrets are read from ONE line of JSON on stdin; they are never taken from
           the command line and never written to disk or printed.
             google / facebook : {"email": "...", "password": "...", "totp": "BASE32 secret or empty"}
             facebook-cookie   : {"cookie": "c_user=...; xs=..."}  (like fb_to_dola.py, but interactive: the
                                 Facebook cookie is loaded, Facebook is opened to confirm it works, then Dola's
                                 "Continue with Facebook" is used and the Dola session is saved to cookie.txt)
           Captcha, 2FA without a TOTP secret, security checkpoints and wrong passwords are NOT automated:
           the script reports them (need_human) and waits while you solve them in the window.
  --after  what to do once Dola reports a logged-in session: keep the window open (default) or close it.

Protocol with DolaCoordinator (all files live in accounts/<name>/):
  .profile_status.json  written every ~2s while the window is open:
                        {"pid", "logged_in": bool, "phase": str, "need_human": str|null, "updated": <unix ts>}
                        removed on exit.
  cookie.txt            updated with the dola.com cookie header when a login is detected
                        (same file/format the gateway reads and add_account_cookie.py writes).
  .close_request        create this file to ask the window to close gracefully.
"""
import argparse
import asyncio
import json
import os
import re
import sys
import time
from pathlib import Path

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

from patchright.async_api import async_playwright

from add_account import totp
from fb_to_dola import parse_fb_cookie, OAUTH_CONFIRM_JS
from aiommo_compat import USER_LAUNCH_SUFFIX, cookie_value, launch_account_context, parse_user_launch

NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,32}$")
CHAT_URL = "https://www.dola.com/chat"
POLL_SEC = 2
LOGIN_TIMEOUT_SEC = 15 * 60          # how long the assisted login waits (captcha / 2FA can take a while)
FALLBACK_AFTER_SEC = 45              # no answer from Dola's login endpoint for this long -> judge by the UI
CLOSE_GRACE_SEC = 3                  # let cookies flush to disk before closing after a successful login
MAX_FILLS = 2                        # fill a credential field at most this many times (wrong password guard)
STUCK_AFTER_SEC = 40                 # no recognised step for this long -> tell the user (and screenshot the page)
SUBMIT_WAIT_SEC = 8                 # after submitting a form, give the page this long before touching it again

LOGIN_BUTTON_SELECTORS = (
    'button:has-text("ログイン")', 'button:has-text("Sign in")', 'button:has-text("Log in")',
    'button:has-text("登录")', 'a:has-text("ログイン")',
)
GOOGLE_BUTTONS = (
    '[data-testid="login_third_google"]', 'text=Googleで続ける', 'text=Continue with Google',
    'button:has-text("Google")',
)
FACEBOOK_BUTTONS = (
    '[data-testid="login_third_facebook"]', 'div[data-testid*="facebook"]', 'button:has-text("Facebook")',
    '[aria-label*="Facebook" i]',
)
# Nút xác nhận OAuth ("Tiếp tục dưới tên …"). Facebook có thể ra bất kỳ ngôn ngữ nào theo cookie/tài khoản,
# nhưng vẫn giữ nhiều ngôn ngữ (Pháp/Anh/Nhật/Trung/TBN) phòng khi Facebook theo ngôn ngữ của tài khoản.
OAUTH_CONFIRM_SELECTORS = (
    'button[name="__CONFIRM__"]',
    'button:has-text("Tiếp tục dưới tên")', 'div[role="button"]:has-text("Tiếp tục dưới tên")',
    'button:has-text("Continue as")', 'div[role="button"]:has-text("Continue as")',
    'button:has-text("Continuer en tant que")', 'div[role="button"]:has-text("Continuer en tant que")',
    'a[role="button"]:has-text("Continuer en tant que")',
    'button:has-text("Continuer")', 'div[role="button"]:has-text("Continuer")',
    'button:has-text("Continuar como")', 'div[role="button"]:has-text("Continuar como")',
    'button:has-text("Tiếp tục")', 'div[role="button"]:has-text("Tiếp tục")',
    'button:has-text("Continue")', 'div[role="button"]:has-text("Continue")',
    'button:has-text("続行")', 'button:has-text("次へ")', 'div[role="button"]:has-text("続行")',
    'button:has-text("继续")', 'button:has-text("繼續")',
)
GOOGLE_CONSENT_SELECTORS = (
    "#submit_button", "[role='button']:has-text('続行')", "button:has-text('続行')",
    "[role='button']:has-text('继续')", "button:has-text('继续')",
    "[role='button']:has-text('Continue')", "button:has-text('Continue')",
    "[role='button']:has-text('Tiếp tục')", "button:has-text('Tiếp tục')",
)
CAPTCHA_SELECTORS = (
    'iframe[src*="recaptcha"]', 'iframe[src*="captcha"]', '#captchaimg', 'img[src*="captcha"]',
    'input[name="captcha_response"]', 'input[name="ca"]',
)


# ------------------------------------------------------------------ status file

class State:
    """Shared between the main loop and the assisted-login task."""

    def __init__(self):
        self.server_login = None      # latest answer from Dola's /alice/user/launch: True / False / None (unknown)
        self.phase = "starting"
        self.need_human = None
        self.login_done = asyncio.Event()
        self.started = time.time()
        self.shot_path = None         # accounts/<name>/need_human.png: page screenshot taken when the user is needed


def write_status(profile_dir: Path, logged_in: bool, st: State):
    """Atomic write so the coordinator never reads a half-written file."""
    payload = json.dumps({
        "pid": os.getpid(), "logged_in": logged_in, "phase": st.phase,
        "need_human": st.need_human, "updated": time.time(),
    }, ensure_ascii=False)
    tmp = profile_dir / ".profile_status.json.tmp"
    target = profile_dir / ".profile_status.json"
    # On Windows the replace can fail for a moment while another process (the coordinator, antivirus) has the
    # file open. A missed heartbeat is harmless (the next one is 2s away) - it must never kill the window.
    for _ in range(3):
        try:
            tmp.write_text(payload, encoding="utf-8")
            os.replace(tmp, target)
            return
        except PermissionError:
            time.sleep(0.2)
    print("WARN: could not update .profile_status.json this round", flush=True)


_last_human = None


def ask_human(st: State, reason):
    """Report (once per distinct reason) that something needs the user's hands."""
    global _last_human
    st.need_human = reason
    if reason and reason != _last_human:
        print(f"NEED_HUMAN: {reason}", flush=True)
    _last_human = reason


# ------------------------------------------------------------------ small page helpers

async def visible(page, selector) -> bool:
    try:
        loc = page.locator(selector).first
        return bool(await loc.count() and await loc.is_visible())
    except Exception:
        return False


async def click_first(page, selectors, timeout=3000) -> bool:
    for sel in selectors:
        try:
            loc = page.locator(sel).first
            if await loc.count() and await loc.is_visible():
                await loc.click(timeout=timeout)
                return True
        except Exception:
            continue
    return False


async def js_click(page, selector):
    """DOM click: Google's next buttons ignore synthetic pointer clicks in automation."""
    await page.locator(selector).first.evaluate("e => e.click()")


async def click_oauth_confirm_js(page) -> bool:
    """Bấm nút chính của màn OAuth theo DOM (không theo chữ). CHỈ gọi khi đang ở màn consent/oauth."""
    try:
        txt = await page.evaluate(OAUTH_CONFIRM_JS)
    except Exception:
        txt = None
    if txt:
        print(f"Xác nhận OAuth Facebook (nút '{txt}', nhận diện theo nút chính).", flush=True)
        return True
    return False


async def has_captcha(page) -> bool:
    for sel in CAPTCHA_SELECTORS:
        if await visible(page, sel):
            return True
    return False


async def detect_login(context, page, st: State):
    """Return (logged_in, cookie_header).

    Primary signal: Dola's own /alice/user/launch answer (is_login) - see browser.parse_user_launch.
    Fallback (endpoint silent for FALLBACK_AFTER_SEC): sessionid present and no login button visible.
    """
    cookies = await context.cookies("https://www.dola.com")
    sid = cookie_value(cookies, "sessionid") or cookie_value(cookies, "sessionid_ss")
    if not sid:
        return False, "", ""

    if st.server_login is True:
        ok = True
    elif st.server_login is False:
        ok = False
    elif time.time() - st.started > FALLBACK_AFTER_SEC and page is not None and not page.is_closed():
        ok = True
        for sel in LOGIN_BUTTON_SELECTORS + ('[data-testid="to_login_button"]',):
            if await visible(page, sel):
                ok = False
                break
    else:
        ok = False

    header = "; ".join(f"{c['name']}={c['value']}" for c in cookies) if ok else ""
    return ok, header, sid


# ------------------------------------------------------------------ assisted login

async def open_login_modal(page, method_buttons) -> bool:
    """Dola page: dismiss cookie banner, open the login modal, click the Google / Facebook button."""
    await click_first(page, ('button:has-text("OK")', 'button:has-text("Accept")', 'button:has-text("Agree")'), 1500)
    await page.wait_for_timeout(500)

    # Dola is a SPA: right after a (re)load the login button is in the DOM before its click handler exists, so a
    # single click is silently ignored. Click, check that the modal really opened, retry.
    for _attempt in range(4):
        for sel in method_buttons:
            if await visible(page, sel):
                try:
                    await page.locator(sel).first.click(timeout=5000)
                    return True
                except Exception:
                    break  # modal is animating / re-rendering: retry the whole attempt

        try:
            login_btn = page.locator('[data-testid="to_login_button"]').first
            await login_btn.wait_for(state="visible", timeout=15000)
            await login_btn.click(timeout=5000)
        except Exception:
            await click_first(page, LOGIN_BUTTON_SELECTORS, 3000)
        await page.wait_for_timeout(2500)
    return False


async def handle_google(pg, creds, filled, st: State) -> bool:
    """One step on an accounts.google.com page. Returns True if something was done."""
    url = pg.url

    if await has_captcha(pg):
        ask_human(st, "Google yêu cầu captcha — hãy giải trong cửa sổ trình duyệt.")
        return False

    if "accountchooser" in url:
        acc = pg.locator(f"text={creds['email']}").first
        if await acc.count() and await acc.is_visible():
            await acc.click(timeout=5000)
            return True

    identifier = pg.locator("#identifierId").first
    if await identifier.count() and await identifier.is_visible():
        if time.time() - filled.get("g_email_at", 0) < SUBMIT_WAIT_SEC:
            return False  # previous submit is still being processed
        if filled["g_email"] >= MAX_FILLS:
            ask_human(st, "Google không nhận email đã nhập — kiểm tra lại email trong cửa sổ.")
            return False
        await identifier.fill(creds["email"])
        await js_click(pg, "#identifierNext")
        filled["g_email"] += 1
        filled["g_email_at"] = time.time()
        st.need_human = None
        return True

    pwd = pg.locator('input[name="Passwd"]').first
    if await pwd.count() and await pwd.is_visible():
        if time.time() - filled.get("g_pass_at", 0) < SUBMIT_WAIT_SEC:
            return False  # previous submit is still being processed
        if filled["g_pass"] >= MAX_FILLS:
            ask_human(st, "Mật khẩu Google có vẻ sai — hãy kiểm tra/nhập lại trong cửa sổ.")
            return False
        await pg.wait_for_timeout(600)
        await pwd.fill(creds["password"])
        await js_click(pg, "#passwordNext")
        filled["g_pass"] += 1
        filled["g_pass_at"] = time.time()
        st.need_human = None
        return True

    # 2FA with an authenticator code
    code_input = pg.locator('input[name="totpPin"], #totpPin, input[type="tel"]').first
    if await code_input.count() and await code_input.is_visible():
        if not creds.get("totp"):
            ask_human(st, "Google yêu cầu mã xác minh 2 bước (2FA) — hãy nhập mã trong cửa sổ.")
            return False
        window = int(time.time() // 30)
        if filled.get("g_totp_window") == window:
            return False  # this 30s code was already tried; wait for the next window instead of hammering
        try:
            await code_input.fill(totp(creds["totp"]))
        except Exception:
            ask_human(st, "Khóa 2FA (TOTP) không hợp lệ — hãy nhập mã thủ công trong cửa sổ.")
            return False
        filled["g_totp_window"] = window
        await click_first(pg, ("#totpNext", 'button:has-text("Next")', 'button:has-text("次へ")', 'button:has-text("Tiếp theo")'))
        st.need_human = None
        return True

    if "/challenge" in url or "signin/v2/challenge" in url or "speedbump" in url:
        ask_human(st, "Google yêu cầu xác minh bổ sung (điện thoại/thiết bị/email khôi phục) — hãy xử lý trong cửa sổ.")
        return False

    if await click_first(pg, GOOGLE_CONSENT_SELECTORS):
        st.need_human = None
        return True
    return False


async def handle_facebook(pg, creds, filled, st: State) -> bool:
    """One step on a facebook.com page. Returns True if something was done."""
    url = pg.url.lower()

    if await has_captcha(pg):
        ask_human(st, "Facebook yêu cầu captcha — hãy giải trong cửa sổ trình duyệt.")
        return False

    # OAuth confirm theo chữ (an toàn trên mọi trang FB).
    if await click_first(pg, OAUTH_CONFIRM_SELECTORS):
        st.need_human = None
        return True
    # Dự phòng độc lập ngôn ngữ: CHỈ ở màn consent/oauth (tránh bấm nhầm nút Đăng nhập của form login).
    if ("/privacy/consent" in url or "/dialog/oauth" in url) and await click_oauth_confirm_js(pg):
        st.need_human = None
        return True

    email = pg.locator("input#email, input[name='email']").first
    password = pg.locator("input#pass, input[name='pass']").first
    if await email.count() and await email.is_visible():
        if creds is None:
            ask_human(st, "Facebook chưa đăng nhập (cookie hết hạn hoặc bị từ chối) — hãy đăng nhập Facebook thủ công trong cửa sổ.")
            return False
        if time.time() - filled.get("f_login_at", 0) < SUBMIT_WAIT_SEC:
            return False  # previous submit is still being processed
        if filled["f_login"] >= MAX_FILLS:
            ask_human(st, "Facebook không nhận thông tin đăng nhập — kiểm tra email/mật khẩu trong cửa sổ.")
            return False
        await email.fill(creds["email"])
        if await password.count() and await password.is_visible():
            await password.fill(creds["password"])
        # Enter works in every UI language (the login control is a div[role=button] with localized text)
        target = password if await password.count() and await password.is_visible() else email
        await target.press("Enter")
        await pg.wait_for_timeout(1500)
        if await email.count() and await email.is_visible():  # still on the form: try the buttons as a fallback
            await click_first(pg, (
                'button[name="login"]', "#loginbutton", '[data-testid="royal_login_button"]', 'button[type="submit"]',
                'div[role="button"]:has-text("ログイン")', 'div[role="button"]:has-text("Log In")',
                'div[role="button"]:has-text("Đăng nhập")',
            ))
        filled["f_login"] += 1
        filled["f_login_at"] = time.time()
        st.need_human = None
        return True

    code_input = pg.locator("input[name='approvals_code'], input[autocomplete='one-time-code']").first
    if await code_input.count() and await code_input.is_visible():
        if not creds or not creds.get("totp"):
            ask_human(st, "Facebook yêu cầu mã 2FA — hãy nhập mã trong cửa sổ.")
            return False
        window = int(time.time() // 30)
        if filled.get("f_totp_window") == window:
            return False
        try:
            await code_input.fill(totp(creds["totp"]))
        except Exception:
            ask_human(st, "Khóa 2FA (TOTP) không hợp lệ — hãy nhập mã thủ công trong cửa sổ.")
            return False
        filled["f_totp_window"] = window
        await click_first(pg, ("#checkpointSubmitButton", 'button[type="submit"]', 'div[role="button"]:has-text("Continue")'))
        st.need_human = None
        return True

    if any(k in url for k in ("checkpoint", "two_step_verification", "two_factor", "/login/device-based", "recover")):
        ask_human(st, "Facebook yêu cầu xác minh/checkpoint — hãy xử lý trong cửa sổ.")
        return False

    # "Trust this browser?" / "Save login info?" style prompts
    if await click_first(pg, ('button:has-text("Tin cậy")', 'button:has-text("Trust")', 'button:has-text("Tiếp tục")', 'button:has-text("Continue")')):
        return True
    return False


async def confirm_dola_popups(pg):
    """Dola's 18+ / terms confirmation after the first login (same handling as add_account.py)."""
    try:
        if await pg.locator("text=18").count():
            await pg.evaluate("""() => {
                const els = [...document.querySelectorAll('button, [role="button"], div, span')];
                const t = els.find(e => (e.textContent || '').trim() === 'OK' && e.childElementCount === 0);
                if (t) t.click();
            }""")
    except Exception:
        pass


async def verify_facebook_cookie(context, st: State, deadline) -> bool:
    """Phase 1 of the cookie flow: open facebook.com with the injected cookie and wait until it is really logged in.

    Checkpoints / captcha / 2FA / an expired cookie are reported through need_human and waited on: the user solves
    them in this tab, then the flow continues to Dola.
    """
    st.phase = "verify_facebook"
    fb = await context.new_page()
    try:
        try:
            await fb.goto("https://www.facebook.com/", timeout=60000, wait_until="domcontentloaded")
        except Exception as e:
            print(f"WARN: cannot open facebook.com: {type(e).__name__}", flush=True)

        while time.time() < deadline:
            await asyncio.sleep(1.5)
            if fb.is_closed():
                ask_human(st, "Bạn đã đóng tab Facebook — mở lại profile để thử lại.")
                return False
            url = fb.url.lower()
            cookies = await context.cookies("https://www.facebook.com")
            has_user = bool(cookie_value(cookies, "c_user"))

            if await has_captcha(fb):
                ask_human(st, "Facebook yêu cầu captcha — hãy giải trong tab Facebook.")
            elif any(k in url for k in ("checkpoint", "two_step_verification", "two_factor", "/login/device-based", "recover")):
                ask_human(st, "Facebook yêu cầu xác minh/2FA/checkpoint — hãy xử lý trong tab Facebook.")
            elif await visible(fb, "input#email, input[name='email']") or not has_user:
                ask_human(st, "Cookie Facebook hết hạn hoặc bị từ chối — hãy đăng nhập Facebook thủ công trong tab này.")
            else:
                st.need_human = None
                print("FB_OK: Facebook đã đăng nhập bằng cookie.", flush=True)
                return True
        return False
    finally:
        if not fb.is_closed():
            try:
                await fb.close()
            except Exception:
                pass


async def assisted_login(context, dola_page, method, creds, st: State):
    """Drive Dola -> Google/Facebook login. Never gives up on captcha/2FA: it waits for the human."""
    filled = {"g_email": 0, "g_pass": 0, "f_login": 0}
    deadline = time.time() + LOGIN_TIMEOUT_SEC
    shot_reason = None

    if method == "facebook-cookie":
        # Phase 1: prove the Facebook cookie works (a human may need to clear a checkpoint) ...
        if not await verify_facebook_cookie(context, st, deadline):
            if not st.login_done.is_set():
                print("LOGIN_TIMEOUT", flush=True)
            return
        # ... Phase 2: back to Dola and continue with "Continue with Facebook" (no credentials needed any more)
        method, creds = "facebook", None
        try:
            await dola_page.bring_to_front()
            await dola_page.goto(CHAT_URL, timeout=60000, wait_until="domcontentloaded")
        except Exception as e:
            print(f"WARN: cannot reopen Dola: {type(e).__name__}", flush=True)

    method_buttons = GOOGLE_BUTTONS if method == "google" else FACEBOOK_BUTTONS
    label = "Google" if method == "google" else "Facebook"

    st.phase = "open_login"
    await asyncio.sleep(2)
    if st.server_login is True:
        return

    if not await open_login_modal(dola_page, method_buttons):
        ask_human(st, f"Không tìm thấy nút đăng nhập {label} trên Dola — hãy tự bấm trong cửa sổ.")
    else:
        print(f"Đã bấm đăng nhập bằng {label}.", flush=True)

    st.phase = "authenticating"
    last_progress = time.time()
    while time.time() < deadline and not st.login_done.is_set():
        await asyncio.sleep(1.5)
        for pg in [p for p in context.pages if not p.is_closed()]:
            try:
                host = pg.url.split("/")[2] if "//" in pg.url else ""
                did = False
                if method == "google" and host.endswith("accounts.google.com"):
                    did = await handle_google(pg, creds, filled, st)
                elif method == "facebook" and host.endswith("facebook.com"):
                    did = await handle_facebook(pg, creds, filled, st)
                elif host.endswith("dola.com"):
                    await confirm_dola_popups(pg)

                if did:
                    last_progress = time.time()
                elif not st.need_human and time.time() - last_progress > STUCK_AFTER_SEC:
                    # Nothing we recognise for a while (e.g. approve-on-phone prompt, "is this you?" page, new UI)
                    ask_human(st, f"Đang chờ nhưng không nhận ra bước hiện tại ({host or 'trang trống'}) — hãy xem cửa sổ trình duyệt và xử lý.")

                if st.need_human and st.need_human != shot_reason and st.shot_path is not None:
                    shot_reason = st.need_human
                    await pg.screenshot(path=str(st.shot_path))  # page content only, never the desktop
            except Exception as e:  # page navigating / closed mid-step: retry next round
                print(f"(bỏ qua lỗi tạm thời: {type(e).__name__}: {str(e)[:80]})", flush=True)

    if not st.login_done.is_set():
        ask_human(st, "Hết thời gian chờ đăng nhập tự động — cửa sổ vẫn mở, hãy đăng nhập thủ công.")
        print("LOGIN_TIMEOUT", flush=True)


# ------------------------------------------------------------------ main

def parse_args():
    ap = argparse.ArgumentParser(add_help=False)
    ap.add_argument("account")
    ap.add_argument("--login", choices=("google", "facebook", "facebook-cookie"))
    ap.add_argument("--after", choices=("keep", "close"), default="keep")
    return ap.parse_args()


async def read_credentials(method):
    """One JSON line from stdin (the parent writes it and closes the pipe)."""
    line = await asyncio.get_running_loop().run_in_executor(None, sys.stdin.readline)
    try:
        creds = json.loads(line)
    except ValueError:
        return None
    if not isinstance(creds, dict):
        return None
    if method == "facebook-cookie":
        cookie = str(creds.get("cookie") or "").strip()
        return {"cookie": cookie} if cookie else None
    if not creds.get("email") or not creds.get("password"):
        return None
    return {"email": str(creds["email"]), "password": str(creds["password"]), "totp": str(creds.get("totp") or "")}


async def main():
    args = parse_args()
    account = args.account.strip()
    if not NAME_RE.match(account):
        print("ERROR: invalid account name (use A-Z a-z 0-9 _ -, max 32 chars)", flush=True)
        sys.exit(2)

    creds = None
    if args.login:
        creds = await read_credentials(args.login)
        if creds is None:
            print("ERROR: --login needs one JSON line on stdin (see usage)", flush=True)
            sys.exit(2)
        if args.login == "facebook-cookie":
            fb_cookies = parse_fb_cookie(creds["cookie"])
            if not any(c["name"] in ("c_user", "xs") for c in fb_cookies):
                print("ERROR: Facebook cookie must contain c_user or xs", flush=True)
                sys.exit(2)

    profile_dir = Path("accounts") / account
    profile_dir.mkdir(parents=True, exist_ok=True)
    close_flag = profile_dir / ".close_request"
    status_file = profile_dir / ".profile_status.json"
    for stale in (close_flag, status_file):
        try:
            stale.unlink(missing_ok=True)
        except OSError:
            pass

    st = State()
    st.shot_path = profile_dir / "need_human.png"

    async with async_playwright() as p:
        context = await launch_account_context(p, account, headless=False)
        closed = asyncio.Event()
        context.on("close", lambda *_: closed.set())

        if args.login == "facebook-cookie":
            await context.add_cookies(fb_cookies)  # .facebook.com cookies, same as fb_to_dola.py

        async def on_response(resp):
            if resp.url.split("?")[0].endswith(USER_LAUNCH_SUFFIX):
                try:
                    state = parse_user_launch(await resp.text())
                except Exception:
                    return
                if state is not None:
                    st.server_login = state

        context.on("response", lambda r: asyncio.ensure_future(on_response(r)))

        login_task = None
        try:
            page = context.pages[0] if context.pages else await context.new_page()
            try:
                await page.set_viewport_size({"width": 1280, "height": 850})
                await page.goto(CHAT_URL, timeout=60000, wait_until="domcontentloaded")
            except Exception as e:  # window stays open so the user can retry manually
                print(f"WARN: cannot open {CHAT_URL}: {e}", flush=True)

            initial = await context.cookies("https://www.dola.com")
            last_sid = cookie_value(initial, "sessionid") or cookie_value(initial, "sessionid_ss")
            sid_changed_at = None

            st.phase = "ready"
            write_status(profile_dir, False, st)
            print("READY", flush=True)

            if creds:
                login_task = asyncio.ensure_future(assisted_login(context, page, args.login, creds, st))

            last_header = ""
            logged_since = None
            while not closed.is_set():
                if close_flag.exists():
                    break

                dola_pages = [pg for pg in context.pages if not pg.is_closed() and "dola.com" in pg.url]
                cur = dola_pages[0] if dola_pages else None

                # A new sessionid means a login just happened: Dola's cached "guest" answer is stale, so reload once
                # (after the OAuth callback settles) to get a fresh /alice/user/launch answer.
                try:
                    cks = await context.cookies("https://www.dola.com")
                    sid_now = cookie_value(cks, "sessionid") or cookie_value(cks, "sessionid_ss")
                except Exception:
                    sid_now = last_sid
                if sid_now and sid_now != last_sid:
                    last_sid, sid_changed_at, st.server_login = sid_now, time.time(), None
                if sid_changed_at and time.time() - sid_changed_at >= 3 and cur is not None:
                    sid_changed_at = None
                    try:
                        await cur.reload(wait_until="domcontentloaded", timeout=30000)
                    except Exception:
                        pass

                try:
                    logged_in, header, _ = await detect_login(context, cur, st)
                except Exception:
                    logged_in, header = False, ""

                if logged_in and header and header != last_header:
                    # Persist exactly like add_account_cookie.py, so the gateway reuses this session
                    (profile_dir / "cookie.txt").write_text(header, encoding="utf-8")
                    try:  # đánh dấu cho gateway_main.py import-cookie: cookie này do chính profile tạo ra, không nạp đè lên nó
                        import hashlib
                        (profile_dir / ".cookie_injected").write_text(
                            hashlib.sha256(header.strip().encode("utf-8")).hexdigest(), encoding="utf-8")
                    except OSError:
                        pass
                    last_header = header
                    print("LOGIN_OK", flush=True)

                if logged_in and args.login in ("facebook", "facebook-cookie"):
                    # Keep the Facebook cookie next to cookie.txt (same fb_cookie.txt convention as server.py) so the
                    # Facebook session behind this Dola login can be reused, e.g. for another profile.
                    try:
                        fb_cookies_now = await context.cookies("https://www.facebook.com")
                        if cookie_value(fb_cookies_now, "c_user"):
                            fb_header = "; ".join(f"{c['name']}={c['value']}" for c in fb_cookies_now)
                            fb_file = profile_dir / "fb_cookie.txt"
                            if not fb_file.exists() or fb_file.read_text(encoding="utf-8").strip() != fb_header:
                                fb_file.write_text(fb_header, encoding="utf-8")
                                print("FB_COOKIE_SAVED", flush=True)
                    except Exception:
                        pass

                if logged_in:
                    st.phase = "logged_in"
                    st.need_human = None
                    if not st.login_done.is_set():
                        st.login_done.set()
                        logged_since = time.time()
                else:
                    logged_since = None

                write_status(profile_dir, logged_in, st)

                if args.after == "close" and logged_since and time.time() - logged_since >= CLOSE_GRACE_SEC:
                    print("AUTO_CLOSE", flush=True)
                    break

                try:
                    await asyncio.wait_for(closed.wait(), timeout=POLL_SEC)
                except asyncio.TimeoutError:
                    pass
        finally:
            if login_task and not login_task.done():
                login_task.cancel()
            try:
                await context.close()
            except Exception:
                pass
            for f in (status_file, close_flag):
                try:
                    f.unlink(missing_ok=True)
                except OSError:
                    pass
    print("CLOSED", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
