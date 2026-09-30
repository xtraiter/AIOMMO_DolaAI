"""Dola Pool: OpenAI-compatible Video API (FastAPI) and Admin Dashboard.

Endpoints (Asynchronous 2-stage):
POST /v1/videos/generations -> Create task (status=queued)
GET  /v1/videos/<id>         -> Query task status (queued/processing/completed/failed)
GET  /videos/<file>          -> Static video download server

Admin Dashboard: GET / -> web/index.html; Admin API /api/admin/*
"""
import asyncio
import hashlib
import ipaddress
import json
import re
import shutil
import time
import uuid
from collections import defaultdict
from pathlib import Path

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

import config
from add_account import add_account_flow
from browser_pool import AllAccountsLimitedError, AllAccountsQuotaBlockedError, BrowserPool
from dola_client import CreditError
from media import (
    copy_local_reference_images, download_reference_images, validate_local_image_paths, validate_reference_urls,
)
from video_worker_ui import (
    AccountLimitedError, AccountUnhealthyError, CreditInsufficientError, DolaAskedBackError, LoginRequiredError,
    RiskControlError,
)
from store import PendingTaskLimitExceeded, TaskQuotaExceeded, TaskStore

Path(config.DOWNLOAD_DIR).mkdir(parents=True, exist_ok=True)
Path("web").mkdir(parents=True, exist_ok=True)

app = FastAPI(title="dola-pool", version="0.4.0")

store = TaskStore(config.DB_PATH)
pool = BrowserPool(max_concurrency=config.MAX_CONCURRENCY)

app.mount("/videos", StaticFiles(directory=config.DOWNLOAD_DIR), name="videos")

# Background jobs (add/verify), in-memory
JOBS: dict[str, dict] = {}

SIZE_TO_RATIO = {
    "1280x720": "16:9", "1920x1080": "16:9",
    "720x1280": "9:16", "1080x1920": "9:16",
    "1024x1024": "1:1", "1440x1080": "4:3", "1080x1440": "3:4",
}
SUPPORTED_DURATIONS = (5, 10, 15, 30)
NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,32}$")


class KeyConcurrencyLimiter:
    """Concurrency limits per API Key; 0 = unlimited."""

    def __init__(self):
        self._condition = asyncio.Condition()
        self._active: defaultdict[str, int] = defaultdict(int)

    async def acquire(self, api_key_hash: str | None, limit: int):
        if not api_key_hash or limit <= 0:
            return
        async with self._condition:
            while self._active[api_key_hash] >= limit:
                await self._condition.wait()
            self._active[api_key_hash] += 1

    async def release(self, api_key_hash: str | None):
        if not api_key_hash:
            return
        async with self._condition:
            if self._active[api_key_hash] > 0:
                self._active[api_key_hash] -= 1
            if self._active[api_key_hash] == 0:
                self._active.pop(api_key_hash, None)
            self._condition.notify_all()



key_limiter = KeyConcurrencyLimiter()


# ===== Authentication =====


def _hash_key(key: str) -> str:
    return hashlib.sha256(key.encode("utf-8")).hexdigest()


def _anonymous_client() -> dict:
    return {
        "api_key_hash": None,
        "api_key_name": "Anonymous",
        "daily_limit": 0,
        "concurrency_limit": 0,
        "allowed_durations": list(SUPPORTED_DURATIONS),
    }


def _env_client(key: str) -> dict:
    return {
        "api_key_hash": _hash_key(key),
        "api_key_name": f"Env Key ({key[:8]}…)",
        "daily_limit": 0,
        "concurrency_limit": 0,
        "allowed_durations": list(SUPPORTED_DURATIONS),
    }


def _auth(authorization):
    """Returns client policy for caller; empty key enables dev mode."""
    if not config.API_KEYS and not store.has_enabled_keys():
        return _anonymous_client()
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(401, "missing bearer token")
    key = authorization[7:].strip()
    if not key:
        raise HTTPException(401, "missing bearer token")
    if key in config.API_KEYS:
        return _env_client(key)
    record = store.get_key(key)
    if not record or not store.is_key_valid(key):
        raise HTTPException(401, "invalid api key")
    store.touch_key(key)
    return {
        "api_key_hash": _hash_key(key),
        "api_key_name": record["name"] or "Unnamed Client",
        "daily_limit": record["daily_limit"],
        "concurrency_limit": record["concurrency_limit"],
        "allowed_durations": record["allowed_durations"],
    }


