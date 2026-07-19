from __future__ import annotations

import os
from contextlib import contextmanager
from typing import Any, Callable, Iterator, TypeVar

T = TypeVar("T")

_PROXY_KEYS = (
    "HTTP_PROXY",
    "HTTPS_PROXY",
    "ALL_PROXY",
    "http_proxy",
    "https_proxy",
    "all_proxy",
)


def trust_env_enabled() -> bool:
    """Whether HTTP clients should honor system proxy env vars.

    Set CAISHENFOLIO_HTTP_TRUST_ENV=0 to force direct connection (common when
    a broken corporate proxy blocks Eastmoney).
    """
    raw = (os.environ.get("CAISHENFOLIO_HTTP_TRUST_ENV") or "1").strip().lower()
    return raw not in {"0", "false", "no", "off"}


def apply_requests_trust_env(trust: bool | None = None) -> bool:
    """Patch requests.Session so new sessions use the desired trust_env flag."""
    enabled = trust_env_enabled() if trust is None else trust
    try:
        import requests
    except Exception:  # noqa: BLE001
        return enabled

    original_init = requests.Session.__init__

    # Avoid double-wrapping
    if getattr(requests.Session.__init__, "_caishenfolio_patched", False):
        requests.Session.__init__._caishenfolio_trust = enabled  # type: ignore[attr-defined]
        return enabled

    def patched_init(self: Any, *args: Any, **kwargs: Any) -> None:
        original_init(self, *args, **kwargs)
        flag = getattr(requests.Session.__init__, "_caishenfolio_trust", enabled)
        self.trust_env = bool(flag)

    patched_init._caishenfolio_patched = True  # type: ignore[attr-defined]
    patched_init._caishenfolio_trust = enabled  # type: ignore[attr-defined]
    requests.Session.__init__ = patched_init  # type: ignore[method-assign]
    return enabled


@contextmanager
def force_direct_connection() -> Iterator[None]:
    """Temporarily ignore proxy environment variables and disable trust_env."""
    saved: dict[str, str] = {}
    for key in _PROXY_KEYS:
        if key in os.environ:
            saved[key] = os.environ.pop(key)

    try:
        import requests

        previous = getattr(requests.Session.__init__, "_caishenfolio_trust", True)
        apply_requests_trust_env(False)
        try:
            yield
        finally:
            apply_requests_trust_env(bool(previous))
    except Exception:  # noqa: BLE001
        yield
    finally:
        os.environ.update(saved)


def is_proxy_or_network_error(exc: BaseException) -> bool:
    text = f"{type(exc).__name__}: {exc}".lower()
    needles = (
        "proxyerror",
        "unable to connect to proxy",
        "proxy",
        "max retries exceeded",
        "connection aborted",
        "remotely closed",
        "remote end closed",
        "timed out",
        "timeout",
        "name resolution",
        "getaddrinfo failed",
        "connection refused",
        "network is unreachable",
        "ssl",
    )
    return any(item in text for item in needles)


def call_with_direct_fallback(fn: Callable[[], T]) -> T:
    """Run fn(); on proxy/network failure, retry once without system proxy.

    Still real network I/O — never invents market data.
    """
    apply_requests_trust_env()
    try:
        return fn()
    except Exception as first:  # noqa: BLE001
        if not is_proxy_or_network_error(first):
            raise
        if not trust_env_enabled():
            # Already forced direct via env; no second policy to try.
            raise
        with force_direct_connection():
            try:
                return fn()
            except Exception as second:  # noqa: BLE001
                raise second from first


def humanize_market_error(exc: BaseException | str) -> str:
    text = str(exc)
    lower = text.lower()
    if "proxy" in lower or "proxyerror" in lower:
        return (
            "无法通过系统代理访问行情源（东方财富等）。"
            "可尝试：1) 关闭无效代理；2) 设置环境变量 CAISHENFOLIO_HTTP_TRUST_ENV=0 后重启分析核心；"
            "3) 确认本机可访问外网。"
            f" 原始错误：{text}"
        )
    if "max retries" in lower or "timed out" in lower or "timeout" in lower:
        return f"访问行情源超时或重试耗尽（网络不稳或被拦截）。原始错误：{text}"
    if "akshare" in lower and ("not" in lower or "没有" in text or "未安装" in text):
        return text
    return f"真实行情获取失败（已 fail-closed，未生成数据）：{text}"
