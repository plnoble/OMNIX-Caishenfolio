"""Central-bank policy rates, fetched rather than hardcoded.

The carry panel used to compute interest-rate differentials from four constants baked into the
source. Constants go stale silently, and a stale rate is worse than no rate: the panel keeps
reporting a differential with full confidence while the real gap has moved. As of 2026-08-01 the
built-in USD figure of 4.5% was off by nearly 90 basis points against the actual EFFR of 3.63%.

So each rate is fetched from the institution that sets or publishes it, cached for a day, and
carries its own ``as_of`` date and source. When a fetch fails the built-in value is still used —
the panel must render — but it is returned with ``stale=True`` so every layer above can say so
instead of implying the number is current.

Parsers are pure functions over the raw payload, so the wire formats stay testable offline.
"""

from __future__ import annotations

import json
import math
import os
import threading
import urllib.request
from dataclasses import dataclass
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any, Callable

from caishenfolio_core.data.policy_rate import FALLBACK_POLICY_RATES, PolicyRate
from caishenfolio_core.market.errors import classify

__all__ = [
    "FALLBACK_POLICY_RATES",
    "PolicyRate",
    "PolicyRateService",
    "parse_china_lpr",
    "parse_ecb_mro",
    "parse_effr",
    "parse_hkma_base_rate",
]

_TIMEOUT_SECONDS = 12
_CACHE_TTL_SECONDS = 24 * 3600

_EFFR_URL = "https://markets.newyorkfed.org/api/rates/unsecured/effr/last/1.json"
_ECB_MRO_URL = (
    "https://data-api.ecb.europa.eu/service/data/FM/D.U2.EUR.4F.KR.MRR_FR.LEV"
    "?lastNObservations=1&format=jsondata"
)
_HKMA_URL = (
    "https://api.hkma.gov.hk/public/market-data-and-statistics/daily-monetary-statistics"
    "/daily-figures-interbank-liquidity?pagesize=1&sortby=end_of_date&sortorder=desc"
)


# --------------------------------------------------------------------------------------
# Parsers — pure, so each wire format is covered without touching the network.
# --------------------------------------------------------------------------------------


def parse_effr(payload: str) -> PolicyRate | None:
    """NY Fed publishes the effective fed funds rate as ``refRates[0].percentRate``."""
    try:
        rows = json.loads(payload).get("refRates") or []
    except (ValueError, AttributeError):
        return None

    for row in rows:
        if not isinstance(row, dict):
            continue
        percent = _to_float(row.get("percentRate"))
        if percent is None:
            continue
        return PolicyRate(
            currency="USD",
            rate=percent / 100.0,
            name="联邦基金有效利率(EFFR)",
            source="纽约联储 markets.newyorkfed.org",
            as_of=_to_date(row.get("effectiveDate")),
            note=_target_range_note(row),
        )
    return None


def _target_range_note(row: dict[str, Any]) -> str:
    low = _to_float(row.get("targetRateFrom"))
    high = _to_float(row.get("targetRateTo"))
    if low is None or high is None:
        return ""
    return f"美联储目标区间 {low:.2f}%–{high:.2f}%。"


def parse_ecb_mro(payload: str) -> PolicyRate | None:
    """ECB answers in SDMX-JSON: the value sits under the single series' observations."""
    try:
        document = json.loads(payload)
        series = document["dataSets"][0]["series"]
        observations = next(iter(series.values()))["observations"]
    except (ValueError, KeyError, IndexError, TypeError, StopIteration):
        return None

    # Observation keys are positional indices into the time dimension.
    try:
        index, values = max(observations.items(), key=lambda kv: int(kv[0]))
        percent = _to_float(values[0])
    except (ValueError, TypeError, IndexError):
        return None
    if percent is None:
        return None

    return PolicyRate(
        currency="EUR",
        rate=percent / 100.0,
        name="欧央行主要再融资利率(MRO)",
        source="欧洲央行 data-api.ecb.europa.eu",
        as_of=_ecb_observation_date(document, index),
    )


def _ecb_observation_date(document: dict[str, Any], index: str) -> date | None:
    try:
        values = document["structure"]["dimensions"]["observation"][0]["values"]
        return _to_date(values[int(index)]["id"])
    except (KeyError, IndexError, ValueError, TypeError):
        return None


