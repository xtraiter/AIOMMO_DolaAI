"""Video generation worker v2: UI automation with OpenCV slider puzzle solver."""
import asyncio
import json
import random
import re
import sys
import time
from pathlib import Path

import aiohttp
from patchright.async_api import async_playwright

from gap import find_gap_x

import config
from browser import cookie_value, launch_account_context
from dola_client import CREDIT_FAIL_PATTERN, CreditError
from dola_errors import AccountUnhealthyError, DolaAskedBackError, LoginRequiredError  # noqa: F401  (re-exported for browser_pool)
from video_worker import POLL_JS, RiskControlError, _download, extract_unwatermarked_url
from warmup import open_new_chat, warmup_chat

# Daily limit pattern matching response text
DAILY_LIMIT_PATTERN = re.compile(
    r"動画生成の\s*1日あたりの上限|每日(?:视频|影片)?生成.*(?:上限|限额|额度)|"
    r"daily.*(?:limit|quota)|(?:limit|quota).*per\s*day",
    re.IGNORECASE,
)


# Dola's chat agent sometimes replies with a question instead of making the video ("may I use 15s?", "do you have a face
# image? A / B"). It stays that way forever, so notice it instead of waiting for the full video timeout.
QUESTION_MARK = re.compile(r"[?\uFF1F]")
ASKED_BACK_GRACE_SEC = 60        # report the question as an error after this long (no auto reply configured)
AUTO_REPLY_GRACE_SEC = 12        # answer Dola's question this soon once it has stopped writing
MAX_AUTO_REPLIES = 3


def _is_dola_question(text: str, prompt: str) -> bool:
    t = (text or "").strip()
    if len(t) < 12 or not QUESTION_MARK.search(t):
        return False
    head = (prompt or "").strip()[:30]
    # our own prompt echoed back in the conversation (it may contain question marks too) is not a question from Dola
    if head and (t.startswith(head) or head.startswith(t[:30])):
        return False
    return True


def _dola_note(texts, prompt: str) -> str:
    """What Dola's chat agent wrote in the conversation besides our own prompt (e.g. "the video came out at 10s instead of 15s")."""
    head = (prompt or "").strip()[:30]
    seen, out = set(), []
    for t in texts or []:
        t = (t or "").strip()
        if not t or t in seen:
            continue
        if head and (t.startswith(head) or head.startswith(t[:30])):
            continue  # our own prompt echoed back
        seen.add(t)
        out.append(t)
    return " | ".join(out)[:600]


async def send_chat_message(page, text: str) -> None:
    """Answer Dola in the open conversation: insert the text (one line) into the chat box and press Enter."""
    box = await page.query_selector("textarea") or await page.query_selector('[contenteditable="true"]')
    if not box:
        raise RuntimeError("Could not find the chat box to answer Dola")
    try:
        await box.click(force=True, timeout=5000)
    except Exception:
        await box.focus()
    await page.keyboard.insert_text(" ".join(text.split()))
    await page.wait_for_timeout(500)
    await page.keyboard.press("Enter")


class AccountLimitedError(Exception):
    """Account reached daily video generation limit."""


class CreditInsufficientError(Exception):
    """Insufficient points prior to generation."""


VIDEO_BTN = "text=動画を作成"          # Entry point button in ja-JP locale
CAPTCHA_FRAME_KEY = "bdcaptcha.html"   # Captcha verifycenter iframe


