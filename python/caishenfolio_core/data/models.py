from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import datetime
from enum import StrEnum
from typing import Generic, TypeVar

_SYMBOL_RE = re.compile(r"^(?P<exchange>[A-Za-z0-9.]+):(?P<code>[A-Za-z0-9.\-]+)$")

T = TypeVar("T")


class Market(StrEnum):
    ASHARE = "ashare"
    HK = "hk"
    US = "us"
    ETF = "etf"


class AssetClass(StrEnum):
    EQUITY = "equity"
    ETF = "etf"
    INDEX = "index"
    FUND = "fund"


class Adjustment(StrEnum):
    RAW = "raw"
    FORWARD = "forward"
    BACKWARD = "backward"
    UNKNOWN = "unknown"


@dataclass(frozen=True, slots=True)
class SymbolId:
    exchange: str
    code: str

    @property
    def value(self) -> str:
        return f"{self.exchange}:{self.code}"

    @classmethod
    def parse(cls, raw: str) -> SymbolId:
        symbol = cls.try_parse(raw)
        if symbol is None:
            raise ValueError(
                f"Invalid symbol '{raw}'. Expected EXCHANGE:SYMBOL (e.g. SSE:600000, NASDAQ:AAPL)."
            )
        return symbol

    @classmethod
    def try_parse(cls, raw: str | None) -> SymbolId | None:
        if raw is None or not str(raw).strip():
            return None
        match = _SYMBOL_RE.match(str(raw).strip())
        if not match:
            return None
        return cls(
            exchange=match.group("exchange").upper(),
            code=match.group("code").upper(),
        )

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True, slots=True)
class OhlcvBar:
    timestamp_utc: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float
    currency: str
    adjustment: Adjustment
    provider: str
    amount: float | None = None
    provenance: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class ProviderResult(Generic[T]):
    ok: bool
    provider: str
    data: T | None = None
    warnings: tuple[str, ...] = ()
    error: str | None = None
    latency_ms: float | None = None
    from_cache: bool = False

    @classmethod
    def success(
        cls,
        provider: str,
        data: T,
        *,
        warnings: tuple[str, ...] = (),
        latency_ms: float | None = None,
        from_cache: bool = False,
    ) -> ProviderResult[T]:
        return cls(
            ok=True,
            provider=provider,
            data=data,
            warnings=warnings,
            latency_ms=latency_ms,
            from_cache=from_cache,
        )

    @classmethod
    def failure(
        cls,
        provider: str,
        error: str,
        *,
        warnings: tuple[str, ...] = (),
    ) -> ProviderResult[T]:
        return cls(ok=False, provider=provider, error=error, warnings=warnings)
