"""Per-share fundamentals for US and HK names, so their valuation history can be built.

Why this is needed: A-shares get a ready-made daily PE/PB series from upstream, but no free
source publishes the same for US and HK. Baidu's HK/US valuation endpoints return non-JSON
today, and the 亿牛网 HK series stopped updating in July 2022 — a percentile taken against a
distribution that ends four years ago is worse than no percentile.

What does exist is the input: Eastmoney publishes per-share earnings and book value for both
markets, back to 2000 for US quarterly filings. PE and PB are then price ÷ per-share figure,
computed here rather than taken from a vendor.

The one thing that must not be got wrong is *when* the market knew a figure. Applying an annual
result from the day the fiscal year ended would place three months of prices against earnings
nobody had seen yet, and quietly flatter every historical percentile. So each figure carries the
date it became public, and the US filings supply that date directly.
"""

from __future__ import annotations

from datetime import date, timedelta
from typing import Any

from caishenfolio_core.data.fundamentals import PerShareFundamental

__all__ = [
    "HK_PUBLICATION_LAG_DAYS",
    "PerShareFundamental",
    "fetch_hk",
    "fetch_us",
    "parse_hk_annual",
    "parse_us_quarterly",
]

#: HK issuers must publish annual results within three months of the year end, and in practice
#: use most of it. Applied only where the feed gives no announcement date of its own.
HK_PUBLICATION_LAG_DAYS = 90

_QUARTERS_IN_TTM = 4


def parse_us_quarterly(rows: list[dict[str, Any]]) -> list[PerShareFundamental]:
    """US 单季报 rows into a trailing-twelve-month EPS series.

    Quarterly filings carry ``NOTICE_DATE``, so no lag has to be assumed. Four consecutive
    quarters are required: summing fewer would understate earnings and overstate PE.
    """
    parsed: list[tuple[date, date, float]] = []
    for row in rows:
        period_end = _to_date(row.get("REPORT_DATE"))
        announced = _to_date(row.get("NOTICE_DATE"))
        eps = _to_float(row.get("DILUTED_EPS"))
        if eps is None:
            eps = _to_float(row.get("BASIC_EPS"))
        if period_end is None or eps is None:
            continue
        # A filing cannot be public before the period it covers has ended.
        if announced is None or announced < period_end:
            announced = period_end + timedelta(days=HK_PUBLICATION_LAG_DAYS)
        parsed.append((period_end, announced, eps))

    parsed.sort(key=lambda item: item[0])

    out: list[PerShareFundamental] = []
    for index in range(_QUARTERS_IN_TTM - 1, len(parsed)):
        window = parsed[index - _QUARTERS_IN_TTM + 1 : index + 1]
        period_end, announced, _ = parsed[index]
        out.append(
            PerShareFundamental(
                period_end=period_end,
                effective_date=announced,
                eps_ttm=sum(eps for _, _, eps in window),
                bps=None,
                currency="USD",
            )
        )

    return out


def parse_hk_annual(
    rows: list[dict[str, Any]], lag_days: int = HK_PUBLICATION_LAG_DAYS
) -> list[PerShareFundamental]:
    """HK 年度 rows. The feed reports no announcement date, so the lag is assumed and flagged."""
    out: list[PerShareFundamental] = []
    for row in rows:
        period_end = _to_date(row.get("REPORT_DATE"))
        if period_end is None:
            continue

        eps = _to_float(row.get("EPS_TTM"))
        if eps is None:
            eps = _to_float(row.get("DILUTED_EPS")) or _to_float(row.get("BASIC_EPS"))

        bps = _to_float(row.get("BPS"))
        if eps is None and bps is None:
            continue

        out.append(
            PerShareFundamental(
                period_end=period_end,
                effective_date=period_end + timedelta(days=lag_days),
                eps_ttm=eps,
                bps=bps,
                currency=str(row.get("CURRENCY") or "HKD").upper(),
                effective_date_estimated=True,
            )
        )

    out.sort(key=lambda item: item.period_end)
    return out


def fetch_us(code: str) -> list[PerShareFundamental] | None:
    frame = _akshare_frame("stock_financial_us_analysis_indicator_em", code, "单季报")
    return None if frame is None else parse_us_quarterly(frame)


def fetch_hk(code: str) -> list[PerShareFundamental] | None:
    frame = _akshare_frame("stock_financial_hk_analysis_indicator_em", code, "年度")
    return None if frame is None else parse_hk_annual(frame)


def _akshare_frame(function_name: str, symbol: str, indicator: str) -> list[dict[str, Any]] | None:
    try:
        import akshare as ak  # type: ignore

        fn = getattr(ak, function_name, None)
        if fn is None:
            return None
        frame = fn(symbol=symbol, indicator=indicator)
    except Exception:  # noqa: BLE001
        return None
    if frame is None or getattr(frame, "empty", True):
        return None
    return [{str(k): v for k, v in record.items()} for record in frame.to_dict("records")]


def _to_float(value: object) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(str(value).strip())
    except (TypeError, ValueError):
        return None
    # Blank cells arrive as NaN; that is a missing figure, not a zero.
    return number if number == number and abs(number) != float("inf") else None


def _to_date(value: object) -> date | None:
    text = str(value or "").strip()
    if len(text) < 10:
        return None
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        return None
