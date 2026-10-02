"""[AIOMMO] Everything AIOMMO DolaAI (DolaCoordinator) needs on top of stock dola-pool, kept in ONE module.

The stock dola-pool files stay as close to upstream as possible; each place that calls into this module is marked
`[AIOMMO]`, so pulling a newer dola-pool is a matter of re-applying those few marked lines.

What lives here
  RunOptions / current()   per-task options carried in a ContextVar (set by server._run_task, read deep in the worker):
                           pinned account, off-screen window, auto reply, prompt cleaning, stage callback, Dola's note
  type_prompt              multi-line prompts: Shift+Enter for line breaks (plain typing would press Enter and send)
  ask-back handling        Dola sometimes answers with a question instead of making the video
  materialize_local_paths  reference images picked on this machine -> refs/ (loopback callers only)
  classify_failure         machine readable failure code for the coordinator
"""
from __future__ import annotations

import contextvars
import dataclasses
import re
import shutil
import uuid
from pathlib import Path
from typing import Callable

import config
from prompt_clean import strip_duration_words


# ------------------------------------------------------------------ per-task options

@dataclasses.dataclass
class RunOptions:
    account: str | None = None              # pin the task to this account (the coordinator schedules accounts itself)
    hide_window: bool = False               # render in a Chromium window placed far off-screen
    auto_reply: str | None = None           # what to answer when Dola asks something that is not about the duration
    strip_duration_words: bool | None = None  # None = gateway default (config.STRIP_DURATION_WORDS)
    on_stage: Callable[[str], None] | None = None
    note: str = ""                          # output: what Dola wrote / what we answered (shown in the coordinator's log)

    def stage(self, name: str) -> None:
        if self.on_stage:
            try:
                self.on_stage(name)
            except Exception:  # a broken progress callback must never abort a generation
                pass


_current: contextvars.ContextVar[RunOptions | None] = contextvars.ContextVar("aiommo_run_options", default=None)


def current() -> RunOptions:
    return _current.get() or RunOptions()


def set_current(opts: RunOptions) -> contextvars.Token:
    return _current.set(opts)


# Tasks the user cancelled through DELETE /v1/videos/{id} (a plain CancelledError means "service restarting" upstream).
USER_CANCELLED: set[str] = set()


# ------------------------------------------------------------------ prompt typing

def clean_prompt(prompt: str, opts: RunOptions | None = None) -> tuple[str, str]:
    """(prompt to type, note). Drops duration words ("30s", "00:00 - 00:03", "Giây 0 đến 3"): the Dola30 extension's README
    asks not to put them in the prompt, otherwise Dola's agent asks back instead of generating."""
    opts = opts or current()
    enabled = opts.strip_duration_words if opts.strip_duration_words is not None else config.STRIP_DURATION_WORDS
    if not enabled:
        return prompt, ""
    cleaned, removed = strip_duration_words(prompt)
    if removed:
        return cleaned, f"[Đã bỏ {removed} chỗ ghi thời lượng khỏi prompt] "
    return prompt, ""


async def type_prompt(page, prompt: str) -> None:
    """Type a prompt into Dola's chat box WITHOUT sending it.

    In the chat box Enter submits the message, so a multi-line prompt must use Shift+Enter for every line break
    (typing "\\n" would press Enter and send the first line on its own). Tabs become spaces because a Tab key press
    moves the focus out of the box. Short single-line prompts keep the slow human-like typing; long / multi-line ones are
    inserted line by line (typing 2,000 characters at 100 ms each would take minutes).
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


# ------------------------------------------------------------------ Dola asks back

class AskedBackError(RuntimeError):
    """Dola answered with a question instead of making the video and it could not be (or was not allowed to be) answered."""


QUESTION_MARK = re.compile(r"[?？]")
ASKED_BACK_GRACE_SEC = 60        # report the question as an error after this long (no auto reply configured)
AUTO_REPLY_GRACE_SEC = 12        # answer Dola's question this soon once it has stopped writing
MAX_AUTO_REPLIES = 3

# Duration offers ("A. 5 秒で生成 B. 10 秒で生成", "supports durations from 4 to 15 s"): dola-pool's own code answers these
# with the requested duration, so the generic auto reply below must leave them alone.
DURATION_TEXT = re.compile(
    r"動画生成には|秒で生成|A/B/C で選んで|supports durations from|nearest supported duration|"
    r"秒数は|から選んで|選んでください|選んで下さい|choose.*duration|select.*duration|"
    r"対応しています|別の秒数|秒数をご希望|秒数を指定|supports?\s+\d+\s*[-–~]\s*\d+\s*(?:s\b|sec)|"
    r"\d+\s*[~～-]\s*\d+\s*秒",
    re.IGNORECASE,
)
ACCEPTANCE_TEXT = re.compile(r"生成されます|ポイントを消費|を生成します|ビデオを生成|動画を生成|生成を開始|分後に完成", re.IGNORECASE)


def _is_our_prompt(text: str, prompt: str) -> bool:
    head = (prompt or "").strip()[:30]
    return bool(head) and (text.startswith(head) or head.startswith(text[:30]))


def is_dola_question(text: str, prompt: str) -> bool:
    t = (text or "").strip()
    if len(t) < 12 or _is_our_prompt(t, prompt) or ACCEPTANCE_TEXT.search(t):
        return False
    return bool(QUESTION_MARK.search(t))


def dola_note(texts, prompt: str) -> str:
    """What Dola's chat agent wrote besides our own prompt (e.g. "the video came out at 10s instead of 15s")."""
    seen, out = set(), []
    for t in texts or []:
        t = (t or "").strip()
        if not t or t in seen or _is_our_prompt(t, prompt):
            continue
        seen.add(t)
        out.append(t)
    return " | ".join(out)[:600]


