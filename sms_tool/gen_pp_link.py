#!/usr/bin/env python3
"""生成 ChatGPT Plus PayPal 授权链接（Stripe Elements confirm 流程）。

完全独立实现，不依赖 gopay.py。

用法：
  python3 gen_pp_link.py <access_token>
  python3 gen_pp_link.py --dry-run

流程：checkout → stripe init → create pm (paypal) → confirm → 授权链接
"""

from __future__ import annotations

import json
import os
import random
import re
import sys
import uuid
from typing import Any
from urllib.parse import quote

import requests

# 可选 curl_cffi（Chrome TLS 指纹）
try:
    from curl_cffi.requests import Session as _CurlCffiSession
except ImportError:
    _CurlCffiSession = None

# ──────────────────────────── 常量 ────────────────────────────

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG_PATH = os.path.join(SCRIPT_DIR, "config.json")

DEFAULT_STRIPE_PK = (
    "pk_live_51HOrSwC6h1nxGoI3lTAgRjYVrz4dU3fVOabyCcKR3pbEJguCVAlqCxdxCUvoRh1XWwRac"
    "ViovU3kLKvpkjh7IqkW00iXQsjo3n"
)

DEFAULT_TIMEOUT = 30

PP_PROXIES = [
    "socks5h://127.0.0.1:17912",  # JP（优惠券资格）
    "socks5h://127.0.0.1:17911",  # US（备选）
]

BILLING_REGIONS = [
    {"country": "US", "currency": "USD", "label": "美国（USD）"},
]

# ──────────────────────────── Session ────────────────────────────


def _new_session(impersonate: str = "chrome136") -> Any:
    if _CurlCffiSession is not None:
        return _CurlCffiSession(impersonate=impersonate)
    return requests.Session()


def _build_chatgpt_session(access_token: str) -> Any:
    device_id = str(uuid.uuid4())
    user_agent = (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36"
    )
    s = _new_session()
    s.headers.update({
        "User-Agent": user_agent,
        "Accept": "*/*",
        "Accept-Language": "en-US,en;q=0.9",
        "Origin": "https://chatgpt.com",
        "Referer": "https://chatgpt.com/",
        "Content-Type": "application/json",
        "oai-device-id": device_id,
        "oai-language": "en-US",
        "sec-ch-ua": '"Google Chrome";v="147", "Not.A/Brand";v="8", "Chromium";v="147"',
        "sec-ch-ua-mobile": "?0",
        "sec-ch-ua-platform": '"Windows"',
        "sec-fetch-dest": "empty",
        "sec-fetch-mode": "cors",
        "sec-fetch-site": "same-origin",
    })
    if access_token:
        s.headers["Authorization"] = f"Bearer {access_token}"
    s.headers["Cookie"] = f"oai-did={device_id}; __Secure-next-auth.session-token=dummy"
    return s


# ──────────────────────────── Token 解析 ────────────────────────────


def parse_token(raw: str) -> str | None:
    value = raw.strip()
    if not value:
        return None
    if value.startswith("{"):
        try:
            data = json.loads(value)
        except json.JSONDecodeError:
            return None
        for key in ("accessToken", "access_token"):
            tok = data.get(key)
            if isinstance(tok, str) and tok.startswith("eyJ"):
                return tok
        return None
    if value.startswith("eyJ") and value.count(".") == 2:
        return value
    return None


# ──────────────────────────── 辅助函数 ────────────────────────────


def _load_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _extract_checkout_context(data: dict[str, Any]) -> tuple[str, str, str]:
    cs_id = str(data.get("checkout_session_id") or data.get("session_id") or data.get("id") or "").strip()
    processor_entity = str(data.get("processor_entity") or "").strip()
    checkout_url = str(
        data.get("checkout_url") or data.get("url") or data.get("openai_checkout_url") or ""
    ).strip()
    candidate_texts = [
        checkout_url,
        str(data.get("success_url") or ""),
        str(data.get("cancel_url") or ""),
        str(data.get("return_url") or ""),
        str(data.get("client_secret") or ""),
    ]
    if not cs_id:
        for text in candidate_texts:
            m = re.search(r"(cs_(?:live|test)_[A-Za-z0-9]+)", text or "")
            if m:
                cs_id = m.group(1)
                break
    if not processor_entity:
        for text in candidate_texts:
            m = re.search(r"/checkout/([^/]+)/cs_(?:live|test)_[A-Za-z0-9]+", text or "")
            if m:
                processor_entity = m.group(1)
                break
        if not processor_entity:
            m = re.search(r"processor_entity=([A-Za-z0-9_]+)", " ".join(candidate_texts))
            if m:
                processor_entity = m.group(1)
    if not checkout_url and cs_id and processor_entity:
        checkout_url = f"https://chatgpt.com/checkout/{processor_entity}/{cs_id}"
    return cs_id, processor_entity, checkout_url