def _admin_auth(x_admin_key: str | None):
    if not config.ADMIN_KEY:
        return
    if x_admin_key != config.ADMIN_KEY:
        raise HTTPException(401, "invalid admin key")


def _normalize_allowed_durations(values) -> list[int]:
    if values is None:
        return list(SUPPORTED_DURATIONS)
    try:
        normalized = sorted({int(value) for value in values})
    except (TypeError, ValueError):
        raise HTTPException(422, "allowed_durations must be an array of 5, 10, 15, or 30")
    if not normalized or any(value not in SUPPORTED_DURATIONS for value in normalized):
        raise HTTPException(422, "allowed_durations must contain at least one of 5, 10, 15, 30")
    return normalized


# ===== Client API =====


class VideoGenRequest(BaseModel):
    model: str = "seedance-2.0"
    prompt: str = Field(..., min_length=1)
    size: str | None = None
    ratio: str | None = None
    duration: int | None = Field(None, ge=5, le=30)
    # Accepts durations: 5, 10, 15, 30 seconds (Dola's own dropdown offers 5s / 10s / 30s).
    reference_images: list[str] = Field(default_factory=list)
    # Absolute paths of image files on THIS machine (DolaCoordinator). Accepted from the loopback interface only.
    reference_local_paths: list[str] = Field(default_factory=list)
    account: str | None = None
    cookie: str | None = None
    # Render with the Chromium window kept off-screen (DolaCoordinator: "Ẩn Chromium"). Loopback default is visible.
    hide_window: bool = False
    # Text to send when Dola answers the prompt with a question instead of making the video (empty = do not answer).
    auto_reply: str | None = Field(None, max_length=600)
    # Drop duration words ("00:00 - 00:03", "Giây 0 đến 3", "30s") from the prompt text; null = gateway default (DOLA_STRIP_DURATION_WORDS).
    strip_duration_words: bool | None = None


class TaskResponse(BaseModel):
    id: str
    status: str
    model: str | None = None
    prompt: str | None = None
    video_url: str | None = None
    error: str | None = None
    # Machine-readable reason when status=failed: account_limited | credit | risk_control | login_required |
    # unhealthy | timeout | 429 | no_account | error
    failure_code: str | None = None
    account: str | None = None
    # Progress: warmup -> new_chat -> submitting -> generating -> done
    stage: str | None = None
    # What Dola's chat agent said next to the video (e.g. it produced a different length than requested)
    note: str | None = None


def _is_loopback(request: Request) -> bool:
    host = request.client.host if request.client else ""
    try:
        return host == "localhost" or ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def _classify_failure(exc: Exception) -> str:
    """Stable code for the coordinator so it can decide whether to switch account."""
    if isinstance(exc, AccountLimitedError):
        return "account_limited"
    if isinstance(exc, (CreditInsufficientError, CreditError)):
        return "credit"
    if isinstance(exc, RiskControlError):
        return "risk_control"
    if isinstance(exc, LoginRequiredError):
        return "login_required"
    if isinstance(exc, AccountUnhealthyError):
        return "unhealthy"
    if isinstance(exc, DolaAskedBackError):
        return "asked_back"
    if isinstance(exc, TimeoutError):
        return "timeout"
    if isinstance(exc, (AllAccountsLimitedError, AllAccountsQuotaBlockedError)):
        return "429"
    if isinstance(exc, RuntimeError) and "No available accounts" in str(exc):
        return "no_account"
    return "error"


def _resolve_ratio(size, ratio):
    if size and size in SIZE_TO_RATIO:
        return SIZE_TO_RATIO[size]
    return ratio


_running_tasks: dict[str, asyncio.Task] = {}


