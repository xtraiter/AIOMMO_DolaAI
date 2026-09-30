"""Facebook Cookie to Dola Account Converter.

Automates the OAuth bridge:
1. Injects Facebook cookie (.facebook.com).
2. Navigates to Dola and clicks 'Login with Facebook'.
3. Automatically confirms Facebook OAuth ('Continue as ...').
4. Captures newly minted Dola session cookies (sessionid).
5. Persists to accounts/<account>/cookie.txt and registers with Gateway.
"""
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
import config
from browser import LAUNCH_ARGS, cookie_value, proxy_for_account, _proxy_from_url


def parse_fb_cookie(raw: str) -> list[dict]:
    """Parse raw Facebook cookie string, JSON export, or pipe format into Playwright cookies."""
    cookies = []
    raw = raw.strip()
    if not raw:
        return cookies

    # Handle pipe format: uid|pass|2fa|cookie or similar
    if "|" in raw:
        parts = raw.split("|")
        # Find part containing c_user or xs
        for part in parts:
            if "c_user=" in part or "xs=" in part:
                raw = part.strip()
                break

    exp = int(time.time()) + 365 * 86400

    # JSON export
    if (raw.startswith("[") and raw.endswith("]")) or (raw.startswith("{") and raw.endswith("}")):
        try:
            data = json.loads(raw)
            items = data if isinstance(data, list) else [data]
            for it in items:
                n = it.get("name", "").strip()
                v = it.get("value", "").strip()
                if n:
                    dom = it.get("domain", ".facebook.com")
                    if "facebook" not in dom:
                        dom = ".facebook.com"
                    cookies.append({
                        "name": n,
                        "value": v,
                        "domain": dom,
                        "path": it.get("path", "/"),
                        "expires": exp,
                    })
            if cookies:
                return cookies
        except Exception:
            pass

    # Semicolon-delimited header string (e.g. c_user=1000...; xs=...; fr=...)
    for item in raw.split(";"):
        item = item.strip()
        if not item or "=" not in item:
            continue
        name, val = item.split("=", 1)
        name = name.strip()
        val = val.strip()
        if not name:
            continue
        cookies.append({
            "name": name,
            "value": val,
            "domain": ".facebook.com",
            "path": "/",
            "expires": exp,
        })

    return cookies


