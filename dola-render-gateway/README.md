# Dola Render Gateway

A high-performance session coordinator and OpenAI-compatible video generation API service.

Provides automated browser session isolation, task queue distribution, extended duration handling, and an intuitive web management dashboard.

---

## 🌟 Key Capabilities

1. **OpenAI-Compatible Video API**:
   - `POST /v1/videos/generations`: Submit generation tasks with prompt, aspect ratio, duration (`10s`, `15s`, `30s`), and reference images.
   - `GET /v1/videos/<id>`: Poll task lifecycle (`queued` -> `processing` -> `completed` / `failed`).
   - High-speed MP4 streaming and static asset delivery.
2. **Extended Duration & High-Definition Media Export**:
   - Integrated browser automation profile for managing extended duration options.
   - Direct original quality stream extraction and processing.
3. **Multi-Account Browser Pool**:
   - Manages multiple persistent browser profiles in `accounts/`.
   - Automatic concurrency management, mutual exclusion, and session rotation.
   - Built-in verification handling.
4. **Admin Web Dashboard**:
   - Real-time dashboard at `/web` to monitor generation trends, success rate, account statuses, task queues, and API key management.

---

## 📁 Repository Structure

```
dola-render-gateway/
├── server.py              # FastAPI server (OpenAI-compatible video API & admin routes)
├── browser_pool.py        # Account pool concurrency manager and task scheduler
├── browser.py             # Playwright persistent context launcher
├── video_worker_ui.py     # UI automation worker with verification handler
├── video_worker.py        # Protocol worker and status polling
├── store.py               # SQLite task persistence and API key storage
├── dola_client.py         # API client communication module
├── media.py               # Reference media processor
├── config.py              # Configuration & environment variables
├── add_account.py         # Automated account profile setup (Google login helpers, TOTP)
├── add_account_cookie.py  # Import a Dola cookie into accounts/<name>
├── fb_to_dola.py          # Facebook cookie -> Dola session
├── open_profile.py        # Interactive login / open profile (status via .profile_status.json)
├── warmup.py              # Daily greeting chat
├── dola_errors.py         # Typed errors (login required, unhealthy account)
├── web/
│   └── index.html         # Single-page admin management dashboard
└── extensions/
    └── dola30/            # Chromium extension profile
```

---

## 🚀 Quick Start

### 1. Requirements
* Python 3.11+
* Chrome / Chromium browser
* Proxy with JP/KR egress

### 2. Setup Environment
```bash
# Create virtual environment
python -m venv .venv
source .venv/bin/activate       # On Windows: .venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt
playwright install chromium
```

### 3. Configure
```bash
# Set your proxy configuration
export DOLA_PROXY="http://127.0.0.1:7890"

# Set API key for client authentication (optional, empty = dev mode)
export DOLA_API_KEYS="sk-your-secret-key"

# Concurrency limits
export DOLA_MAX_CONCURRENCY=3
```

### 4. Start Server
```bash
uvicorn server:app --host 0.0.0.0 --port 8000
```
Open **http://127.0.0.1:8000/web** to access the Admin Dashboard.

### 5. Video flow, pre-flight greeting chat and API additions

For every video the UI worker (`video_worker_ui.py`) does, in order: open the account profile → check login →
read the credit balance → **greeting chat** (one random question, waits for Dola's answer; captcha is solved if it
appears) → **open a new chat** → open "Create video" → attach reference images → set model / ratio / duration →
type the prompt → solve captcha → poll the conversation → download the video.

If the greeting chat gets no answer the account is put on a 10-minute cooldown and the task fails with
`failure_code=unhealthy` (no video credit is spent).

| Variable | Default | Meaning |
|---|---|---|
| `DOLA_WARMUP` | `1` | Greeting chat runs once per account per day (`accounts_meta.warmup_day`); set `0` to disable it |
| `DOLA_WARMUP_TIMEOUT` | `90` | Seconds to wait for Dola's answer |
| `DOLA_WARMUP_QUESTIONS` | `warmup_questions.txt` | Optional file, one question per line (built-in list is used otherwise) |
| `DOLA_DRY_RUN` | `0` | Testing only: do everything except sending the video prompt |

`POST /v1/videos/generations` additions:
* `reference_local_paths`: absolute paths of image files on the gateway machine. **Accepted from loopback only**
  (403 otherwise). Images are validated (JPEG/PNG/WEBP, size limit) and copied to a temp folder during the run.
* `GET /v1/videos/<id>` now also returns `failure_code`, `account` and `stage`.
  `failure_code` is one of `account_limited`, `credit`, `risk_control`, `login_required`, `unhealthy`, `timeout`,
  `429`, `no_account`, `error`; `stage` is `warmup` → `new_chat` → `submitting` → `generating` → `done`.

`open_profile.py <account> [--login google|facebook|facebook-cookie] [--after keep|close]` opens an account profile in a
visible Chromium window (used by DolaCoordinator); credentials are read from one JSON line on stdin.


### 🌐 SonicVoice (For Voice Clone)

[![Website](https://img.shields.io/badge/Website-SonicVoice.pro-6366f1?style=for-the-badge&logo=google-chrome&logoColor=white)](https://sonicvoice.pro)

### 💬 Admin & Support

[![Zalo](https://img.shields.io/badge/Zalo-Nhóm%20Zalo-0068FF?style=for-the-badge&logoColor=white)](https://zalo.me/g/jvwa05y9id3apkgfocw0)

---

## 📜 License
For educational and internal testing purposes.
