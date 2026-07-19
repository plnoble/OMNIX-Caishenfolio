from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any


def _env(name: str) -> str:
    return (os.environ.get(name) or "").strip()


def load_secrets() -> dict[str, str]:
    """Load market secrets from env (preferred) and optional credentials file.

    Never invent defaults for tokens. Empty string means not configured.
    """
    secrets = {
        "tushare_token": _env("CAISHENFOLIO_TUSHARE_TOKEN") or _env("TUSHARE_TOKEN"),
        "alphavantage_api_key": _env("CAISHENFOLIO_ALPHAVANTAGE_API_KEY")
        or _env("ALPHAVANTAGE_API_KEY")
        or _env("ALPHA_VANTAGE_API_KEY"),
    }

    path = _env("CAISHENFOLIO_CREDENTIALS_PATH")
    if not path:
        return secrets

    file = Path(path)
    if not file.is_file():
        return secrets

    try:
        raw = json.loads(file.read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        return secrets

    if not isinstance(raw, dict):
        return secrets

    if not secrets["tushare_token"]:
        secrets["tushare_token"] = str(raw.get("tushare_token") or "").strip()
    if not secrets["alphavantage_api_key"]:
        secrets["alphavantage_api_key"] = str(raw.get("alphavantage_api_key") or "").strip()
    return secrets


def redact_secret(value: str, keep: int = 4) -> str:
    if not value:
        return "(未配置)"
    if len(value) <= keep:
        return "***"
    return value[:keep] + "***"


def public_secret_status() -> dict[str, Any]:
    secrets = load_secrets()
    return {
        "tushare_token_configured": bool(secrets["tushare_token"]),
        "tushare_token_hint": redact_secret(secrets["tushare_token"]),
        "alphavantage_api_key_configured": bool(secrets["alphavantage_api_key"]),
        "alphavantage_api_key_hint": redact_secret(secrets["alphavantage_api_key"]),
    }
