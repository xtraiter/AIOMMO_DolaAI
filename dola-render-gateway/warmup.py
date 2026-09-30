"""Pre-flight greeting chat and "open a fresh chat" helper for the Dola UI worker.

Why: a session cookie can be present and still be useless (risk control, throttled chat, half-expired login).
Sending one cheap random question and seeing Dola answer proves the account works BEFORE any video credit is spent.
Only then does the worker open a brand-new chat and submit the real video request.
"""
import asyncio
import random
import time
from pathlib import Path

import config
from dola_errors import AccountUnhealthyError, LoginRequiredError
from video_worker import POLL_JS

# Short, harmless, varied questions (English / Japanese / Vietnamese). Override with warmup_questions.txt.
DEFAULT_QUESTIONS = [
    "What is the capital of Japan?",
    "Give me a fun fact about cats.",
    "Can you suggest a good name for a pet goldfish?",
    "How many days are there in a leap year?",
    "Recommend a relaxing weekend activity.",
    "What is a healthy breakfast idea?",
    "Explain photosynthesis in one sentence.",
    "Tell me a short, clean joke.",
    "What is 12 times 8?",
    "Suggest three words that rhyme with light.",
    "What is the tallest mountain in the world?",
    "How do I boil an egg perfectly?",
    "こんにちは！今日はいい天気ですね。",
    "おすすめの朝ごはんを教えてください。",
    "日本で一番長い川はどれですか？",
    "簡単なストレッチを一つ教えてください。",
    "Xin chào, hôm nay bạn thế nào?",
    "Gợi ý cho mình một món ăn nhẹ nhé.",
    "Thủ đô của Việt Nam là gì?",
    "Cho mình một mẹo học từ vựng hiệu quả.",
]

LOGIN_PROMPTS = ("text=他の機能を利用するにはログインしてください", "text=Please log in to use other features", "text=请先登录")


def pick_question() -> str:
    """One random greeting question: from warmup_questions.txt if present, otherwise the built-in list."""
    path = Path(config.WARMUP_QUESTIONS_FILE)
    if path.exists():
        try:
            lines = [ln.strip() for ln in path.read_text(encoding="utf-8-sig").splitlines()
                     if ln.strip() and not ln.lstrip().startswith("#")]
            if lines:
                return random.choice(lines)
        except OSError:
            pass
    return random.choice(DEFAULT_QUESTIONS)


async def find_prompt_box(page):
    """The chat input (textarea or contenteditable), same lookup the video submission uses."""
    return await page.query_selector("textarea") or await page.query_selector('[contenteditable="true"]')


async def _type_and_send(page, text: str):
    box = await find_prompt_box(page)
    if not box:
        raise AccountUnhealthyError("Không tìm thấy ô nhập chat trên Dola.")
    try:
        await box.click(force=True, timeout=5000)
    except Exception:
        await box.focus()
    await page.keyboard.type(text, delay=random.randint(40, 90))
    await page.wait_for_timeout(500)
    await page.keyboard.press("Enter")


def _conversation_id(page) -> str:
    tail = page.url.rstrip("/").split("/")[-1]
    return tail if tail.isdigit() else ""


async def warmup_chat(page, context, account: str, solve_captcha, cookie_value, on_stage=None) -> str:
    """Send one random question and wait for Dola's answer. Returns the question that was used.

    Raises LoginRequiredError (login prompt appeared) or AccountUnhealthyError (no conversation / no answer in time).
    RiskControlError from `solve_captcha` propagates so the pool can cool the account down.
    """
    question = pick_question()
    if on_stage:
        on_stage("warmup")
    print(f"[{account}] Warm-up chat: {question[:50]}", flush=True)

    body_len_before = len(await page.evaluate("document.body.innerText"))
    await _type_and_send(page, question)

    for sel in LOGIN_PROMPTS:
        if await page.locator(sel).count() > 0:
            raise LoginRequiredError(
                f"Tài khoản '{account}' chưa đăng nhập Dola (hệ thống yêu cầu đăng nhập khi chat). Hãy đăng nhập lại profile.")

    # Slider captcha may appear on the very first message of a session
    await solve_captcha(page, account)

    conv_id = ""
    for _ in range(25):
        await page.wait_for_timeout(1000)
        conv_id = _conversation_id(page)
        if conv_id:
            break
    if not conv_id:
        raise AccountUnhealthyError("Chat hỏi thăm không tạo được cuộc trò chuyện (Dola không phản hồi).")

    cookies = await context.cookies("https://www.dola.com")
    ms_token, fp = cookie_value(cookies, "msToken"), cookie_value(cookies, "s_v_web_id")
    own = question[:120].strip()

    deadline = time.time() + config.WARMUP_TIMEOUT
    poll_failures = 0
    while time.time() < deadline:
        await asyncio.sleep(3)
        try:
            poll = await asyncio.wait_for(page.evaluate(
                POLL_JS, {"conversationId": conv_id, "msToken": ms_token, "fp": fp}), timeout=30)
        except Exception as e:
            print(f"[{account}] Warm-up poll exception: {str(e)[:80]}", flush=True)
            poll = {"ok": False}

        if poll.get("ok"):
            replies = [t.strip() for t in poll.get("texts", [])
                       if t.strip() and t.strip() != own and not own.startswith(t.strip())]
            if replies:
                print(f"[{account}] Warm-up OK — Dola answered: {replies[-1][:60]}", flush=True)
                return question
        else:
            poll_failures += 1
            # The history endpoint is unavailable: fall back to "the page grew and settled" as proof of an answer
            if poll_failures >= 3:
                size = len(await page.evaluate("document.body.innerText"))
                if size > body_len_before + 80:
                    await page.wait_for_timeout(4000)
                    if len(await page.evaluate("document.body.innerText")) >= size:
                        print(f"[{account}] Warm-up OK (page content grew)", flush=True)
                        return question

    raise AccountUnhealthyError(f"Dola không trả lời chat hỏi thăm trong {config.WARMUP_TIMEOUT}s.")


async def open_new_chat(page, on_stage=None) -> None:
    """Start a fresh conversation (so the video request is not mixed into the greeting chat)."""
    if on_stage:
        on_stage("new_chat")

    clicked = False
    for sel in ("text=新しいチャット", "text=New Chat", "text=新对话", "text=新建对话"):
        loc = page.locator(sel).first
        try:
            if await loc.count() and await loc.is_visible():
                await loc.click(timeout=4000)
                clicked = True
                break
        except Exception:
            continue

    await page.wait_for_timeout(1500)
    if not clicked or _conversation_id(page):
        # Sidebar entry not found (or still on the old conversation): a plain /chat URL is always a blank chat
        await page.goto("https://www.dola.com/chat", timeout=60000, wait_until="domcontentloaded")
        await page.wait_for_timeout(2500)

    # Close announcement dialogs that may pop up on a fresh load
    for close_sel in ('[data-slot="dialog-close"]', 'button[aria-label="Close"]', 'button[aria-label*="close" i]'):
        try:
            btn = page.locator(close_sel).first
            if await btn.count() and await btn.is_visible():
                await btn.click(timeout=1500)
        except Exception:
            pass
    print("New chat opened.", flush=True)