# Read-only balance pre-check from recent conversations
BALANCE_JS = r"""
async ({msToken, fp}) => {
  const params = new URLSearchParams({
    version_code: "20800", language: "ja", device_platform: "web",
    doubao_device_platform: "web", aid: "495671", real_aid: "495671",
    pkg_type: "release_version", pc_version: "3.32.62", doubao_pc_version: "3.32.62",
    region: "JP", sys_region: "JP", samantha_web: "1", web_platform: "browser",
    "use-olympus-account": "1", web_tab_id: crypto.randomUUID(),
  });
  if (msToken) params.set("msToken", msToken);
  if (fp) params.set("fp", fp);
  const headers = {
    "Content-Type": "application/json; encoding=utf-8",
    "agw-js-conv": "str", "Accept": "*/*",
  };
  const recent = await fetch("/im/chain/recent_conv?" + params.toString(), {
    method: "POST", headers,
    body: JSON.stringify({
      cmd: 3200,
      uplink_body: {pull_recent_conv_chain_uplink_body: {
        limit: 20, message_count_per_conv: 10, api_version: 1, conv_version: 0,
        direction: 3,
        option: {not_need_message: false, need_complete_conversation: true,
          need_coco_conversation: true, need_coco_bot: true,
          need_pc_pin_chain: true, pc_pin_query_type: 0},
      }},
      sequence_id: crypto.randomUUID(), channel: 2, version: "1",
    }), credentials: "include",
  });
  if (!recent.ok) return {ok: false, texts: []};
  const recentData = await recent.json();
  const body = recentData.downlink_body || {};
  const down = body.pull_recent_conv_chain_downlink_body || {};
  const cells = down.cells || [];
  const ids = cells.map(c => (c.conversation || {}).conversation_id || c.id)
    .filter(Boolean).slice(0, 10);
  if (!ids.length) return {ok: true, texts: []};

  const batch = await fetch("/im/chain/batch_single?" + params.toString(), {
    method: "POST", headers,
    body: JSON.stringify({
      cmd: 3101,
      uplink_body: {batch_pull_singe_chain_uplink_body: {
        conversation_type: 3, direction: 3, limit: 1,
        params: ids.map(conversation_id => ({conversation_id})),
        evaluate_ab_params: "", evaluate_common_params: "", ext: {},
      }},
      sequence_id: crypto.randomUUID(), channel: 2, version: "1",
    }), credentials: "include",
  });
  if (!batch.ok) return {ok: true, texts: []};
  const data = await batch.json();
  const texts = [];
  const seen = new Set();
  const walk = (v) => {
    if (typeof v === "string") {
      if ((v.includes("ポイント") || v.includes("积分") || v.toLowerCase().includes("points")
        || v.includes("上限") || v.includes("limit")) && v.length < 1200 && !seen.has(v)) {
        seen.add(v); texts.push(v);
      }
      try {
        const t = v.trim();
        if (t.startsWith("{") || t.startsWith("[")) walk(JSON.parse(t));
      } catch (e) {}
      return;
    }
    if (!v || typeof v !== "object") return;
    if (Array.isArray(v)) { for (const x of v) walk(x); return; }
    for (const x of Object.values(v)) walk(x);
  };
  walk(data);
  return {ok: true, texts: texts.slice(-80)};
}
""";


def find_captcha_frame(page):
    for f in page.frames:
        if CAPTCHA_FRAME_KEY in f.url:
            return f
    return None


async def _fetch_bytes(url: str) -> bytes:
    async with aiohttp.ClientSession() as s:
        async with s.get(url, proxy=config.PROXY or None) as r:
            return await r.read()



async def attach_reference_images(page, image_paths: list[str]) -> None:
    """Uploads reference images through native file input and waits for TOS upload."""
    if not image_paths:
        return
    file_input = page.locator('input[type="file"]').first
    await file_input.wait_for(state="attached", timeout=10000)
    events = []

    def on_response(response):
        url = response.url
        if "/alice/resource/prepare_upload" in url or "/upload/v1/" in url:
            events.append((response.status, url))

    page.on("response", on_response)
    try:
        await file_input.set_input_files(image_paths)
        expected = len(image_paths)
        deadline = time.time() + max(60, expected * 20)
        while time.time() < deadline:
            prepare_count = sum("/alice/resource/prepare_upload" in url and 200 <= status < 300
                                for status, url in events)
            tos_count = sum("/upload/v1/" in url and 200 <= status < 300
                            for status, url in events)
            # Wait for thumbnails and TOS completion before sending
            thumb_count = await page.locator('img[alt]').count()
            if prepare_count >= expected and tos_count >= expected and thumb_count >= expected:
                await page.wait_for_timeout(800)
                print(f"[upload] Reference images uploaded: {expected} image(s)", flush=True)
                return
            await page.wait_for_timeout(250)
        raise TimeoutError(
            f"Reference image upload timeout: prepare={prepare_count}/{expected}, tos={tos_count}/{expected}"
        )
    finally:
        page.remove_listener("response", on_response)