async def _run_task(task_id, model, prompt, ratio, duration, reference_images, client, preferred_account=None,
                    local_reference_paths=None, hide_window=False, auto_reply=None,
                    strip_duration_words=None):
    api_key_hash = client.get("api_key_hash")
    acquired = False
    reference_root = None
    local_root = None
    try:
        await key_limiter.acquire(api_key_hash, client.get("concurrency_limit", 0))
        acquired = True
        store.update(task_id, status="processing", started_at=time.time())

        def on_conversation_id(account, conversation_id, deadline_at):
            store.update(task_id, status="processing", account=account,
                         conversation_id=conversation_id, deadline_at=deadline_at,
                         last_poll_at=time.time())

        def on_poll(now):
            store.update(task_id, last_poll_at=now)

        def on_stage(stage):
            store.update(task_id, stage=stage)

        reference_root, reference_paths = await download_reference_images(
            reference_images or [], task_id)
        if local_reference_paths:
            local_root, local_copies = copy_local_reference_images(local_reference_paths, task_id)
            reference_paths = list(local_copies) + list(reference_paths)
        result = await pool.generate_video(
            prompt, ratio, duration, model,
            on_conversation_id=on_conversation_id, on_poll=on_poll,
            reference_image_paths=reference_paths,
            preferred_account=preferred_account, on_stage=on_stage, hide_window=hide_window,
            auto_reply=auto_reply, strip_duration_words=strip_duration_words)
        public_url = f"{config.PUBLIC_BASE}/videos/{Path(result['local_path']).name}"
        store.update(task_id, status="completed", video_url=public_url, stage="done",
                     account=result.get("account"), last_poll_at=time.time(),
                     finished_at=time.time(), note=result.get("note") or None)
    except asyncio.CancelledError:
        # Cancelled through DELETE /v1/videos/{id}: the worker's finally blocks close Chromium and free the account
        store.update(task_id, status="failed", error="Tác vụ đã bị hủy.", failure_code="cancelled",
                     finished_at=time.time())
        raise
    except Exception as e:
        store.update(task_id, status="failed", error=str(e)[:500],
                     failure_code=_classify_failure(e), finished_at=time.time())
    finally:
        if reference_root:
            shutil.rmtree(reference_root, ignore_errors=True)
        if local_root:
            shutil.rmtree(local_root, ignore_errors=True)
        if acquired:
            await key_limiter.release(api_key_hash)


async def _resume_task(row: dict):
    task_id = row["id"]
    deadline = row.get("deadline_at") or (
        time.time() + (1800 if row.get("duration") == 30 else config.VIDEO_TIMEOUT)
    )
    remaining = max(1, int(deadline - time.time()))
    api_key_hash = row.get("api_key_hash")
    acquired = False
    try:
        await key_limiter.acquire(
            api_key_hash, int(row.get("client_concurrency_limit") or 0)
        )
        acquired = True
        store.update(task_id, status="processing", last_poll_at=time.time(),
                     started_at=row.get("started_at") or time.time())

        def on_poll(now):
            store.update(task_id, last_poll_at=now)

        result = await pool.resume_video(
            row["account"], row["conversation_id"], remaining, on_poll=on_poll)
        public_url = f"{config.PUBLIC_BASE}/videos/{Path(result['local_path']).name}"
        store.update(task_id, status="completed", video_url=public_url,
                     account=result.get("account"), last_poll_at=time.time(),
                     finished_at=time.time())
    except Exception as e:
        store.update(task_id, status="failed", error=str(e)[:500],
                     finished_at=time.time())
    finally:
        if acquired:
            await key_limiter.release(api_key_hash)


def _task_client(row: dict) -> dict:
    """Restores client context from task snapshot."""
    return {
        "api_key_hash": row.get("api_key_hash"),
        "api_key_name": row.get("api_key_name") or "Historical Task",
        "daily_limit": 0,
        "concurrency_limit": int(row.get("client_concurrency_limit") or 0),
        "allowed_durations": list(SUPPORTED_DURATIONS),
    }


def _stored_reference_list(raw) -> list[str]:
    try:
        values = json.loads(raw or "[]")
    except (TypeError, json.JSONDecodeError):
        return []
    return [v for v in values if isinstance(v, str)] if isinstance(values, list) else []


