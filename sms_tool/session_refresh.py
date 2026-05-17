import json
import re
import time
from pathlib import Path

from .config import CFG
from .paths import output_dir
from .storage import list_paypal_accounts, upsert_account


def refresh_session(email="", session_file="", timeout=300, headless=False):
    """Refresh ChatGPT session through a visible, user-driven OAuth/browser flow."""
    data, json_path = _load_seed_session(email=email, session_file=session_file)
    target_email = (email or data.get("email") or "").strip().lower()
    timeout = max(30, int(timeout or 300))

    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        return {"ok": False, "error": "playwright_not_installed"}

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=bool(headless))
        ctx = browser.new_context(
            user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/148.0.0.0 Safari/537.36",
            viewport={"width": 1280, "height": 900},
            locale="en-US",
            timezone_id="America/New_York",
        )
        _import_cookie_header(ctx, data.get("cookie_header", ""))
        try:
            page = ctx.new_page()
            page.goto(CFG["chatgpt"].get("chat_base_url", "https://chatgpt.com"), wait_until="domcontentloaded", timeout=120000)
            print("[*] Browser opened. Complete any required login/payment confirmation manually.")
            auth_session = _poll_auth_session(ctx, timeout)
            cookies = ctx.cookies()
        except Exception as e:
            return {"ok": False, "email": target_email, "error": str(e)}
        finally:
            browser.close()

    access_token = _session_token(auth_session, "accessToken", "access_token")
    oauth_refresh_token = _session_token(auth_session, "refreshToken", "refresh_token")
    if not access_token:
        return {"ok": False, "email": target_email, "error": "auth_session_missing_access_token"}

    refreshed = dict(data)
    if target_email:
        refreshed["email"] = target_email
    refreshed["success"] = True
    refreshed["access_token"] = access_token
    refreshed["auth_session"] = auth_session
    refreshed["cookie_header"] = _cookie_header(cookies)
    refreshed["oauth_refresh_token"] = oauth_refresh_token
    refreshed["refresh_token_status"] = "oauth_present" if oauth_refresh_token else "missing"
    refreshed["refresh_token_updated_at"] = int(time.time())
    refreshed["refreshed_at"] = int(time.time())

    if not json_path:
        json_path = _new_session_path(refreshed)
    Path(json_path).parent.mkdir(parents=True, exist_ok=True)
    Path(json_path).write_text(json.dumps(refreshed, ensure_ascii=False, indent=2), encoding="utf-8")
    upsert_account(refreshed, json_path=json_path)
    return {
        "ok": True,
        "email": refreshed.get("email", ""),
        "json_path": json_path,
        "refresh_token_status": refreshed["refresh_token_status"],
    }


def _load_seed_session(email="", session_file=""):
    if session_file:
        path = Path(session_file)
        return _read_json(path), str(path)
    if email:
        for row in list_paypal_accounts(email=email):
            json_path = str(row.get("json_path") or "").strip()
            if json_path and Path(json_path).exists():
                return _read_json(Path(json_path)), json_path
    return ({"email": email.strip().lower()} if email else {}, "")


def _read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def _poll_auth_session(ctx, timeout):
    chat_base = CFG["chatgpt"].get("chat_base_url", "https://chatgpt.com").rstrip("/")
    deadline = time.time() + timeout
    last_status = ""
    while time.time() < deadline:
        try:
            response = ctx.request.get(f"{chat_base}/api/auth/session", timeout=30000)
            last_status = str(response.status)
            if response.status == 200:
                body = response.json()
                if _session_token(body, "accessToken", "access_token"):
                    print("[*] Auth session refreshed.")
                    return body
        except Exception as e:
            last_status = str(e)
        print(f"[*] Waiting for auth session... {last_status}")
        time.sleep(3)
    raise RuntimeError("timed out waiting for ChatGPT auth session")


def _session_token(data, *keys):
    if not isinstance(data, dict):
        return ""
    for key in keys:
        value = data.get(key)
        if isinstance(value, str) and value:
            return value
    session = data.get("session")
    if isinstance(session, dict):
        for key in keys:
            value = session.get(key)
            if isinstance(value, str) and value:
                return value
    return ""


def _import_cookie_header(ctx, cookie_header):
    cookies = []
    for item in str(cookie_header or "").split(";"):
        if "=" not in item:
            continue
        name, value = item.split("=", 1)
        name = name.strip()
        value = value.strip()
        if not name or not value:
            continue
        cookies.append({
            "name": name,
            "value": value,
            "domain": ".chatgpt.com",
            "path": "/",
            "httpOnly": name.startswith("__Secure-") or name.startswith("__Host-"),
            "secure": True,
            "sameSite": "Lax",
        })
    if cookies:
        ctx.add_cookies(cookies)


def _cookie_header(cookies):
    return "; ".join(
        f"{cookie.get('name')}={cookie.get('value')}"
        for cookie in cookies
        if cookie.get("name") and cookie.get("value") and _chatgpt_cookie(cookie)
    )


def _chatgpt_cookie(cookie):
    domain = str(cookie.get("domain") or "")
    return "chatgpt.com" in domain


def _new_session_path(data):
    directory = output_dir(CFG)
    email = (data.get("email") or "unknown").replace("+", "")
    safe_email = re.sub(r"[^a-zA-Z0-9_.@-]+", "_", email)
    return str(directory / f"session_{safe_email}_{int(time.time())}.json")