def _gen_track(distance: float):
    """Generates humanized mouse drag trajectory."""
    steps = random.randint(45, 65)
    overshoot = random.uniform(3, 9)
    pts = []
    for i in range(1, steps + 1):
        t = i / steps
        s = 10 * t**3 - 15 * t**4 + 6 * t**5
        x = (distance + overshoot) * s
        y = random.uniform(-1.5, 1.5) if 0.1 < t < 0.95 else 0
        pts.append((x, y, random.randint(8, 22)))
    for i in range(1, random.randint(3, 5) + 1):
        pts.append((distance + overshoot * (1 - i / 5), random.uniform(-0.8, 0.8), random.randint(15, 30)))
    return pts


async def solve_slider(page, frame, attempt: int) -> bool:
    """Solves captcha slider notch within iframe and performs drag."""
    await frame.wait_for_selector("img", timeout=15000)
    await frame.evaluate("""async () => {
        const t0 = Date.now();
        while (Date.now() - t0 < 10000) {
            const imgs = [...document.images];
            if (imgs.length >= 2 && imgs.every(im => im.complete && im.naturalWidth > 0)) return;
            await new Promise(r => setTimeout(r, 200));
        }
        throw new Error("captcha images load timeout");
    }""")
    await page.wait_for_timeout(800)

    imgs = await frame.evaluate("""() => [...document.images].map(im => ({
        src: im.src, w: im.naturalWidth, h: im.naturalHeight,
        bw: im.getBoundingClientRect().width,
        left: im.getBoundingClientRect().left,
    }))""")
    bg = next((i for i in imgs if ".jpeg" in i["src"] or "-2." in i["src"]), None)
    piece = next((i for i in imgs if i is not bg and (".png" in i["src"] or "-1." in i["src"])), None)
    if not bg or not piece:
        print("  ✗ Captcha background or puzzle image not found", flush=True)
        return False

    bg_bytes = await _fetch_bytes(bg["src"])
    piece_bytes = await _fetch_bytes(piece["src"])
    Path("dbg_bg.jpg").write_bytes(bg_bytes)
    Path("dbg_piece.png").write_bytes(piece_bytes)

    gap_x, conf = find_gap_x(bg_bytes, piece_bytes)
    scale = bg["bw"] / bg["w"] if bg["w"] else 340 / 552
    distance = (gap_x - (piece["left"] - bg["left"]) / scale) * scale
    print(f"  [solve#{attempt}] gap_x={gap_x} conf={conf:.3f} scale={scale:.2f} distance={distance:.0f}px", flush=True)

    btn = frame.locator(".captcha-slider-btn")
    bb = await btn.bounding_box()
    if not bb:
        print("  ✗ Drag handle .captcha-slider-btn not found", flush=True)
        return False
    sx, sy = bb["x"] + bb["width"] / 2, bb["y"] + bb["height"] / 2
    await page.mouse.move(sx, sy)
    await page.wait_for_timeout(random.randint(150, 350))
    await page.mouse.down()
    await page.wait_for_timeout(random.randint(80, 180))
    for dx, dy, dt in _gen_track(distance):
        await page.mouse.move(sx + dx, sy + dy)
        await asyncio.sleep(dt / 1000)
    await page.wait_for_timeout(random.randint(120, 260))
    await page.mouse.up()

    for _ in range(10):
        await page.wait_for_timeout(700)
        if not find_captcha_frame(page):
            return True
    return False



async def wait_and_solve_captcha(page, account: str, wait_sec: int = 20) -> None:
    """Wait for the slider captcha after a submit and solve it (up to 3 attempts). Returns when absent or solved.

    Raises RiskControlError when it cannot be passed.
    """
    for attempt in range(1, 4):
        frame = None
        for _ in range(wait_sec):
            await page.wait_for_timeout(1000)
            frame = find_captcha_frame(page)
            if frame:
                break
        if not frame:
            return
        print(f"[{account}] Captcha detected, attempt {attempt} solving...", flush=True)
        if await solve_slider(page, frame, attempt):
            print(f"[{account}] Captcha passed ✓", flush=True)
            await page.wait_for_timeout(3000)  # Wait for frontend auto-retry
            return
        print(f"[{account}] Captcha not passed, retrying...", flush=True)
    await page.screenshot(path="solve_fail.png")
    raise RiskControlError("Captcha failed 3 times")