def _task_reference_images(raw) -> list[str]:
    """Public URLs stored with the task (local files are stored with a 'file:' prefix)."""
    return [v for v in _stored_reference_list(raw) if not v.startswith("file:")]


def _task_local_reference_paths(raw) -> list[str]:
    return [v[5:] for v in _stored_reference_list(raw) if v.startswith("file:")]


@app.on_event("startup")
async def resume_incomplete_tasks():
    """Recovers accepted sessions on startup and requeues pending tasks."""
    for row in store.recoverable_tasks():
        asyncio.create_task(_resume_task(row))
    for row in store.recoverable_queued_tasks():
        ratio = row.get("ratio")
        if ratio == "default":
            ratio = None
        asyncio.create_task(_run_task(
            row["id"], row["model"], row["prompt"], ratio, row["duration"],
            _task_reference_images(row.get("reference_images")), _task_client(row),
            local_reference_paths=_task_local_reference_paths(row.get("reference_images")),
        ))


@app.post("/v1/videos/generations", response_model=TaskResponse)
async def create_video(req: VideoGenRequest, request: Request, authorization: str | None = Header(default=None)):
    client = _auth(authorization)
    duration = req.duration or 10
    if duration not in SUPPORTED_DURATIONS:
        raise HTTPException(422, "Currently supports durations of 5s, 10s, 15s, and 30s")
    if duration not in client["allowed_durations"]:
        raise HTTPException(422, f"Current API Key is not allowed to generate {duration}s videos")
    model_key = req.model.lower().replace("-", "_")
    if model_key not in (
        "seedance_2.0", "seedance_2.5", "seedance_v2.0", "seedance_v2.5",
        "seedance_20", "seedance_25", "seedance_v20", "seedance_v25",
    ):
        raise HTTPException(422, "Supported models are seedance-2.0 and seedance-2.5")
    if duration >= 30 and not model_key.endswith(("2.5", "25")):
        raise HTTPException(422, "30s videos are only available with seedance-2.5 (seedance-2.0 supports 5s/10s/15s)")
    try:
        reference_images = await validate_reference_urls(req.reference_images)
    except ValueError as exc:
        raise HTTPException(422, str(exc)) from exc

    local_reference_paths = []
    if req.reference_local_paths:
        # Reading files by path is only safe for a caller on the same machine; the server may listen on 0.0.0.0
        if not _is_loopback(request):
            raise HTTPException(403, "reference_local_paths is only accepted from the machine running the gateway")
        try:
            local_reference_paths = validate_local_image_paths(req.reference_local_paths)
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc
        if len(reference_images) + len(local_reference_paths) > config.REFERENCE_IMAGE_MAX_COUNT:
            raise HTTPException(422, f"Maximum of {config.REFERENCE_IMAGE_MAX_COUNT} reference images allowed")

    # Auto-import cookie into pool if supplied and account not yet present or cookie changed
    if req.cookie:
        target_account = req.account or "Account_1"
        cookie_path = Path("accounts") / target_account / "cookie.txt"
        fb_cookie_path = Path("accounts") / target_account / "fb_cookie.txt"
        current_cookie = ""
        current_fb_cookie = ""
        if cookie_path.exists():
            try: current_cookie = cookie_path.read_text(encoding="utf-8").strip()
            except: pass
        if fb_cookie_path.exists():
            try: current_fb_cookie = fb_cookie_path.read_text(encoding="utf-8").strip()
            except: pass

        # Check if Dola session is currently marked as dead (login_ok = False)
        is_dola_dead = False
        for acc_info in pool.list_accounts():
            if acc_info["name"] == target_account and acc_info["login_ok"] is False:
                is_dola_dead = True
                break

        try:
            if "c_user=" in req.cookie or "xs=" in req.cookie:
                # It's an FB cookie. Only convert if it's a new FB cookie, OR the Dola session died
                if target_account not in pool.accounts or req.cookie.strip() != current_fb_cookie or is_dola_dead:
                    from fb_to_dola import convert_fb_to_dola_session
                    print(f"[{target_account}] Converting FB cookie to Dola...", flush=True)
                    await convert_fb_to_dola_session(target_account, req.cookie, headless=True)
                    fb_cookie_path.parent.mkdir(parents=True, exist_ok=True)
                    fb_cookie_path.write_text(req.cookie.strip(), encoding="utf-8")
            else:
                # It's a Dola cookie
                if target_account not in pool.accounts or req.cookie.strip() != current_cookie:
                    from add_account_cookie import import_single_account
                    await import_single_account(target_account, req.cookie)
                    if fb_cookie_path.exists():
                        fb_cookie_path.unlink() # clear fb cookie tracking
        except Exception as e:
            print(f"[create_video] Error auto-importing account '{target_account}': {e}", flush=True)

    # Queue task when accounts are busy; reject only when pool is fully exhausted.
    if not pool.available and pool.all_accounts_limited:
        raise HTTPException(429, "Rate limited: All accounts reached Dola daily video limit, please try again tomorrow")
    if not pool.available and pool.all_accounts_quota_blocked:
        raise HTTPException(429, "Insufficient credits: All accounts lack points, waiting for refresh")
    if not pool.accounts:
        raise HTTPException(503, "no account in pool")
    task_id = "video_" + uuid.uuid4().hex
    ratio = _resolve_ratio(req.size, req.ratio)
    try:
        store.create(
            task_id,
            req.model,
            req.prompt,
            ratio or "default",
            duration,
            reference_images=json.dumps(
                reference_images + [f"file:{p}" for p in local_reference_paths], ensure_ascii=False),
            api_key_hash=client["api_key_hash"],
            api_key_name=client["api_key_name"],
            daily_limit=client["daily_limit"],
            concurrency_limit=client["concurrency_limit"],
            max_pending=config.MAX_PENDING_TASKS,
        )
    except TaskQuotaExceeded as exc:
        raise HTTPException(429, str(exc)) from exc
    except PendingTaskLimitExceeded as exc:
        raise HTTPException(429, str(exc)) from exc
    running = asyncio.create_task(_run_task(
        task_id, req.model, req.prompt, ratio, duration, reference_images, client,
        preferred_account=req.account, local_reference_paths=local_reference_paths,
        hide_window=req.hide_window, auto_reply=(req.auto_reply or "").strip() or None,
        strip_duration_words=req.strip_duration_words,
    ))
    _running_tasks[task_id] = running
    running.add_done_callback(lambda _t, tid=task_id: _running_tasks.pop(tid, None))
    return TaskResponse(id=task_id, status="queued", model=req.model, prompt=req.prompt)


