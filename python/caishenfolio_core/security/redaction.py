from __future__ import annotations

import re
from typing import Any

_SENSITIVE_KEY_FRAGMENTS = (
    "password",
    "secret",
    "token",
    "api_key",
    "apikey",
    "authorization",
    "access_key",
    "private_key",
    "credential",
)

_ASSIGNMENT_RE = re.compile(
    r"(?i)\b(password|secret|token|api[_-]?key|authorization|access[_-]?key|private[_-]?key|credential)\b\s*[:=]\s*([^\s,;]+)"
)
_BEARER_RE = re.compile(r"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*")


def is_sensitive_key(key: str) -> bool:
    normalized = key.replace("-", "_").replace(" ", "_").lower()
    return any(fragment in normalized for fragment in _SENSITIVE_KEY_FRAGMENTS)


def redact_text(value: str) -> str:
    if not value:
        return value
    redacted = _ASSIGNMENT_RE.sub(r"\1=[REDACTED]", value)
    return _BEARER_RE.sub("Bearer [REDACTED]", redacted)


def redact_mapping(value: Any, key: str | None = None) -> Any:
    if key is not None and is_sensitive_key(key):
        return "[REDACTED]"
    if isinstance(value, str):
        return redact_text(value)
    if isinstance(value, dict):
        return {str(k): redact_mapping(v, str(k)) for k, v in value.items()}
    if isinstance(value, list):
        return [redact_mapping(item) for item in value]
    if isinstance(value, tuple):
        return tuple(redact_mapping(item) for item in value)
    return value