_BALANCE_PATTERNS = (
    re.compile(r"(?:本日は|今日(?:还剩|剩余)?|今天).*?(\d+)\s*(?:ポイント|积分|points?)", re.I),
    re.compile(r"(?:remaining|left)\s*[:：]?\s*(\d+)\s*points?", re.I),
    re.compile(r"(?:还剩|剩余|还有)\s*(\d+)\s*(?:积分|点)", re.I),
)


def _parse_balance_texts(texts: list[str]) -> tuple[int | None, bool, str]:
    for text in texts:
        if DAILY_LIMIT_PATTERN.search(text):
            return None, True, text
    for text in texts:
        for pattern in _BALANCE_PATTERNS:
            match = pattern.search(text)
            if match:
                return int(match.group(1)), False, text
    return None, False, ""


async def _preflight_balance(page, ms_token: str, fp: str, required: int) -> dict:
    """Reads known credit balance from chat history."""
    try:
        result = await asyncio.wait_for(page.evaluate(
            BALANCE_JS, {"msToken": ms_token, "fp": fp}), timeout=30)
        balance, daily_limited, source = _parse_balance_texts(result.get("texts", []))
        if daily_limited:
            raise AccountLimitedError(f"Account daily generation limit: {source[:120]}")
        if balance is not None and balance < required:
            raise CreditInsufficientError(
                f"Insufficient points: current {balance}, required {required} (source: {source[:120]})"
            )
        return {"balance": balance, "source": source}
    except (AccountLimitedError, CreditInsufficientError):
        raise
    except Exception as e:
        print(f"  Balance pre-check indeterminate (proceeding with submit): {str(e)[:120]}", flush=True)
        return {"balance": None, "source": ""}


async def poll_conversation(account: str, page, context, conversation_id: str,
                            timeout: int, on_poll=None, on_balance=None, prompt: str = "",
                            auto_reply: str | None = None) -> dict:
    """Polls accepted conversation for video completion."""
    cookies = await context.cookies("https://www.dola.com")
    ms_token, fp = cookie_value(cookies, "msToken"), cookie_value(cookies, "s_v_web_id")
    start = time.time()
    last_callback = 0.0
    question_since = None
    question_text = ""
    answered: set = set()   # questions we already answered (they stay in the conversation history)
    replies = 0
    grace = AUTO_REPLY_GRACE_SEC if auto_reply else ASKED_BACK_GRACE_SEC
    while time.time() - start < timeout:
        await asyncio.sleep(5)
        try:
            poll = await asyncio.wait_for(page.evaluate(
                POLL_JS, {"conversationId": conversation_id, "msToken": ms_token, "fp": fp}), timeout=30)
        except Exception as e:
            print(f"  Polling exception: {e}", flush=True)
            continue
        now = time.time()
        if on_poll and now - last_callback >= 30:
            on_poll(now)
            last_callback = now
        for text in poll.get("texts", []):
            balance, _, source = _parse_balance_texts([text])
            if balance is not None and on_balance:
                on_balance(balance, source)
            if DAILY_LIMIT_PATTERN.search(text):
                raise AccountLimitedError(f"Account daily limit reached: {text[:120]}")
            if CREDIT_FAIL_PATTERN.search(text):
                raise CreditError(f"Insufficient quota: {text[:80]}")
        questions = [t for t in poll.get("texts", []) if _is_dola_question(t, prompt) and t not in answered]
        if questions and not poll.get("videos"):
            if question_since is None:
                question_since, question_text = now, questions[-1]
            elif now - question_since >= grace:
                if auto_reply and replies < MAX_AUTO_REPLIES:
                    # Dola wants a confirmation ("use 15s instead?", "do you have a face image?"): give it, once per question
                    print(f"[{account}] Dola asked: {question_text[:150]!r} -> auto reply #{replies + 1}", flush=True)
                    answered.update(questions)
                    await send_chat_message(page, auto_reply)
                    replies += 1
                    question_since = None
                    continue
                raise DolaAskedBackError(question_text.strip())
        else:
            question_since = None
        if poll.get("videos"):
            video_models = poll.get("videoModels", [])
            url = extract_unwatermarked_url(
                video_models[0] if video_models else "", poll["videos"][0])
            print(f"[{account}] Completed! Downloading (unwatermarked priority)...", flush=True)
            local = await _download(url, account)
            print(f"[{account}] Downloaded {local} ({local.stat().st_size / 1e6:.1f} MB)", flush=True)
            return {"video_url": url, "local_path": str(local),
                    "conversation_id": conversation_id, "account": account,
                    "note": ((f"[App tự trả lời Dola {replies} lần] " if replies else "") + _dola_note(poll.get("texts", []), prompt))[:700]}
        print(f"  ...Generating ({int(time.time() - start)}s)", flush=True)
    raise TimeoutError(f"No video generated within {timeout}s (conversation_id={conversation_id})")