@app.delete("/v1/videos/{task_id}")
async def cancel_video(task_id: str, authorization: str | None = Header(default=None)):
    """Cancels a queued / running task: closes its Chromium so the account is free again (no credit is refunded)."""
    client = _auth(authorization)
    row = store.get_for_client(task_id, client["api_key_hash"])
    if not row:
        raise HTTPException(404, "task not found")
    running = _running_tasks.get(task_id)
    if running is None or running.done():
        return {"id": task_id, "status": row["status"], "cancelled": False}
    running.cancel()
    try:
        await asyncio.wait_for(asyncio.shield(running), timeout=20)
    except (asyncio.CancelledError, asyncio.TimeoutError, Exception):
        pass  # the task records its own failed/cancelled state; a slow browser close must not block the caller
    return {"id": task_id, "status": "cancelled", "cancelled": True}


@app.get("/v1/videos/{task_id}", response_model=TaskResponse)
async def get_video(task_id: str, authorization: str | None = Header(default=None)):
    client = _auth(authorization)
    row = store.get_for_client(task_id, client["api_key_hash"])
    if not row:
        raise HTTPException(404, "task not found")
    return TaskResponse(
        id=row["id"], status=row["status"], model=row["model"],
        prompt=row["prompt"], video_url=row["video_url"], error=row["error"],
        failure_code=row.get("failure_code"), account=row.get("account"), stage=row.get("stage"),
        note=row.get("note"),
    )


@app.get("/health")
async def health():
    return {
        "ok": True,
        "accounts": pool.account_status(),
        "available": pool.available,
        "pending_tasks": store.pending_task_count(),
        "max_pending_tasks": config.MAX_PENDING_TASKS,
    }