# ──────────────────────────── 核心流程 ────────────────────────────


def _try_paypal_link(
    access_token: str,
    cfg: dict,
    region: dict,
    proxy: str,
) -> dict[str, Any] | None:
    stripe_pk = (cfg.get("stripe") or {}).get("publishable_key") or DEFAULT_STRIPE_PK
    runtime_cfg = cfg.get("runtime") or {}
    runtime_version = runtime_cfg.get("version") or "fed52f3bc6"

    # 构建 ChatGPT session
    cs = _build_chatgpt_session(access_token)
    cs.proxies = {"http": proxy, "https": proxy}

    # 构建 Stripe 外部 session
    ext = _new_session()
    ext.proxies = {"http": proxy, "https": proxy}
    ext.headers.update({
        "User-Agent": cs.headers.get("User-Agent", ""),
        "Accept-Language": "en-US,en;q=0.9",
    })

    # ── Step 1: ChatGPT checkout ──
    body: dict[str, Any] = {
        "entry_point": "all_plans_pricing_modal",
        "plan_name": "chatgptplusplan",
        "billing_details": {
            "country": region["country"],
            "currency": region["currency"],
        },
        "checkout_ui_mode": "hosted",
        "cancel_url": "https://chatgpt.com/#pricing",
        "promo_campaign": {
            "promo_campaign_id": "plus-1-month-free",
            "is_coupon_from_query_param": False,
        },
    }

    print(f"[pp] checkout: region={region['country']} promo=plus-1-month-free proxy={proxy}", file=sys.stderr)

    r = cs.post(
        "https://chatgpt.com/backend-api/payments/checkout",
        json=body, timeout=DEFAULT_TIMEOUT,
    )
    if r.status_code == 400:
        err_text = r.text[:300]
        if "already paid" in err_text.lower():
            return {"ok": False, "error": "该账号已有 ChatGPT Plus 订阅，无法重复创建 checkout"}
        return {"ok": False, "error": f"checkout 创建失败: {r.status_code} {err_text}"}
    r.raise_for_status()

    data = r.json()
    cs_id, processor_entity, checkout_url = _extract_checkout_context(data)
    if not cs_id or not cs_id.startswith("cs_"):
        return {"ok": False, "error": f"checkout 响应异常: {json.dumps(data, ensure_ascii=False)[:300]}"}

    processor_entity = processor_entity or ("openai_llc" if region["country"] == "US" else "openai_ie")
    print(f"[pp] cs_id={cs_id} processor_entity={processor_entity}", file=sys.stderr)

    # ── Step 2: Stripe init ──
    init_body = {
        "browser_locale": "en-US",
        "browser_timezone": "Asia/Shanghai",
        "elements_session_client[client_betas][0]": "custom_checkout_server_updates_1",
        "elements_session_client[client_betas][1]": "custom_checkout_manual_approval_1",
        "elements_session_client[elements_init_source]": "custom_checkout",
        "elements_session_client[referrer_host]": "chatgpt.com",
        "elements_session_client[stripe_js_id]": str(uuid.uuid4()),
        "elements_session_client[locale]": "en",
        "elements_session_client[is_aggregation_expected]": "false",
        "elements_options_client[stripe_js_locale]": "auto",
        "key": stripe_pk,
        "_stripe_version": (
            "2025-03-31.basil; checkout_server_update_beta=v1; "
            "checkout_manual_approval_preview=v1"
        ),
    }
    r1 = ext.post(
        f"https://api.stripe.com/v1/payment_pages/{cs_id}/init",
        data=init_body, timeout=DEFAULT_TIMEOUT,
    )
    r1.raise_for_status()
    init_data = r1.json() or {}

    init_checksum = init_data.get("init_checksum") or ""
    if not init_checksum:
        return {"ok": False, "error": f"Stripe init 无 init_checksum: {r1.text[:200]}"}

    due = (init_data.get("total_summary") or {}).get("due")
    amount_due = (init_data.get("invoice") or {}).get("amount_due")
    currency = (init_data.get("invoice") or {}).get("currency") or region["currency"]
    pm_types = init_data.get("payment_method_types") or []
    has_paypal = any("paypal" in (p or "").lower() for p in pm_types)

    expected_amount = "0"
    if due is not None:
        expected_amount = str(due)
    elif amount_due is not None:
        expected_amount = str(amount_due)

    print(f"[pp] init: due={due} amount_due={amount_due} currency={currency} pm_types={pm_types}", file=sys.stderr)

    if not has_paypal:
        return {"ok": False, "error": f"Stripe 不支持 PayPal（可用: {pm_types}）", "region": region["label"]}

    # ── Step 3: 创建 PayPal payment method ──
    stripe_js_id = str(uuid.uuid4())
    elements_session_id = f"elements_session_{uuid.uuid4().hex[:11]}"

    pm_body = {
        "type": "paypal",
        "billing_details[name]": "John Doe",
        "billing_details[email]": "buyer@example.com",
        "billing_details[address][country]": "US",
        "billing_details[address][line1]": "3110 Sunset Boulevard",
        "billing_details[address][city]": "Los Angeles",
        "billing_details[address][postal_code]": "90026",
        "billing_details[address][state]": "CA",
        "payment_user_agent": (
            f"stripe.js/{runtime_version}; stripe-js-v3/{runtime_version}; "
            "payment-element; deferred-intent"
        ),
        "referrer": "https://chatgpt.com",
        "time_on_page": str(random.randint(25000, 55000)),
        "client_attribution_metadata[client_session_id]": stripe_js_id,
        "client_attribution_metadata[checkout_session_id]": cs_id,
        "client_attribution_metadata[merchant_integration_source]": "elements",
        "client_attribution_metadata[merchant_integration_subtype]": "payment-element",
        "client_attribution_metadata[merchant_integration_version]": "2021",
        "client_attribution_metadata[payment_intent_creation_flow]": "deferred",
        "client_attribution_metadata[payment_method_selection_flow]": "automatic",
        "client_attribution_metadata[merchant_integration_additional_elements][0]": "payment",
        "guid": uuid.uuid4().hex,
        "muid": uuid.uuid4().hex,
        "sid": uuid.uuid4().hex,
        "key": stripe_pk,
        "_stripe_version": (
            "2025-03-31.basil; checkout_server_update_beta=v1; "
            "checkout_manual_approval_preview=v1"
        ),
    }

    r2 = ext.post(
        "https://api.stripe.com/v1/payment_methods",
        data=pm_body, timeout=DEFAULT_TIMEOUT,
    )
    print(f"[pp] pm create: status={r2.status_code}", file=sys.stderr)

    if r2.status_code != 200:
        return {"ok": False, "error": f"创建 PayPal PM 失败: {r2.status_code} {r2.text[:200]}"}

    pm_id = r2.json().get("id", "")
    if not pm_id.startswith("pm_"):
        return {"ok": False, "error": f"PM 响应异常: {r2.text[:200]}"}

    print(f"[pp] pm_id={pm_id}", file=sys.stderr)

    # ── Step 4: Stripe confirm ──
    chatgpt_return = (
        f"https://chatgpt.com/checkout/verify?stripe_session_id={cs_id}"
        f"&processor_entity={processor_entity}&plan_type=plus"
    )
    return_url = (
        f"https://checkout.stripe.com/c/pay/{cs_id}"
        f"?returned_from_redirect=true&ui_mode=custom&return_url={quote(chatgpt_return, safe='')}"
    )

    confirm_body = {
        "guid": uuid.uuid4().hex,
        "muid": uuid.uuid4().hex,
        "sid": uuid.uuid4().hex,
        "payment_method": pm_id,
        "init_checksum": init_checksum,
        "version": runtime_version,
        "expected_amount": expected_amount,
        "expected_payment_method_type": "paypal",
        "return_url": return_url,
        "elements_session_client[session_id]": elements_session_id,
        "elements_session_client[locale]": "en",
        "elements_session_client[referrer_host]": "chatgpt.com",
        "elements_session_client[is_aggregation_expected]": "false",
        "elements_session_client[client_betas][0]": "custom_checkout_server_updates_1",
        "elements_session_client[client_betas][1]": "custom_checkout_manual_approval_1",
        "elements_session_client[elements_init_source]": "custom_checkout",
        "elements_session_client[stripe_js_id]": stripe_js_id,
        "client_attribution_metadata[client_session_id]": stripe_js_id,
        "client_attribution_metadata[checkout_session_id]": cs_id,
        "client_attribution_metadata[merchant_integration_source]": "checkout",
        "client_attribution_metadata[merchant_integration_subtype]": "payment-element",
        "client_attribution_metadata[merchant_integration_version]": "custom",
        "client_attribution_metadata[payment_intent_creation_flow]": "deferred",
        "client_attribution_metadata[payment_method_selection_flow]": "automatic",
        "client_attribution_metadata[merchant_integration_additional_elements][0]": "payment",
        "client_attribution_metadata[merchant_integration_additional_elements][1]": "address",
        "elements_options_client[stripe_js_locale]": "auto",
        "elements_options_client[saved_payment_method][enable_save]": "never",
        "elements_options_client[saved_payment_method][enable_redisplay]": "never",
        "key": stripe_pk,
        "_stripe_version": (
            "2025-03-31.basil; checkout_server_update_beta=v1; "
            "checkout_manual_approval_preview=v1"
        ),
    }

    # Terms of service consent
    consent_collection = init_data.get("consent_collection") or {}
    tos = consent_collection.get("terms_of_service")
    if tos and tos not in ("none", ""):
        confirm_body["consent[terms_of_service]"] = "accepted"

    # Runtime anti-bot tokens
    if runtime_cfg.get("js_checksum"):
        confirm_body["js_checksum"] = runtime_cfg["js_checksum"]
    if runtime_cfg.get("rv_timestamp"):
        confirm_body["rv_timestamp"] = runtime_cfg["rv_timestamp"]

    r3 = ext.post(
        f"https://api.stripe.com/v1/payment_pages/{cs_id}/confirm",
        data=confirm_body, timeout=DEFAULT_TIMEOUT,
    )
    print(f"[pp] confirm: status={r3.status_code}", file=sys.stderr)

    if r3.status_code != 200:
        return {"ok": False, "error": f"Stripe confirm 失败: {r3.status_code} {r3.text[:300]}"}

    confirm_data = r3.json() or {}

    # 提取授权链接
    redirect_url = ""
    si = confirm_data.get("setup_intent") or {}
    na = si.get("next_action") or {}
    if na.get("type") == "redirect_to_url":
        redirect_url = (na.get("redirect_to_url") or {}).get("url", "")
    if not redirect_url:
        pi = confirm_data.get("payment_intent") or {}
        na = pi.get("next_action") or {}
        if na.get("type") == "redirect_to_url":
            redirect_url = (na.get("redirect_to_url") or {}).get("url", "")

    promo_applied = due == 0 or amount_due == 0
    coupon_state = f"eligible (0 {currency.upper()})" if promo_applied else f"not_eligible ({amount_due or due} {currency.upper()})"

    return {
        "ok": True,
        "url": redirect_url,
        "cs_id": cs_id,
        "pm_id": pm_id,
        "due": due,
        "amount_due": amount_due,
        "currency": currency,
        "payment_method_types": pm_types,
        "has_paypal": has_paypal,
        "coupon_state": coupon_state,
        "region": region["label"],
        "proxy": proxy,
    }