async def resume_video(account: str, conversation_id: str, timeout: int,
                       on_poll=None, on_balance=None) -> dict:
    """Recovers accepted session after server restart without re-sending prompt."""
    async with async_playwright() as p:
        context = await launch_account_context(p, account, headless=False, use_extension=True)
        try:
            page = context.pages[0] if context.pages else await context.new_page()
            await page.goto(f"https://www.dola.com/chat/{conversation_id}",
                            timeout=60000, wait_until="domcontentloaded")
            await page.wait_for_timeout(5000)
            return await poll_conversation(account, page, context, conversation_id, timeout, on_poll, on_balance)
        finally:
            await context.close()


async def type_prompt(page, prompt: str) -> None:
    """Type a prompt into Dola's chat box WITHOUT sending it.

    In the chat box Enter submits the message, so a multi-line prompt must use Shift+Enter for every line break
    (typing "\n" would press Enter and send the first line on its own). Tabs are turned into spaces because a Tab key
    press moves the focus out of the box. Short single-line prompts keep the original slow, human-like typing;
    long / multi-line ones are inserted line by line (typing 2,000 characters at 100 ms each would take minutes).
    """
    text = prompt.replace("\r\n", "\n").replace("\r", "\n").replace("\t", "    ")
    lines = text.split("\n")
    slow = len(lines) == 1 and len(text) <= 300
    for i, line in enumerate(lines):
        if i > 0:
            await page.keyboard.press("Shift+Enter")
        if not line:
            continue
        if slow:
            await page.keyboard.type(line, delay=100)
        else:
            await page.keyboard.insert_text(line)
            await page.wait_for_timeout(60)