# ===== Admin Dashboard API =====


class AdminLogin(BaseModel):
    key: str


class AccountPatch(BaseModel):
    scheduling: bool | None = None
    note: str | None = None
    email: str | None = None


class AccountAdd(BaseModel):
    name: str
    email: str
    password: str
    totp: str


class KeyCreate(BaseModel):
    name: str = ""
    daily_limit: int = Field(0, ge=0, le=1_000_000)
    concurrency_limit: int = Field(0, ge=0, le=1_000)
    allowed_durations: list[int] = Field(default_factory=lambda: list(SUPPORTED_DURATIONS))
    expires_at: float | None = Field(None, ge=0)


class KeyPatch(BaseModel):
    name: str | None = None
    enabled: bool | None = None
    daily_limit: int | None = Field(None, ge=0, le=1_000_000)
    concurrency_limit: int | None = Field(None, ge=0, le=1_000)
    allowed_durations: list[int] | None = None
    expires_at: float | None = Field(None, ge=0)


@app.post("/api/admin/login")
async def admin_login(body: AdminLogin):
    if not config.ADMIN_KEY:
        return {"ok": True, "auth_required": False}
    if body.key == config.ADMIN_KEY:
        return {"ok": True, "auth_required": True}
    raise HTTPException(401, "wrong admin key")