class AskBackWatcher:
    """Answers a question from Dola that is NOT about the duration (face image A/B, confirm script...), up to 3 times."""

    def __init__(self, prompt: str, auto_reply: str | None):
        self.prompt = prompt
        self.auto_reply = auto_reply
        self.grace = AUTO_REPLY_GRACE_SEC if auto_reply else ASKED_BACK_GRACE_SEC
        self.since: float | None = None
        self.text = ""
        self.answered: set[str] = set()
        self.replies = 0

    async def step(self, page, texts: list[str], has_video: bool, now: float) -> None:
        questions = [t for t in texts
                     if t not in self.answered and is_dola_question(t, self.prompt) and not DURATION_TEXT.search(t)]
        if not questions or has_video:
            self.since = None
            return
        if self.since is None:
            self.since, self.text = now, questions[-1]
            return
        if now - self.since < self.grace:
            return
        if self.auto_reply and self.replies < MAX_AUTO_REPLIES:
            print(f"[asked back] {self.text[:150]!r} -> auto reply #{self.replies + 1}", flush=True)
            self.answered.update(questions)
            await send_chat_message(page, self.auto_reply)
            self.replies += 1
            self.since = None
            return
        raise AskedBackError(self.text.strip())

    def note_prefix(self) -> str:
        return f"[App tự trả lời Dola {self.replies} lần] " if self.replies else ""


# ------------------------------------------------------------------ local reference images

def materialize_local_paths(paths: list[str]) -> list[str]:
    """Copy reference images picked on THIS machine into REFERENCE_FILE_DIR (where dola-pool keeps inline images).

    Only called for loopback requests. Checks: absolute path, real image, JPEG/PNG/WEBP, size limit, count limit.
    The copies are what the task uses (and deletes at the end); the originals are never touched.
    """
    from PIL import Image

    formats = {"JPEG": ".jpg", "PNG": ".png", "WEBP": ".webp"}
    if len(paths) > config.REFERENCE_IMAGE_MAX_COUNT:
        raise ValueError(f"Maximum of {config.REFERENCE_IMAGE_MAX_COUNT} reference images allowed")
    root = Path(config.REFERENCE_FILE_DIR)
    root.mkdir(parents=True, exist_ok=True)
    out: list[str] = []
    seen: set[str] = set()
    total = 0
    try:
        for raw in paths:
            src = Path(raw)
            if not src.is_absolute() or not src.is_file():
                raise ValueError(f"Reference image not found: {raw}")
            size = src.stat().st_size
            if size > config.REFERENCE_IMAGE_MAX_BYTES:
                raise ValueError(f"Reference image exceeds single file size limit: {src.name}")
            try:
                with Image.open(src) as image:
                    image.verify()
                    fmt = image.format
            except Exception as exc:
                raise ValueError(f"Reference file is not a valid image: {src.name}") from exc
            if fmt not in formats:
                raise ValueError(f"Reference image only supports JPEG, PNG, WEBP: {src.name}")
            key = str(src.resolve())
            if key in seen:
                continue
            seen.add(key)
            total += size
            if total > config.REFERENCE_TOTAL_MAX_BYTES:
                raise ValueError("Reference images exceed the total size limit")
            dest = root / f"ref_{uuid.uuid4().hex[:12]}_{len(out)}{formats[fmt]}"
            shutil.copyfile(src, dest)
            out.append(str(dest))
    except Exception:
        for p in out:
            Path(p).unlink(missing_ok=True)
        raise
    return out