async def generate_video(account: str, prompt: str, ratio: str = None,
                         duration: int = None, timeout: int = None,
                         model: str = "seedance_v2.0", use_extension: bool = True,
                         on_conversation_id=None, on_poll=None, on_balance=None,
                         reference_image_paths: list[str] | None = None,
                         on_stage=None, warmup: bool | None = None, on_warmup_done=None,
                         hide_window: bool = False, auto_reply: str | None = None) -> dict:
    """Full generation flow via UI automation.

    Stages reported through on_stage: warmup -> new_chat -> submitting -> generating (-> done by the caller).

    The greeting chat runs once per account per day: the pool passes warmup=True only when it is due and gets
    on_warmup_done() after Dola answered. warmup=None (direct calls) falls back to config.WARMUP_ENABLED.
    """
    def stage(name: str):
        if on_stage:
            try:
                on_stage(name)
            except Exception:  # a broken progress callback must never abort the generation
                pass

    timeout = timeout or config.VIDEO_TIMEOUT
    model_key = model.lower().replace("-", "_")
    if model_key in ("seedance_2.5", "seedance_v2.5", "seedance_25", "seedance_v25"):
        model_key = "seedance_v2.5"
    elif model_key in ("seedance_2.0", "seedance_v2.0", "seedance_20", "seedance_v20"):
        model_key = "seedance_v2.0"
    else:
        raise ValueError(f"Unsupported model: {model} (supported: seedance-2.0 / seedance-2.5)")
    if duration is not None and duration not in (10, 15, 30):
        raise ValueError("Dola supports durations of 10s, 15s, and 30s via extension")
    if duration == 30 and not use_extension:
        raise ValueError("30s generation requires Dola30 extension enabled")
    # 30s videos require extended generation timeout
    if duration == 30:
        timeout = max(timeout, 1800)
    if reference_image_paths:
        timeout = max(timeout, config.REFERENCE_VIDEO_TIMEOUT)
    async with async_playwright() as p:
        context = await launch_account_context(
            p, account, headless=False if use_extension else None,
            use_extension=use_extension, hide_window=hide_window)
        try:
            page = context.pages[0] if context.pages else await context.new_page()
            await page.goto("https://www.dola.com/chat", timeout=60000, wait_until="domcontentloaded")
            await page.wait_for_timeout(4000)

            # Check authentication cookies
            cookies = await context.cookies("https://www.dola.com")
            sid = cookie_value(cookies, "sessionid") or cookie_value(cookies, "sessionid_ss")
            if not sid:
                raise LoginRequiredError(f"Tài khoản '{account}' chưa đăng nhập Dola (thiếu cookie sessionid hoặc phiên đăng nhập đã hết hạn). Vui lòng đăng nhập lại profile!")

            # Check if login button is visible in header bar (meaning cookie expired)
            for login_btn_sel in ('button:has-text("ログイン")', 'button:has-text("Log in")', 'button:has-text("登录")', 'a:has-text("ログイン")', 'a:has-text("Log in")'):
                btn = page.locator(login_btn_sel).first
                if await btn.count() and await btn.is_visible():
                    raise LoginRequiredError(f"Tài khoản '{account}' có phiên đăng nhập đã hết hạn trên Dola (xuất hiện nút Đăng nhập). Vui lòng đăng nhập lại profile!")

            # Dismiss any popup overlay or announcement dialog if present
            try:
                for close_sel in ('[data-slot="dialog-close"]', 'button[aria-label="Close"]', 'button[aria-label*="close" i]'):
                    btn = page.locator(close_sel).first
                    if await btn.count() and await btn.is_visible():
                        await btn.click(timeout=1500)
            except Exception:
                pass

            ms_token, fp = cookie_value(cookies, "msToken"), cookie_value(cookies, "s_v_web_id")
            await _preflight_balance(page, ms_token, fp, config.VIDEO_REQUIRED_POINTS)

            # ---- Pre-flight greeting chat, then a brand-new chat for the real request ----
            if config.WARMUP_ENABLED and warmup is not False:
                await warmup_chat(
                    page, context, account,
                    solve_captcha=lambda pg, acc: wait_and_solve_captcha(pg, acc, wait_sec=6),
                    cookie_value=cookie_value, on_stage=stage)
                if on_warmup_done:
                    on_warmup_done()  # only after a real answer: a failed greeting is retried on the next task
                await open_new_chat(page, on_stage=stage)
            elif config.WARMUP_ENABLED:
                print(f"[{account}] Greeting chat already done today - skipping", flush=True)

            # ---- UI Submission ----
            stage("submitting")
            # Video creation button (supports Japanese, English, Chinese)
            video_btn = None
            for sel in ("text=動画を作成", "text=Create video", "text=Generate video", "text=创建视频"):
                loc = page.locator(sel).first
                if await loc.count() and await loc.is_visible():
                    video_btn = loc
                    break
            if video_btn:
                await video_btn.click(timeout=10000)
            else:
                await page.click(VIDEO_BTN, timeout=10000)
            await page.wait_for_timeout(1500)

            # Check if login modal appeared after clicking create video
            login_prompts = ("text=他の機能を利用するにはログインしてください", "text=Please log in to use other features", "text=请先登录")
            for prompt_sel in login_prompts:
                if await page.locator(prompt_sel).count() > 0:
                    raise LoginRequiredError(f"Tài khoản '{account}' chưa đăng nhập Dola (hệ thống yêu cầu đăng nhập khi tạo video). Vui lòng đăng nhập lại profile!")

            if reference_image_paths:
                await attach_reference_images(page, reference_image_paths)

            # Select model in UI (Seedance 2.0 / 2.5)
            try:
                need_switch = True
                current_model = None
                for label in ("モデル 2.0高速", "モデル 2.5", "Model 2.0 Fast", "Model 2.5", "Seedance 2.0", "Seedance 2.5"):
                    loc = page.get_by_text(label, exact=True).first
                    if await loc.count() and await loc.is_visible():
                        current_model = loc
                        txt = await loc.inner_text()
                        if (model_key == "seedance_v2.0" and ("2.0" in txt)) or (model_key == "seedance_v2.5" and ("2.5" in txt)):
                            need_switch = False
                        break

                if need_switch:
                    if current_model is None:
                        current_model = page.get_by_text(re.compile(r"^(モデル|Model)\s*", re.I), exact=False).first
                    if await current_model.count() and await current_model.is_visible():
                        await current_model.click(timeout=5000)
                        await page.wait_for_timeout(500)
                        options = (("Dreamina Seedance 2.5",)
                                   if model_key == "seedance_v2.5"
                                   else ("Dreamina Seedance 2.0高速", "Dreamina Seedance 2.0", "Seedance2.0Fast", "Seedance 2.0 Fast"))
                        selected = False
                        for option_text in options:
                            loc = page.get_by_text(option_text, exact=False).first
                            if await loc.count() and await loc.is_visible():
                                await loc.click(timeout=5000)
                                selected = True
                                break
                        if not selected:
                            print(f"[{account}] Warning: Target model option not found in dropdown, using current", flush=True)
                        await page.wait_for_timeout(500)
            except Exception as e:
                print(f"[{account}] Note on model selector: {e}, continuing with active model", flush=True)
            if ratio:
                try:
                    await page.click("text=比率", timeout=3000)
                    await page.wait_for_timeout(500)
                    await page.click(f"text={ratio}", timeout=3000)
                except Exception as e:
                    print(f"  (Failed to set ratio, using default: {str(e)[:80]})", flush=True)
            if duration:
                try:
                    await page.click(f"text={duration}s", timeout=3000)
                except Exception:
                    try:  # Open duration dropdown
                        await page.get_by_text(re.compile(r"^\d+s$")).first.click(timeout=3000)
                        await page.wait_for_timeout(500)
                        await page.click(f"text={duration}s", timeout=3000)
                    except Exception as e:
                        print(f"  (Failed to set duration, using default: {str(e)[:80]})", flush=True)
            # Dismiss any leftover open dropdown menus by pressing Escape
            try:
                await page.keyboard.press("Escape")
                await page.wait_for_timeout(300)
            except Exception:
                pass

            box = await page.query_selector("textarea") or await page.query_selector('[contenteditable="true"]')
            if not box:
                raise RuntimeError("Could not find prompt textarea or contenteditable box on Dola page")
            try:
                await box.click(force=True, timeout=5000)
            except Exception:
                await box.focus()
            await type_prompt(page, prompt)
            await page.wait_for_timeout(600)
            if config.DRY_RUN:
                # Test mode: settings are applied and the prompt is typed; nothing is sent, so no credit is spent
                print(f"[{account}] [dry-run] prompt typed, NOT sending: {prompt[:40]}", flush=True)
                await page.screenshot(path="dry_run.png")
                return {"video_url": "", "local_path": "", "conversation_id": "", "account": account, "dry_run": True}
            await page.keyboard.press("Enter")
            print(f"[{account}] UI submitted prompt: {prompt[:40]}", flush=True)

            # ---- Captcha Solver (up to 3 attempts) ----
            await wait_and_solve_captcha(page, account, wait_sec=20)

            # ---- Wait for real conversation_id ----
            conv_id = ""
            for _ in range(30):
                await page.wait_for_timeout(1000)
                tail = page.url.rstrip("/").split("/")[-1]
                if tail.isdigit():
                    conv_id = tail
                    break
            if not conv_id:
                await page.screenshot(path="no_conv.png")
                raise TimeoutError("conversation_id not acquired within 30s")
            print(f"[{account}] conversation_id={conv_id}, polling for video...", flush=True)
            stage("generating")

            deadline = time.time() + timeout
            if on_conversation_id:
                on_conversation_id(account, conv_id, deadline)
            return await poll_conversation(account, page, context, conv_id, timeout, on_poll, on_balance, prompt=prompt,
                                           auto_reply=auto_reply)
        finally:
            await context.close()


async def _main():
    account = sys.argv[1] if len(sys.argv) > 1 else "acc1"
    prompt = sys.argv[2] if len(sys.argv) > 2 else "An orange cat napping on a sunny windowsill"
    ratio = sys.argv[3] if len(sys.argv) > 3 else None
    duration = int(sys.argv[4]) if len(sys.argv) > 4 else None
    model = sys.argv[5] if len(sys.argv) > 5 else "seedance_v2.0"
    result = await generate_video(account, prompt, ratio, duration, model=model)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        asyncio.run(_main())
    except Exception:
        import traceback
        traceback.print_exc()