@app.get("/api/admin/accounts")
async def admin_accounts(x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    return {"accounts": pool.list_accounts()}


@app.patch("/api/admin/accounts/{name}")
async def admin_account_patch(name: str, body: AccountPatch,
                              x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    if name not in pool.accounts:
        raise HTTPException(404, "account not found")
    if body.scheduling is not None:
        pool.set_scheduling(name, body.scheduling)
    if body.note is not None:
        pool.set_note(name, body.note)
    if body.email is not None:
        pool.set_email(name, body.email)
    return {"ok": True}


@app.delete("/api/admin/accounts/{name}")
async def admin_account_delete(name: str, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    if name not in pool.accounts:
        raise HTTPException(404, "account not found")
    try:
        pool.delete_account(name)
    except RuntimeError as e:
        raise HTTPException(409, str(e))
    return {"ok": True}


@app.post("/api/admin/accounts/{name}/verify")
async def admin_account_verify(name: str, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    try:
        ok = await pool.verify_account(name)
    except RuntimeError as e:
        raise HTTPException(409, str(e))
    except FileNotFoundError as e:
        raise HTTPException(404, str(e))
    return {"ok": ok, "login_ok": ok}


class CookieImport(BaseModel):
    name: str
    cookie: str


class FbCookieImport(BaseModel):
    name: str
    cookie: str
    proxy: str | None = None


@app.post("/api/admin/accounts/import_cookie")
async def admin_import_cookie(body: CookieImport, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    # Auto-detect Facebook cookie
    if "c_user=" in body.cookie or "xs=" in body.cookie:
        from fb_to_dola import convert_fb_to_dola_session
        res = await convert_fb_to_dola_session(body.name, body.cookie, headless=True)
        if not res.get("ok"):
            raise HTTPException(400, res.get("error", "Lỗi chuyển đổi Cookie Facebook"))
        
        # Save FB tracking cookie
        fb_cookie_path = Path("accounts") / body.name / "fb_cookie.txt"
        fb_cookie_path.parent.mkdir(parents=True, exist_ok=True)
        fb_cookie_path.write_text(body.cookie.strip(), encoding="utf-8")
        
        return {"ok": True, "name": body.name, "cookie": res.get("cookie")}

    from add_account_cookie import import_single_account
    success = await import_single_account(body.name, body.cookie)
    return {"ok": success, "name": body.name}


@app.post("/api/admin/accounts/import_fb_cookie")
async def admin_import_fb_cookie(body: FbCookieImport, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    from fb_to_dola import convert_fb_to_dola_session
    res = await convert_fb_to_dola_session(body.name, body.cookie, proxy=body.proxy, headless=True)
    if not res.get("ok"):
        raise HTTPException(400, res.get("error", "Lỗi chuyển đổi Cookie Facebook sang Dola"))
    
    # Save FB tracking cookie
    fb_cookie_path = Path("accounts") / body.name / "fb_cookie.txt"
    fb_cookie_path.parent.mkdir(parents=True, exist_ok=True)
    fb_cookie_path.write_text(body.cookie.strip(), encoding="utf-8")
    
    return res


async def _run_add_job(name: str, email: str, password: str, totp: str):
    JOBS[name] = {"kind": "add", "status": "running", "error": "", "started_at": time.time()}
    try:
        await add_account_flow(name, email, password, totp)
        pool.set_email(name, email)
        pool.set_login_status(name, True)
        JOBS[name] = {**JOBS[name], "status": "success"}
    except Exception as e:
        JOBS[name] = {**JOBS[name], "status": "failed", "error": str(e)[:300]}


@app.post("/api/admin/accounts", status_code=202)
async def admin_account_add(body: AccountAdd, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    if not NAME_RE.match(body.name):
        raise HTTPException(400, "invalid account name")
    if body.name in pool.accounts:
        raise HTTPException(409, "account exists")
    if JOBS.get(body.name, {}).get("status") == "running":
        raise HTTPException(409, "add job running")
    asyncio.create_task(_run_add_job(body.name, body.email, body.password, body.totp))
    return {"ok": True, "job": "running"}


@app.get("/api/admin/jobs")
async def admin_jobs(x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    return {"jobs": JOBS}


@app.get("/api/admin/tasks")
async def admin_tasks(limit: int = 50, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    return {"tasks": store.recent_tasks(min(max(limit, 1), 200))}


@app.get("/api/admin/stats")
async def admin_stats(x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    st = store.stats()
    accs = pool.list_accounts()
    sched = [a for a in accs if a["scheduling"] and not a["cooling"]]
    st["total_accounts"] = len(accs)
    st["available_accounts"] = sum(1 for a in sched if a["remaining"] > 0)
    st["total_remaining"] = sum(a["remaining"] for a in sched)
    totals = st.pop("per_account_total", {})
    st["per_account"] = [{**a, "completed_total": totals.get(a["name"], 0)} for a in accs]
    return st


@app.get("/api/admin/keys")
async def admin_keys(x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    keys = []
    for key in store.list_keys():
        usage = store.key_usage(store.hash_api_key(key["key"]))
        keys.append({**key, **{
            "today_total": usage["total"],
            "today_completed": usage["completed"],
            "today_failed": usage["failed"],
            "today_active": usage["active"],
            "today_queued": usage["queued"],
        }})
    return {"keys": keys, "env_keys": len(config.API_KEYS)}


@app.post("/api/admin/keys")
async def admin_key_create(body: KeyCreate, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    allowed = _normalize_allowed_durations(body.allowed_durations)
    return {"created": store.create_key(
        body.name,
        daily_limit=body.daily_limit,
        concurrency_limit=body.concurrency_limit,
        allowed_durations=allowed,
        expires_at=body.expires_at,
    )}


@app.patch("/api/admin/keys/{key}")
async def admin_key_patch(key: str, body: KeyPatch,
                          x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    if not store.get_key(key):
        raise HTTPException(404, "api key not found")
    fields = {}
    if body.name is not None:
        fields["name"] = body.name
    if body.enabled is not None:
        fields["enabled"] = 1 if body.enabled else 0
    if body.daily_limit is not None:
        fields["daily_limit"] = body.daily_limit
    if body.concurrency_limit is not None:
        fields["concurrency_limit"] = body.concurrency_limit
    if body.allowed_durations is not None:
        fields["allowed_durations"] = _normalize_allowed_durations(body.allowed_durations)
    if body.expires_at is not None:
        fields["expires_at"] = body.expires_at
    store.update_key(key, **fields)
    return {"ok": True}


@app.delete("/api/admin/keys/{key}")
async def admin_key_delete(key: str, x_admin_key: str | None = Header(default=None)):
    _admin_auth(x_admin_key)
    if not store.get_key(key):
        raise HTTPException(404, "api key not found")
    store.delete_key(key)
    return {"ok": True}


# Dashboard single-file frontend
app.mount("/", StaticFiles(directory="web", html=True), name="web")