def parse_hkma_base_rate(payload: str) -> PolicyRate | None:
    """HKMA's daily figures carry the base rate; the field name has varied across revisions."""
    try:
        records = json.loads(payload)["result"]["records"]
    except (ValueError, KeyError, TypeError):
        return None

    for record in records:
        if not isinstance(record, dict):
            continue
        key = next((k for k in record if "base_rate" in k.lower()), None)
        percent = _to_float(record.get(key)) if key else None
        if percent is None:
            continue
        return PolicyRate(
            currency="HKD",
            rate=percent / 100.0,
            name="金管局基本利率",
            source="香港金管局 api.hkma.gov.hk",
            as_of=_to_date(record.get("end_of_date") or record.get("end_of_month")),
        )
    return None


def parse_china_lpr(rows: list[dict[str, Any]]) -> PolicyRate | None:
    """The 1-year LPR is the PBoC's headline lending benchmark. Rows come from akshare."""
    for row in reversed(rows):
        if not isinstance(row, dict):
            continue
        key = next((k for k in row if "1Y" in str(k).upper() or "1年" in str(k)), None)
        percent = _to_float(row.get(key)) if key else None
        if percent is None:
            continue
        return PolicyRate(
            currency="CNY",
            rate=percent / 100.0,
            name="1年期LPR",
            source="中国人民银行（经 akshare）",
            as_of=_to_date(row.get("TRADE_DATE") or row.get("日期") or row.get("date")),
        )
    return None


# --------------------------------------------------------------------------------------
# Japan
# ---------------------------------------------------------------------------------------


def parse_japan_bank_rate(rows: list[dict[str, Any]]) -> PolicyRate | None:
    """BoJ decisions, latest last. Scheduled meetings appear before they decide, with no value."""
    for row in reversed(rows):
        if not isinstance(row, dict):
            continue
        key = next((k for k in row if "现值" in str(k) or "value" in str(k).lower()), None)
        percent = _to_float(row.get(key)) if key else None
        # A future meeting is already listed with an empty value; that is not a cut to zero.
        if percent is None:
            continue
        return PolicyRate(
            currency="JPY",
            rate=percent / 100.0,
            name="日本央行政策利率",
            source="日本银行（经 akshare）",
            as_of=_to_date(row.get("发布日期") or row.get("日期") or row.get("date")),
        )
    return None


# ---------------------------------------------------------------------------------------
# Fetchers — thin network shells around the parsers.
# --------------------------------------------------------------------------------------


def _fetch(url: str, timeout: int) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "OMNIX-Caishenfolio"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read().decode("utf-8", errors="replace")


def fetch_usd(timeout: int = _TIMEOUT_SECONDS) -> PolicyRate | None:
    return parse_effr(_fetch(_EFFR_URL, timeout))


def fetch_eur(timeout: int = _TIMEOUT_SECONDS) -> PolicyRate | None:
    return parse_ecb_mro(_fetch(_ECB_MRO_URL, timeout))


def fetch_hkd(timeout: int = _TIMEOUT_SECONDS) -> PolicyRate | None:
    return parse_hkma_base_rate(_fetch(_HKMA_URL, timeout))


def fetch_cny(timeout: int = _TIMEOUT_SECONDS) -> PolicyRate | None:
    """LPR has no key-free JSON endpoint, so this one goes through akshare when installed."""
    frame = _akshare_frame("macro_china_lpr")
    return None if frame is None else parse_china_lpr(frame)


def fetch_jpy(timeout: int = _TIMEOUT_SECONDS) -> PolicyRate | None:
    """The BoJ publishes no key-free JSON either; akshare is the available channel."""
    frame = _akshare_frame("macro_japan_bank_rate")
    return None if frame is None else parse_japan_bank_rate(frame)


def _akshare_frame(function_name: str) -> list[dict[str, Any]] | None:
    try:
        import akshare as ak  # type: ignore

        fn = getattr(ak, function_name, None)
        if fn is None:
            return None
        frame = fn()
    except Exception:  # noqa: BLE001
        return None
    if frame is None or getattr(frame, "empty", True):
        return None
    return [{str(k): v for k, v in record.items()} for record in frame.to_dict("records")]


DEFAULT_FETCHERS: dict[str, Callable[[int], PolicyRate | None]] = {
    "USD": fetch_usd,
    "EUR": fetch_eur,
    "HKD": fetch_hkd,
    "CNY": fetch_cny,
    "JPY": fetch_jpy,
}


# --------------------------------------------------------------------------------------
# Service
# --------------------------------------------------------------------------------------


def default_cache_path() -> Path:
    env = (os.environ.get("CAISHENFOLIO_POLICY_RATES_CACHE_PATH") or "").strip()
    if env:
        return Path(env)
    base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    root = base / "Caishenfolio" / "state"
    root.mkdir(parents=True, exist_ok=True)
    return root / "policy_rates.json"