# ──────────────────────────── 入口 ────────────────────────────


def generate_pp_link(access_token: str) -> dict[str, Any]:
    try:
        cfg = _load_json(DEFAULT_CONFIG_PATH)
    except Exception as e:
        cfg = {}

    paypal_cfg = cfg.get("paypal") or {}
    proxies = paypal_cfg.get("proxies") or PP_PROXIES

    last_err = None
    for region in BILLING_REGIONS:
        for proxy in proxies:
            try:
                result = _try_paypal_link(access_token, cfg, region, proxy)
                if result and result.get("ok"):
                    return result
                if result and result.get("error"):
                    last_err = result["error"]
            except Exception as e:
                last_err = str(e)
                print(f"[pp] attempt failed: {region['label']}+{proxy}: {last_err}", file=sys.stderr)
                continue

    return {"ok": False, "error": f"所有尝试均失败，最后错误: {last_err}"}


def main() -> int:
    args = sys.argv[1:]

    if args and args[0] == "--dry-run":
        print(json.dumps({
            "ok": True,
            "mode": "dry-run",
            "config_exists": os.path.exists(DEFAULT_CONFIG_PATH),
            "proxies": PP_PROXIES,
            "regions": [r["label"] for r in BILLING_REGIONS],
        }, ensure_ascii=False, indent=2))
        return 0

    if not args:
        print(json.dumps({"ok": False, "error": "用法: gen_pp_link.py <access_token>"}, ensure_ascii=False))
        return 2

    access_token = parse_token(args[0])
    if not access_token:
        print(json.dumps({"ok": False, "error": "无效的 access_token（需要 eyJ 开头的 JWT 或 session JSON）"}, ensure_ascii=False))
        return 1

    result = generate_pp_link(access_token)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    raise SystemExit(main())
