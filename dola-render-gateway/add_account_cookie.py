"""Import account by injecting Cookie string into persistent browser profile.

Usage:
  py -3 add_account_cookie.py <account_name> "<cookie_string>"
  py -3 add_account_cookie.py --file cookies.txt
"""
import asyncio
import json
import os
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
from browser import LAUNCH_ARGS, cookie_value


def parse_cookie_string(raw: str) -> list[dict]:
    """Parse HTTP Cookie header string, raw token, or JSON export into list of dicts for Playwright."""
    cookies = []
    raw = raw.strip()
    if not raw:
        return cookies

    exp = int(time.time()) + 365 * 86400

    # Support JSON export (e.g. from Cookie-Editor or EditThisCookie extensions)
    if (raw.startswith("[") and raw.endswith("]")) or (raw.startswith("{") and raw.endswith("}")):
        try:
            data = json.loads(raw)
            items = data if isinstance(data, list) else [data]
            for it in items:
                n = it.get("name", "").strip()
                v = it.get("value", "").strip()
                if n:
                    cookies.append({
                        "name": n,
                        "value": v,
                        "domain": it.get("domain", ".dola.com"),
                        "path": it.get("path", "/"),
                        "expires": exp,
                    })
            if cookies:
                return cookies
        except Exception:
            pass

    # If user provided a raw single token (e.g., 32-hex characters without key=value)
    if "=" not in raw and ";" not in raw and len(raw) >= 16:
        cookies.append({
            "name": "sessionid",
            "value": raw,
            "domain": ".dola.com",
            "path": "/",
            "expires": exp,
        })
        return cookies

    # Split by semicolon (standard HTTP Cookie header)
    pairs = raw.split(";")
    for p in pairs:
        p = p.strip()
        if not p or "=" not in p:
            continue
        name, val = p.split("=", 1)
        name = name.strip()
        val = val.strip()
        if not name:
            continue
        cookies.append({
            "name": name,
            "value": val,
            "domain": ".dola.com",
            "path": "/",
            "expires": exp,
        })

    # Fallback: if sessionid_ss exists but sessionid is missing, duplicate it
    has_sid = any(c["name"] == "sessionid" for c in cookies)
    sid_ss = next((c["value"] for c in cookies if c["name"] == "sessionid_ss"), "")
    if not has_sid and sid_ss:
        cookies.append({
            "name": "sessionid",
            "value": sid_ss,
            "domain": ".dola.com",
            "path": "/",
            "expires": exp,
        })

    return cookies


async def import_single_account(account: str, cookie_string: str) -> bool:
    """Creates accounts/<account> profile with injected cookies."""
    profile_dir = Path("accounts") / account
    profile_dir.mkdir(parents=True, exist_ok=True)
    # Persist raw cookie string into cookie.txt
    (profile_dir / "cookie.txt").write_text(cookie_string.strip(), encoding="utf-8")

    cookies_list = parse_cookie_string(cookie_string)
    if not any(c["name"] == "sessionid" for c in cookies_list):
        print(f"[{account}] Warning: Cookie missing 'sessionid='. Session may be invalid.", flush=True)

    print(f"[{account}] Initializing browser profile at {profile_dir}...", flush=True)
    async with async_playwright() as p:
        kwargs = {
            "headless": True,
            "args": list(LAUNCH_ARGS),
            "locale": "ja-JP",
            "timezone_id": "Asia/Tokyo",
        }
        if config.PROXY:
            kwargs["proxy"] = {"server": config.PROXY}

        context = await p.chromium.launch_persistent_context(str(profile_dir), **kwargs)
        try:
            await context.add_cookies(cookies_list)
            page = context.pages[0] if context.pages else await context.new_page()

            print(f"[{account}] Checking session on dola.com...", flush=True)
            try:
                await page.goto("https://www.dola.com/chat", timeout=20000, wait_until="domcontentloaded")
                await page.wait_for_timeout(2000)
            except Exception as e:
                print(f"[{account}] Note: Navigation check ({e}), cookies are saved.", flush=True)

            cks = await context.cookies("https://www.dola.com")
            sid = cookie_value(cks, "sessionid")
            if sid:
                print(f"[{account}] OK Login successful! sessionid = {sid[:10]}***", flush=True)
                return True
            else:
                print(f"[{account}] Injected {len(cookies_list)} cookies into profile.", flush=True)
                return True
        finally:
            await context.close()


async def import_from_file(filepath: str):
    p = Path(filepath)
    if not p.exists():
        print(f"Error: File not found: {filepath}", flush=True)
        return

    lines = p.read_text(encoding="utf-8").splitlines()
    count = 0
    idx = 1
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        # Format: Name|Cookie or raw Cookie
        if "|" in line:
            parts = line.split("|", 1)
            acc_name = parts[0].strip()
            cookie_str = parts[1].strip()
        else:
            acc_name = f"acc_{idx}"
            cookie_str = line

        idx += 1
        success = await import_single_account(acc_name, cookie_str)
        if success:
            count += 1

    print(f"\n==================================================")
    print(f"Đã nạp thành công {count} tài khoản vào pool!")
    print(f"==================================================")


async def main():
    if len(sys.argv) < 2:
        print("Sử dụng:")
        print("  py -3 add_account_cookie.py <tên_tài_khoản> \"<chuỗi_cookie>\"")
        print("  py -3 add_account_cookie.py --file cookies.txt")
        sys.exit(1)

    if sys.argv[1] == "--file":
        fname = sys.argv[2] if len(sys.argv) > 2 else "cookies.txt"
        await import_from_file(fname)
    else:
        name = sys.argv[1]
        cookie = sys.argv[2] if len(sys.argv) > 2 else ""
        if not cookie:
            print("Lỗi: Vui lòng cung cấp chuỗi cookie.")
            sys.exit(1)
        await import_single_account(name, cookie)


if __name__ == "__main__":
    asyncio.run(main())