class PolicyRateService:
    """Resolves policy rates, preferring live sources and falling back to labelled constants.

    Rates move at scheduled meetings, not by the minute, so a fetched value is cached for a day.
    """

    def __init__(
        self,
        cache_path: Path | None = None,
        ttl_seconds: int = _CACHE_TTL_SECONDS,
        timeout_seconds: int = _TIMEOUT_SECONDS,
        fetchers: dict[str, Callable[[int], PolicyRate | None]] | None = None,
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._cache_path = cache_path
        self._ttl = ttl_seconds
        self._timeout = timeout_seconds
        self._fetchers = DEFAULT_FETCHERS if fetchers is None else fetchers
        self._now = now or (lambda: datetime.now(timezone.utc))
        self._lock = threading.Lock()

    def rates(self, currencies: list[str]) -> dict[str, PolicyRate]:
        """Returns one entry per requested currency; never omits one, never invents one."""
        wanted = [c.strip().upper() for c in currencies if c and c.strip()]
        with self._lock:
            cached = self._read_cache()
            out: dict[str, PolicyRate] = {}
            changed = False

            for currency in wanted:
                fresh = cached.get(currency)
                if fresh is not None:
                    out[currency] = fresh
                    continue

                fetched = self._fetch_one(currency)
                if fetched is not None:
                    out[currency] = fetched
                    changed = True
                else:
                    out[currency] = self._fallback(currency)

            if changed:
                self._write_cache({**cached, **{k: v for k, v in out.items() if not v.stale}})
            return out

    def _fetch_one(self, currency: str) -> PolicyRate | None:
        fetcher = self._fetchers.get(currency)
        if fetcher is None:
            return None
        try:
            rate = fetcher(self._timeout)
        except Exception as exc:  # noqa: BLE001
            # A rate source being down must not take the panel down with it.
            classify(exc)
            return None
        # A non-positive or absurd rate means the payload changed shape; refuse it.
        if rate is None or not (-0.05 < rate.rate < 0.50):
            return None
        return rate

    def _fallback(self, currency: str) -> PolicyRate:
        built_in = FALLBACK_POLICY_RATES.get(currency)
        if built_in is not None:
            return built_in
        # An unknown currency has no rate at all — say so rather than substitute a neighbour's.
        return PolicyRate(currency, 0.0, "未知利率", "无", None, True, "没有该币种的利率来源。")

    # -- cache -------------------------------------------------------------------------

    def _path(self) -> Path | None:
        if self._cache_path is not None:
            return self._cache_path
        try:
            return default_cache_path()
        except OSError:
            return None

    def _read_cache(self) -> dict[str, PolicyRate]:
        path = self._path()
        if path is None or not path.exists():
            return {}
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
            fetched_at = datetime.fromisoformat(str(document["fetched_at"]))
        except (OSError, ValueError, KeyError, TypeError):
            return {}

        if (self._now() - fetched_at).total_seconds() > self._ttl:
            return {}

        out: dict[str, PolicyRate] = {}
        for currency, item in (document.get("rates") or {}).items():
            try:
                out[str(currency)] = PolicyRate(
                    currency=str(currency),
                    rate=float(item["rate"]),
                    name=str(item.get("name", "")),
                    source=str(item.get("source", "")),
                    as_of=_to_date(item.get("as_of")),
                    stale=False,
                    note=str(item.get("note", "")),
                )
            except (KeyError, TypeError, ValueError):
                continue
        return out

    def _write_cache(self, rates: dict[str, PolicyRate]) -> None:
        path = self._path()
        if path is None or not rates:
            return
        document = {
            "fetched_at": self._now().isoformat(),
            "rates": {k: v.to_dict() for k, v in rates.items()},
        }
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
        except OSError:
            # Losing the cache costs a refetch, nothing more.
            return


def _to_float(value: object) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(str(value).strip().rstrip("%"))
    except (TypeError, ValueError):
        return None
    # NaN reaches here from unfilled table cells; it is a missing value, not a number.
    return number if math.isfinite(number) else None


def _to_date(value: object) -> date | None:
    text = str(value or "").strip()
    if not text:
        return None
    for fmt in ("%Y-%m-%d", "%Y/%m/%d", "%Y%m%d", "%Y-%m"):
        try:
            return datetime.strptime(text[: len(fmt.replace("%Y", "2000"))], fmt).date()
        except ValueError:
            continue
    try:
        return datetime.fromisoformat(text[:10]).date()
    except ValueError:
        return None
