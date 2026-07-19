from __future__ import annotations

import ipaddress


def is_loopback_host(host: str) -> bool:
    if not host or not host.strip():
        return False
    value = host.strip().strip("[]")
    if value.lower() in {"localhost", "127.0.0.1", "::1"}:
        return True
    try:
        return ipaddress.ip_address(value).is_loopback
    except ValueError:
        return False


def ensure_loopback(host: str) -> None:
    if not is_loopback_host(host):
        raise ValueError(
            f"Host '{host}' is not loopback. Managed Analytics Core must bind to loopback only."
        )


def is_denied_wildcard(host: str) -> bool:
    if not host or not host.strip():
        return True
    return host.strip() in {"0.0.0.0", "::", "*"}
