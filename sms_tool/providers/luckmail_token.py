from urllib.parse import quote

from curl_cffi import requests as curl_requests


class LuckMailTokenClient:
    """Direct LuckMail token mailbox client.

    LuckMail long-term mailbox purchases return a tok_*/lmp_* mailbox token.
    These endpoints read that token directly from LuckMail:
    - GET /api/v1/openapi/email/token/{token}/code
    - GET /api/v1/openapi/email/token/{token}/mails
    - GET /api/v1/openapi/email/token/{token}/alive
    """

    def __init__(self, base_url, api_key, timeout=30, verify_tls=False):
        self.base_url = str(base_url or "https://mails.luckyous.com").rstrip("/")
        self.api_key = str(api_key or "").strip()
        self.timeout = int(timeout or 30)
        self.verify_tls = bool(verify_tls)
        if not self.api_key:
            raise RuntimeError("LuckMail token API key is required")

    def code(self, mailbox_token):
        return self._request("code", mailbox_token)

    def mails(self, mailbox_token):
        return self._request("mails", mailbox_token)

    def alive(self, mailbox_token):
        return self._request("alive", mailbox_token)

    def resolve_email(self, mailbox_token):
        for fetch in (self.alive, self.code, self.mails):
            body = fetch(mailbox_token)
            email = str(((body.get("data") or {}).get("email_address")) or "").strip().lower()
            if email:
                return email
        return ""

    def _request(self, endpoint, mailbox_token):
        token = self._validate_mailbox_token(mailbox_token)
        url = f"{self.base_url}/api/v1/openapi/email/token/{quote(token, safe='')}/{endpoint}"
        r = curl_requests.get(
            url,
            headers=self._headers(),
            impersonate="chrome",
            timeout=self.timeout,
            verify=self.verify_tls,
        )
        try:
            body = r.json()
        except Exception:
            body = {"raw": r.text[:500]}
        if r.status_code < 200 or r.status_code >= 300:
            raise RuntimeError(f"LuckMail token HTTP {r.status_code}: {body}")
        if body.get("code") not in (0, None):
            raise RuntimeError(f"LuckMail token API error: {body}")
        return body

    def _headers(self):
        return {
            "X-API-Key": self.api_key,
            "Accept": "application/json",
            "Content-Type": "application/json",
        }

    @staticmethod
    def _validate_mailbox_token(mailbox_token):
        token = str(mailbox_token or "").strip()
        if not token:
            raise RuntimeError("mailbox token is required")
        if not (token.startswith("tok_") or token.startswith("lmp_")):
            raise RuntimeError("mailbox token must start with tok_ or lmp_")
        return token