async def convert_fb_to_dola_session(account: str, fb_cookie_str: str, proxy: str = None, headless: bool = True) -> dict:
    """Converts Facebook session into active Dola session by completing OAuth flow."""
    fb_cookies = parse_fb_cookie(fb_cookie_str)
    has_cuser = any(c["name"] == "c_user" for c in fb_cookies)
    has_xs = any(c["name"] == "xs" for c in fb_cookies)
    if not (has_cuser or has_xs):
        return {
            "ok": False,
            "error": "Cookie Facebook không hợp lệ (cần có ít nhất 'c_user' hoặc 'xs').",
        }

    profile_dir = Path("accounts") / account
    profile_dir.mkdir(parents=True, exist_ok=True)

    args = list(LAUNCH_ARGS)
    kwargs = {
        "headless": headless,
        "args": args,
        "locale": "vi-VN",
        "timezone_id": "Asia/Ho_Chi_Minh",
    }
    use_proxy = (_proxy_from_url(proxy) if proxy else None) or proxy_for_account(account)
    if use_proxy:
        kwargs["proxy"] = use_proxy

    print(f"[{account}] Bắt đầu quy trình xác thực Dola qua Cookie Facebook...", flush=True)

    async with async_playwright() as p:
        context = await p.chromium.launch_persistent_context(str(profile_dir), **kwargs)
        try:
            # 1. Inject Facebook cookies
            await context.add_cookies(fb_cookies)
            page = context.pages[0] if context.pages else await context.new_page()

            # 2. Navigate to Dola chat
            print(f"[{account}] Mở https://www.dola.com/chat ...", flush=True)
            await page.goto("https://www.dola.com/chat", timeout=60000, wait_until="domcontentloaded")
            await page.wait_for_timeout(3000)

            # Check if already logged in on Dola
            existing_cookies = await context.cookies("https://www.dola.com")
            existing_sid = cookie_value(existing_cookies, "sessionid")
            has_login_btn = await page.locator('[data-testid="to_login_button"]').count() > 0 or \
                            await page.locator('button:has-text("ログイン"), button:has-text("Log In"), button:has-text("Sign in")').count() > 0

            if existing_sid and not has_login_btn:
                print(f"[{account}] Đã có sẵn phiên Dola hợp lệ!", flush=True)
                full_header = "; ".join([f"{c['name']}={c['value']}" for c in existing_cookies])
                (profile_dir / "cookie.txt").write_text(full_header, encoding="utf-8")
                return {"ok": True, "account": account, "cookie": full_header, "sessionid": existing_sid}

            # 3. Dismiss initial cookie banner
            try:
                ok_btn = page.locator('button:has-text("OK"), button:has-text("Accept"), button:has-text("Agree")').first
                if await ok_btn.count() and await ok_btn.is_visible():
                    await ok_btn.click(timeout=1500)
                    await page.wait_for_timeout(500)
            except Exception:
                pass

            # 4. Open Login Modal
            login_btn = page.locator('[data-testid="to_login_button"]').first
            if not (await login_btn.count()):
                login_btn = page.locator('button:has-text("ログイン"), button:has-text("Log In"), button:has-text("Sign in")').first

            if await login_btn.count():
                try:
                    await login_btn.click(force=True, timeout=5000)
                except Exception:
                    await login_btn.dispatch_event("click")
                await page.wait_for_timeout(2000)

            # 5. Listen for Facebook popup or redirection
            fb_popup = None
            async def on_popup(p_page):
                nonlocal fb_popup
                fb_popup = p_page
                print(f"[{account}] Phát hiện cửa sổ xác thực Facebook...", flush=True)

            context.on("page", on_popup)

            # Click Facebook login button
            fb_btn = page.locator('[data-testid="login_third_facebook"]').first
            if not (await fb_btn.count()):
                fb_btn = page.locator('div[data-testid*="facebook"], button:has-text("Facebook"), [aria-label*="Facebook" i]').first

            if not (await fb_btn.count()):
                await page.screenshot(path="fb_btn_not_found.png")
                return {"ok": False, "error": "Không tìm thấy nút đăng nhập Facebook trên giao diện Dola."}

            print(f"[{account}] Bấm nút Đăng nhập bằng Facebook...", flush=True)
            await fb_btn.click(force=True, timeout=5000)

            # Wait up to 20 seconds for OAuth confirmation
            confirmed = False
            for _ in range(40):
                await page.wait_for_timeout(500)

                # Check popup if present
                active_target = fb_popup if (fb_popup and not fb_popup.is_closed()) else page
                target_url = active_target.url.lower()

                if "facebook.com" in target_url:
                    # Look for OAuth confirm buttons
                    for confirm_sel in (
                        'button[name="__CONFIRM__"]',
                        'button:has-text("Tiếp tục dưới tên")',
                        'button:has-text("Continue as")',
                        'button:has-text("Tiếp tục")',
                        'button:has-text("Continue")',
                        'div[role="button"]:has-text("Tiếp tục")',
                        'div[role="button"]:has-text("Continue")',
                        'button[type="submit"]',
                    ):
                        try:
                            c_btn = active_target.locator(confirm_sel).first
                            if await c_btn.count() and await c_btn.is_visible():
                                print(f"[{account}] Tự động xác nhận ủy quyền Facebook ('{confirm_sel}')...", flush=True)
                                await c_btn.click(timeout=3000)
                                await page.wait_for_timeout(2000)
                                confirmed = True
                                break
                        except Exception:
                            pass

                # Check if we are back on dola.com with active session
                dola_cookies = await context.cookies("https://www.dola.com")
                sid = cookie_value(dola_cookies, "sessionid") or cookie_value(dola_cookies, "sessionid_ss")
                if sid:
                    print(f"[{account}] Đã bắt được token Dola (sessionid={sid[:8]}...)!", flush=True)
                    confirmed = True
                    break

            # 6. Wait for main page to settle on dola.com
            await page.wait_for_timeout(3000)

            # Dismiss age verification / terms popup if appears
            for confirm_btn_sel in (
                'button:has-text("年齢を確認してください")',
                'button:has-text("OK")',
                'button:has-text("Confirm")',
                'button:has-text("確認")',
                'button:has-text("Agree")',
                'button:has-text("同意")',
            ):
                try:
                    c_btn = page.locator(confirm_btn_sel).first
                    if await c_btn.count() and await c_btn.is_visible():
                        print(f"[{account}] Xác nhận thông báo điều khoản ({confirm_btn_sel})...", flush=True)
                        await c_btn.click(timeout=2000)
                        await page.wait_for_timeout(1000)
                except Exception:
                    pass

            # 7. Extract final Dola cookies
            final_cookies = await context.cookies("https://www.dola.com")
            final_sid = cookie_value(final_cookies, "sessionid") or cookie_value(final_cookies, "sessionid_ss")

            if not final_sid:
                await page.screenshot(path="fb_convert_failed.png")
                return {
                    "ok": False,
                    "error": "Không thể lấy cookie Dola sau khi đăng nhập Facebook. Cookie Facebook có thể đã hết hạn hoặc bị checkpoint."
                }

            full_dola_cookie = "; ".join([f"{c['name']}={c['value']}" for c in final_cookies])
            (profile_dir / "cookie.txt").write_text(full_dola_cookie, encoding="utf-8")

            print(f"[{account}] CHUYỂN ĐỔI THÀNH CÔNG! Đã lưu Cookie Dola vào {profile_dir / 'cookie.txt'}", flush=True)
            return {
                "ok": True,
                "account": account,
                "cookie": full_dola_cookie,
                "sessionid": final_sid
            }

        finally:
            await context.close()