# ------------------------------------------------------------------ failure code

def classify_failure(exc: BaseException) -> str:
    """Stable code for the coordinator so it can decide whether to switch account.
    account_limited | credit | risk_control | login_required | unhealthy | asked_back | timeout | 429 | no_account | error"""
    from browser_pool import AllAccountsLimitedError, AllAccountsQuotaBlockedError
    from dola_client import CreditError
    from pure_api_gen import AbnormalNoAckError
    from video_worker import RiskControlError
    from video_worker_ui import AccountLimitedError, CreditInsufficientError, LoginExpiredError

    if isinstance(exc, AskedBackError):
        return "asked_back"
    if isinstance(exc, AccountLimitedError):
        return "account_limited"
    if isinstance(exc, (CreditInsufficientError, CreditError)):
        return "credit"
    if isinstance(exc, RiskControlError):
        return "risk_control"
    if isinstance(exc, LoginExpiredError):
        return "login_required"
    if isinstance(exc, AbnormalNoAckError):
        return "unhealthy"
    if isinstance(exc, TimeoutError):
        return "timeout"
    if isinstance(exc, (AllAccountsLimitedError, AllAccountsQuotaBlockedError)):
        return "429"
    if isinstance(exc, RuntimeError) and "No available accounts" in str(exc):
        return "no_account"
    return "error"

# ---------------------------------------------------------------------------------------------------------------------
# Giao diện Dola KIỂU MỚI: Dola cấp cho một số tài khoản giao diện khác: không còn nút "比率" và các ô "10s/15s/30s" riêng mà chỉ có một
# nút gộp "自動 · 10秒" mở ra bảng gồm lưới tỷ lệ + thanh trượt độ dài 4–10 giây. Đường trình duyệt của dola-pool bấm theo chữ
# nên với giao diện này nó âm thầm dùng mặc định. Hàm dưới nhận ra giao diện mới và chọn tỷ lệ / độ dài trong bảng đó.
NEW_UI_CHIP = re.compile(r"·\s*\d+秒")


class DurationUnsupportedError(RuntimeError):
    """Giao diện của tài khoản không cung cấp độ dài yêu cầu (pool sẽ thử tài khoản khác)."""


async def set_options_new_ui(page, ratio, duration) -> bool:
    """False nếu không phải giao diện mới (để dùng cách cũ); True nếu đã chọn xong trong bảng mới."""
    chip = page.get_by_text(NEW_UI_CHIP).first
    if not (await chip.count() and await chip.is_visible()):
        return False
    print("  (giao diện Dola kiểu mới: chọn tỷ lệ / độ dài trong bảng)", flush=True)
    await chip.click(timeout=3000)
    await page.wait_for_timeout(800)
    if ratio:
        try:
            await page.get_by_text(ratio, exact=True).first.click(timeout=3000)
            await page.wait_for_timeout(300)
        except Exception as e:  # noqa: BLE001
            print(f"  (Failed to set ratio in the new panel, using default: {str(e)[:80]})", flush=True)
    if duration:
        thumb = page.locator("[aria-valuemax]").first
        if not await thumb.count():
            raise RuntimeError("Dola's new options panel has no length slider")
        top = int(await thumb.get_attribute("aria-valuemax") or 0)
        low = int(await thumb.get_attribute("aria-valuemin") or 0)
        first_sec = 4  # thanh trượt bắt đầu ở 4 giây, mỗi bước 1 giây
        max_sec = first_sec + top - low
        if duration > max_sec:
            await page.keyboard.press("Escape")
            raise DurationUnsupportedError(
                f"This account's Dola interface only offers {first_sec}-{max_sec} s (slider), {duration}s was requested")
        await thumb.focus()
        await page.keyboard.press("Home")
        for _ in range(max(0, duration - first_sec)):
            await page.keyboard.press("ArrowRight")
        await page.wait_for_timeout(300)
        now = int(await thumb.get_attribute("aria-valuenow") or -1)
        if now - low + first_sec != duration:
            print(f"  (Length slider ended at {now - low + first_sec}s instead of {duration}s)", flush=True)
    await page.keyboard.press("Escape")
    await page.wait_for_timeout(300)
    return True