async def main():
    if len(sys.argv) < 2:
        print("Usage:")
        print("  py -3 fb_to_dola.py <account_name> \"<fb_cookie_string>\"")
        print("  py -3 fb_to_dola.py --file fb_cookies.txt")
        return

    if sys.argv[1] == "--file":
        file_path = sys.argv[2] if len(sys.argv) > 2 else "fb_cookies.txt"
        p = Path(file_path)
        if not p.exists():
            print(f"File khong ton tai: {file_path}")
            return
        lines = [l.strip() for l in p.read_text(encoding="utf-8-sig").splitlines() if l.strip() and not l.startswith("#")]
        print(f"Tim thay {len(lines)} dong cookie Facebook trong {file_path}.")
        for idx, line in enumerate(lines, 1):
            name = f"FB_{idx}"
            cookie_str = line
            if "|" in line:
                parts = line.split("|")
                if len(parts) >= 2 and not ("c_user=" in parts[0] or "xs=" in parts[0]):
                    name = parts[0].strip()
                    cookie_str = "|".join(parts[1:]).strip()
            print(f"\n[{idx}/{len(lines)}] Dang xu ly '{name}'...")
            res = await convert_fb_to_dola_session(name, cookie_str)
            if res.get("ok"):
                print(f"  ✓ '{name}': Thanh cong!")
            else:
                print(f"  ✗ '{name}': That bai ({res.get('error')})")
    else:
        acc = sys.argv[1].strip()
        cookie_arg = sys.argv[2].strip() if len(sys.argv) > 2 else ""
        if not cookie_arg:
            cookie_arg = input("Dan chuoi Cookie Facebook cua ban vao day: ").strip()
        res = await convert_fb_to_dola_session(acc, cookie_arg, headless=False)
        if res.get("ok"):
            print("\n" + "=" * 50)
            print(f"THÀNH CÔNG! Cookie Dola cho tài khoản '{acc}':")
            print(res["cookie"])
            print("=" * 50)
        else:
            print(f"\n[!] LỖI: {res.get('error')}")


if __name__ == "__main__":
    asyncio.run(main())
