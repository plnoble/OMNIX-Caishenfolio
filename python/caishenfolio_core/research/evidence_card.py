"""The evidence card (plan §9): everything a person needs to judge whether the case for a
trade is well-made — and nothing that decides the trade for them.

The card's contract with its reader:

- Every claim is traceable: each item carries the source, the data date, and either a
  formula, a rule, or a rule reference. Nothing is asserted from thin air.
- Missing data is *stated*, never filled in by guessing (the plan's "缺失数据不得由大模型
  补写" — this module has no model in it at all; it assembles facts from market channels).
- The conclusion is a status — 证据偏正面 / 证据冲突 / 风险超限 / 证据不足 — derived
  mechanically from the evidence counts and the user's own limits. It is never a buy/sell.
- Banned wording never appears: the forbidden phrases have a test guarding them.

Structure follows the plan's nine sections: data quality, valuation, fundamentals, market
behaviour, historical check, portfolio impact, evidence for/against/unknown, invalidation
conditions, conclusion status.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from typing import Any

_DISCLAIMER = (
    "研究/模拟结论，非投资建议。证据卡只整理事实与历史，不预测未来，不输出买卖指令；"
    "历史回测表现不代表未来收益。"
)


def _quarter_index_from_iso(iso_date: str) -> int | None:
    """An ISO date's quarter as a continuous index (2025-07-19 → 2025*4+3)."""
    from datetime import date as _date

    try:
        d = _date.fromisoformat(iso_date)
        return d.year * 4 + (d.month - 1) // 3 + 1
    except (ValueError, TypeError):
        return None


def _quarter_index(period: str) -> int | None:
    """`YYYYQn` → year * 4 + quarter (a continuous index); None when unparseable."""
    text = (period or "").strip().upper()
    if len(text) == 6 and text[:4].isdigit() and text[4] == "Q" and text[5] in "1234":
        return int(text[:4]) * 4 + int(text[5])
    return None


def _is_iso_date(value: str) -> bool:
    """Strict full-format yyyy-mm-dd (recheck-2 F-03).

    ``date.fromisoformat`` alone also accepts the compact ``20250719`` form; that string
    then joined date ``max()`` comparisons and could outrank real ``2025-07-20`` by
    LEXICOGRAPHY — the headline picked an older date. The regex rejects compact forms;
    the round-trip equality check rejects nonsense like ``2025-2-29`` style edge slips.
    """
    import re as _re
    from datetime import date as _date

    if not _re.fullmatch(r"\d{4}-\d{2}-\d{2}", value or ""):
        return False
    try:
        return _date.fromisoformat(value).isoformat() == value
    except (ValueError, TypeError):
        return False


def _bar_date(bar: dict[str, Any]) -> str:
    """The bar channel stamps rows ``timestamp_utc``; accept both spellings."""
    value = bar.get("date") or bar.get("timestamp_utc") or ""
    return str(value)[:10]

#: The plan's banned phrases (§9 禁止项). The wording guard test walks every string the
#: card can produce and asserts none of these ever appear — including as substrings, which
#: is how "这不等于该买" once smuggled "该买" into a disclaimer.
BANNED_PHRASES: tuple[str, ...] = (
    "必涨",
    "稳赚",
    "最佳买点",
    "抄底",
    "建议买入",
    "建议卖出",
    "该买",
    "该卖",
    "买入依据",
    "卖出依据",
)


@dataclass(frozen=True, slots=True)
class EvidenceItem:
    """One claim with its provenance. ``basis`` says why we believe it."""

    text: str
    stance: str  # positive | negative | neutral | unknown
    basis: str  # source + date + formula/rule, human-readable

    def to_dict(self) -> dict[str, Any]:
        return {"text": self.text, "stance": self.stance, "basis": self.basis}


@dataclass(frozen=True, slots=True)
class Conclusion:
    """The card's verdict — a status about the *evidence*, never a trade instruction."""

    status: str  # positive | conflicting | risk_exceeded | insufficient
    summary: str
    reasons: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {"status": self.status, "summary": self.summary, "reasons": list(self.reasons)}


@dataclass(frozen=True, slots=True)
class DataQualityBlock:
    """Plan §9 section 1: where the numbers came from and whether they agree."""

    provider: str
    data_as_of: str
    bar_count: int
    quote_sources: int
    quote_spread_pct: float | None
    adjustment: str
    missing: list[str] = field(default_factory=list)
    conflicts: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    #: Each channel's own last-data date ("" when the channel is missing) — the card's top
    #: date is the freshest *actual* data, never the request's end parameter (P1-11).
    channel_as_of: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "provider": self.provider,
            "data_as_of": self.data_as_of,
            "bar_count": self.bar_count,
            "quote_sources": self.quote_sources,
            "quote_spread_pct": self.quote_spread_pct,
            "adjustment": self.adjustment,
            "missing": list(self.missing),
            "conflicts": list(self.conflicts),
            "warnings": list(self.warnings),
            "channel_as_of": dict(self.channel_as_of),
        }


@dataclass(frozen=True, slots=True)
class PortfolioImpactBlock:
    """Plan §9 section 6: what the candidate amount would do to the portfolio's shape.

    Every weight is a fact computed from the user's own numbers; the block contains no
    opinion about whether a change is good.
    """

    currency: str
    candidate_amount: float
    candidate_weight_after: float | None
    single_position_weight_after: float | None
    single_position_weight_before: float | None
    single_position_limit_pct: float | None
    cash_weight_after: float | None
    region_weight_after: dict[str, float] = field(default_factory=dict)
    currency_weight_after: dict[str, float] = field(default_factory=dict)
    #: Baselines so the UI can show before → after; empty when the after-values failed.
    region_weight_before: dict[str, float] = field(default_factory=dict)
    currency_weight_before: dict[str, float] = field(default_factory=dict)
    cash_weight_before: float | None = None
    #: The currency the funding side pays in — a first-class fact of the simulation.
    funding_currency: str = ""
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "currency": self.currency,
            "candidate_amount": self.candidate_amount,
            "candidate_weight_after": self.candidate_weight_after,
            "single_position_weight_after": self.single_position_weight_after,
            "single_position_weight_before": self.single_position_weight_before,
            "single_position_limit_pct": self.single_position_limit_pct,
            "cash_weight_after": self.cash_weight_after,
            "region_weight_after": dict(self.region_weight_after),
            "currency_weight_after": dict(self.currency_weight_after),
            "region_weight_before": dict(self.region_weight_before),
            "currency_weight_before": dict(self.currency_weight_before),
            "cash_weight_before": self.cash_weight_before,
            "funding_currency": self.funding_currency,
            "notes": list(self.notes),
        }


@dataclass(frozen=True, slots=True)
class EvidenceCard:
    """The assembled card. Sections may be empty-but-explained; nothing is invented."""

    symbol: str
    as_of: str
    data_quality: DataQualityBlock
    valuation: dict[str, Any]
    fundamentals: dict[str, Any]
    market_behaviour: dict[str, Any]
    historical_check: dict[str, Any]
    portfolio_impact: PortfolioImpactBlock | None
    evidence_for: list[EvidenceItem]
    evidence_against: list[EvidenceItem]
    unknowns: list[EvidenceItem]
    invalidation: list[str]
    conclusion: Conclusion
    #: "buy" or "sell" — the direction the candidate amount refers to. Evidence stances are
    #: already expressed relative to this direction; the UI labels them accordingly.
    side: str = "buy"
    #: The freshest MARKET-data date (bars/quote/valuation), strictly ISO (remediation WP4).
    market_as_of: str = ""
    disclaimer: str = _DISCLAIMER

    def to_dict(self) -> dict[str, Any]:
        return {
            "symbol": self.symbol,
            "as_of": self.as_of,
            "market_as_of": self.market_as_of,
            "side": self.side,
            "data_quality": self.data_quality.to_dict(),
            "valuation": self.valuation,
            "fundamentals": self.fundamentals,
            "market_behaviour": self.market_behaviour,
            "historical_check": self.historical_check,
            "portfolio_impact": self.portfolio_impact.to_dict() if self.portfolio_impact else None,
            "evidence_for": [item.to_dict() for item in self.evidence_for],
            "evidence_against": [item.to_dict() for item in self.evidence_against],
            "unknowns": [item.to_dict() for item in self.unknowns],
            "invalidation": list(self.invalidation),
            "conclusion": self.conclusion.to_dict(),
            "disclaimer": self.disclaimer,
        }


# --------------------------------------------------------------------------------------
# Section builders. Each takes the raw channel payloads and returns its block plus the
# evidence items it contributes — the evidence lists stay honest because each item is
# created where the fact is read, next to its basis string.
# --------------------------------------------------------------------------------------


def build_data_quality(
    bars_payload: dict[str, Any],
    quote_payload: dict[str, Any] | None,
    valuation_payload: dict[str, Any] | None,
    financials_payload: dict[str, Any] | None,
    adjustment: str,
    price_tolerance_pct: float = 2.0,
) -> tuple[DataQualityBlock, list[EvidenceItem]]:
    """Data quality is a gate, never a directional vote (P1-09).

    Sources agreeing means the *number* is trustworthy — it says nothing about whether buying
    is a good idea, so it can never count as positive evidence. Disagreement beyond the
    caller's tolerance is a reliability warning, likewise not a directional fact.
    """
    bars = bars_payload.get("data") or []
    provider = str(bars_payload.get("provider") or "未知")
    warnings = [str(w) for w in (bars_payload.get("warnings") or [])]
    last_date = _bar_date(bars[-1]) if bars else ""

    quote_sources = 0
    spread: float | None = None
    conflicts: list[str] = []
    quote_as_of = ""
    if quote_payload and quote_payload.get("ok"):
        data = quote_payload.get("data") or {}
        quote_sources = int(data.get("source_count") or 0)
        raw_spread = data.get("spread_pct")
        spread = float(raw_spread) if raw_spread is not None else None
        quote_as_of = str(data.get("as_of") or "")
        # Only a spread beyond the caller's tolerance is a conflict; "在容差内" must mean it.
        if quote_sources >= 2 and spread is not None and spread > price_tolerance_pct:
            conflicts.append(
                f"{quote_sources} 个价格源相差 {spread:.2g}%（{data.get('sources') or '来源未列'}），"
                f"超过容差 {price_tolerance_pct:g}%，已取中位数，市值可能不准。"
            )

    missing: list[str] = []
    if not bars:
        missing.append("K线")
    if not (quote_payload and quote_payload.get("ok")):
        missing.append("最新价")
    if not (valuation_payload and valuation_payload.get("ok")):
        missing.append("估值历史")
    if not (financials_payload and financials_payload.get("ok")):
        missing.append("财务摘要")

    block = DataQualityBlock(
        provider=provider,
        data_as_of=last_date,
        bar_count=len(bars),
        quote_sources=quote_sources,
        quote_spread_pct=spread,
        adjustment=adjustment,
        missing=missing,
        conflicts=conflicts,
        warnings=warnings,
        channel_as_of={
            "bars": last_date,
            "quote": quote_as_of,
            "valuation": str((valuation_payload or {}).get("data", {}).get("as_of") or "")
            if valuation_payload and valuation_payload.get("ok") else "",
            "financials": str((financials_payload or {}).get("data", [{}])[-1].get("period") or "")
            if financials_payload and financials_payload.get("ok") and financials_payload.get("data") else "",
        },
    )

    items: list[EvidenceItem] = []
    if quote_sources >= 2:
        within = spread is not None and spread <= price_tolerance_pct
        items.append(
            EvidenceItem(
                text=f"最新价经 {quote_sources} 个数据源交叉核对"
                + (f"，价差 {spread:.2g}% 在容差 {price_tolerance_pct:g}% 内" if within else ""),
                stance="neutral",  # trustworthy data is a gate, not a reason to buy (P1-09)
                basis=f"latest_quote 交叉核对（{provider}，{last_date}）",
            )
        )
    for conflict in conflicts:
        items.append(EvidenceItem(
            text=conflict,
            stance="neutral",  # unreliable data warns; it still votes on nothing (P1-09)
            basis=f"latest_quote 交叉核对（{provider}，{last_date}）",
        ))
    return block, items


def build_valuation(valuation_payload: dict[str, Any] | None) -> tuple[dict[str, Any], list[EvidenceItem]]:
    """Plan §9 section 2: only verifiable metrics, each with its convention stated."""
    if not (valuation_payload and valuation_payload.get("ok")):
        return {"available": False, "reason": "估值历史不可用（数据源不支持或未取到）。"}, [
            EvidenceItem(
                text="估值水平未知（PE/PB 分位无法计算）",
                stance="unknown",
                basis="valuation_history 通道未返回数据",
            )
        ]

    data = valuation_payload.get("data") or {}
    readings = valuation_payload.get("readings") or []
    metrics = []
    items: list[EvidenceItem] = []
    for reading in readings:
        name = str(reading.get("metric") or "")
        current = reading.get("current")
        band = reading.get("band")
        sample = reading.get("sample_size") or 0
        if current is None:
            metrics.append({"metric": name, "current": None, "band": None, "sample_size": sample})
            continue

        entry = {
            "metric": name,
            "current": current,
            "band": band,
            "percentile": reading.get("percentile"),
            "sample_size": sample,
            # The convention note comes from the interpretation layer, verbatim.
            "note": reading.get("plain") or "",
        }
        metrics.append(entry)
        if sample and sample < 60:
            items.append(EvidenceItem(
                text=f"{name} 分位样本仅 {sample} 点，位置结论不可靠",
                stance="unknown",
                basis=f"valuation_history（样本量 {sample} < 60）",
            ))
        elif band:
            items.append(EvidenceItem(
                text=f"{name} 处于{band}（当前 {current}，历史分位 {reading.get('percentile')}%）",
                stance="neutral",  # position is a fact; the reading layer forbids "cheap=buy"
                basis=f"valuation_history 分位（{valuation_payload.get('provider')}）",
            ))

    return {"available": True, "metrics": metrics, "as_of": data.get("as_of")}, items


def build_fundamentals(financials_payload: dict[str, Any] | None) -> tuple[dict[str, Any], list[EvidenceItem]]:
    """Plan §9 section 3: revenue/profit/cash/debt trends — as filed, without a verdict."""
    if not (financials_payload and financials_payload.get("ok")):
        return {"available": False, "reason": "财务摘要不可用（数据源不支持或未取到）。"}, [
            EvidenceItem(
                text="基本面趋势未知（收入/利润/现金流未取到）",
                stance="unknown",
                basis="financial_summary 通道未返回数据",
            )
        ]

    rows = financials_payload.get("data") or []
    periods = [dict(row) for row in rows]
    items: list[EvidenceItem] = []

    growth_series = [row.get("revenue_growth") for row in rows if row.get("revenue_growth") is not None]
    profit_series = [row.get("profit_growth") for row in rows if row.get("profit_growth") is not None]
    if len(growth_series) >= 3:
        if all(g > 0 for g in growth_series):
            items.append(EvidenceItem(
                text=f"收入连续 {len(growth_series)} 期为正增长（最新 {growth_series[-1]:.1%}）",
                stance="positive",
                basis=f"financial_summary：{rows[-1].get('period')} 等各期收入同比",
            ))
        elif all(g < 0 for g in growth_series):
            items.append(EvidenceItem(
                text=f"收入连续 {len(growth_series)} 期为负增长（最新 {growth_series[-1]:.1%}）",
                stance="negative",
                basis=f"financial_summary：{rows[-1].get('period')} 等各期收入同比",
            ))
    else:
        items.append(EvidenceItem(
            text="收入趋势的期数不足，方向无法判断",
            stance="unknown",
            basis=f"financial_summary：仅 {len(growth_series)} 期含同比",
        ))

    if len(profit_series) >= 3 and all(p < 0 for p in profit_series):
        items.append(EvidenceItem(
            text=f"净利润连续 {len(profit_series)} 期为负增长",
            stance="negative",
            basis=f"financial_summary：{rows[-1].get('period')} 等各期利润同比",
        ))

    return {
        "available": True,
        "periods": periods,
        "latest_period": rows[-1].get("period") if rows else None,
    }, items


def build_market_behaviour(bars_payload: dict[str, Any]) -> tuple[dict[str, Any], list[EvidenceItem]]:
    """Plan §9 section 4: trend, volatility, drawdown, activity — numbers plus basis."""
    bars = bars_payload.get("data") or []
    if len(bars) < 30:
        return {
            "available": False,
            "reason": f"K线仅 {len(bars)} 根，不足以计算趋势/波动/回撤。",
        }, [
            EvidenceItem(
                text=f"市场行为指标未知（K线不足：{len(bars)} 根 < 30）",
                stance="unknown",
                basis="bars 通道样本量不足",
            )
        ]

    closes = [float(bar["close"]) for bar in bars]
    volumes = [float(bar.get("volume") or 0) for bar in bars]
    last_date = _bar_date(bars[-1])

    # Trend: last close vs the 60-day mean, stated as a fact.
    window = closes[-60:]
    mean60 = sum(window) / len(window)
    trend_pct = (closes[-1] - mean60) / mean60

    # Volatility: stdev of daily returns, annualized — the formula is in the basis.
    returns = [(closes[i] - closes[i - 1]) / closes[i - 1] for i in range(1, len(closes))]
    mean_ret = sum(returns) / len(returns)
    var = sum((r - mean_ret) ** 2 for r in returns) / max(len(returns) - 1, 1)
    daily_vol = var**0.5
    annual_vol = daily_vol * (252**0.5)

    # Max drawdown within the window.
    peak = closes[0]
    max_dd = 0.0
    for close in closes:
        peak = max(peak, close)
        max_dd = min(max_dd, (close - peak) / peak)

    # Activity: latest volume vs the 60-day mean.
    vol_window = volumes[-60:]
    mean_vol = sum(vol_window) / len(vol_window) if vol_window else 0
    activity_ratio = (volumes[-1] / mean_vol) if mean_vol > 0 else None

    block = {
        "available": True,
        "trend_vs_mean60_pct": round(trend_pct * 100, 2),
        "annualized_volatility_pct": round(annual_vol * 100, 2),
        "max_drawdown_pct": round(max_dd * 100, 2),
        "volume_vs_mean60": round(activity_ratio, 2) if activity_ratio is not None else None,
        "sample": len(closes),
        "as_of": last_date,
    }

    items = [
        EvidenceItem(
            text=f"现价高于 60 日均价 {trend_pct:.1%}" if trend_pct > 0 else f"现价低于 60 日均价 {-trend_pct:.1%}",
            stance="neutral",
            basis=f"收盘价 vs 60 日均值（bars，{last_date}，样本 {len(closes)}）",
        ),
        EvidenceItem(
            text=f"年化波动率 {annual_vol:.1%}，窗口内最大回撤 {max_dd:.1%}",
            stance="neutral",
            basis="日收益率标准差 × √252；窗口内峰谷最大跌幅（bars）",
        ),
    ]
    if activity_ratio is not None and activity_ratio >= 3:
        items.append(EvidenceItem(
            text=f"最新成交量约为 60 日均量的 {activity_ratio:.1f} 倍，事件驱动可能性高",
            stance="neutral",
            basis=f"最新量 ÷ 60 日均量（bars，{last_date}）",
        ))
    return block, items


def build_historical_check(bars_payload: dict[str, Any]) -> tuple[dict[str, Any], list[EvidenceItem]]:
    """Plan §9 section 5: what happened after comparable positions — with the caveat."""
    bars = bars_payload.get("data") or []
    if len(bars) < 120:
        return {
            "available": False,
            "reason": f"K线仅 {len(bars)} 根（<120），历史条件检验样本不足。",
            "caveat": "样本不足时不做历史推断，也不猜测。",
        }, [
            EvidenceItem(
                text="历史条件检验不可用（样本 <120 根）",
                stance="unknown",
                basis="bars 通道样本量不足；不足即停止，不用短样本凑数",
            )
        ]

    closes = [float(bar["close"]) for bar in bars]
    last_date = _bar_date(bars[-1])
    percentile = (sum(1 for c in closes[:-20] if c <= closes[-1]) / max(len(closes) - 20, 1)) * 100

    # Forward 20-day outcomes from the 20 closest historical prices (absolute distance).
    candidates = sorted(range(len(closes) - 20), key=lambda i: abs(closes[i] - closes[-1]))[:20]
    outcomes = [(closes[i + 20] - closes[i]) / closes[i] for i in candidates]
    outcomes.sort()
    median = outcomes[len(outcomes) // 2]

    # Out-of-sample-style stability: the same conditional question answered on the older half
    # and the newer half of history. If the two halves disagree, the pattern did not persist —
    # that is exactly the kind of finding a train/test split is for, and it needs no strategy.
    half = len(closes) // 2
    older_median = _median_forward(closes[:half], closes[-1])
    newer_median = _median_forward(closes[half:], closes[-1])
    stability_agrees = (
        older_median is not None and newer_median is not None
        and (older_median == 0) == (newer_median == 0)
        and (older_median > 0) == (newer_median > 0)
    )

    # Parameter sensitivity: the comparable-window size is a choice; if widening it flips the
    # direction, the "pattern" is an artifact of the parameter, not a fact about the asset.
    median_10 = _median_forward(closes, closes[-1], nearest=10)
    median_30 = _median_forward(closes, closes[-1], nearest=30)
    sensitivity_agrees = (
        median_10 is not None and median_30 is not None
        and (median_10 > 0) == (median_30 > 0)
    )

    block = {
        "available": True,
        "current_price_percentile": round(percentile, 1),
        "comparable_sample": len(outcomes),
        "forward_window_days": 20,
        "median_forward_return_pct": round(median * 100, 2),
        "worst_forward_return_pct": round(outcomes[0] * 100, 2),
        "best_forward_return_pct": round(outcomes[-1] * 100, 2),
        "stability": {
            "older_half_median_pct": round(older_median * 100, 2) if older_median is not None else None,
            "newer_half_median_pct": round(newer_median * 100, 2) if newer_median is not None else None,
            "agrees": stability_agrees,
        },
        "sensitivity": {
            "nearest_10_median_pct": round(median_10 * 100, 2) if median_10 is not None else None,
            "nearest_30_median_pct": round(median_30 * 100, 2) if median_30 is not None else None,
            "agrees": sensitivity_agrees,
        },
        "as_of": last_date,
        "caveat": (
            "条件分布只描述「历史上从类似价位出发 20 天后发生过什么」，样本有限、"
            "不含未来，不能当作操作依据；未含交易成本。"
        ),
    }
    items = [
        EvidenceItem(
            text=(
                f"历史上 {len(outcomes)} 次与当前价位相近的时点，其后 20 个交易日"
                f"收益中位数 {median:.1%}，最差 {outcomes[0]:.1%}，最好 {outcomes[-1]:.1%}"
            ),
            stance="neutral",
            basis="bars：与现价绝对距离最近的 20 个历史时点，各自后 20 日收益",
        ),
        EvidenceItem(
            text=(
                ("前后两半历史各自的条件收益中位数方向一致"
                 if stability_agrees
                 else "前后两半历史各自的条件收益中位数方向不一致——该「规律」未经得起时间上的样本外检验")
                + f"（前半 {older_median:.1%} / 后半 {newer_median:.1%}）"
            ),
            stance="neutral",
            basis="bars：按时间对半切分，各自独立重算条件分布（训练/检验式拆分）",
        ),
        EvidenceItem(
            text=(
                ("把可比样本从 20 放宽到 10/30，条件收益方向不变"
                 if sensitivity_agrees
                 else "把可比样本从 20 放宽到 10/30，条件收益方向会翻转——结果对参数敏感，警惕过拟合")
                + f"（10 样本 {median_10:.1%} / 30 样本 {median_30:.1%}）"
            ),
            stance="neutral",
            basis="bars：同一条件、不同可比窗口大小的重算对比",
        ),
    ]
    return block, items


def _median_forward(
    closes: list[float], reference: float, nearest: int = 10, window: int = 20
) -> float | None:
    """Median forward return of the ``nearest`` historical prices to ``reference``."""
    usable = len(closes) - window
    if usable < 1:
        return None
    candidates = sorted(range(usable), key=lambda i: abs(closes[i] - reference))[:nearest]
    outcomes = sorted((closes[i + window] - closes[i]) / closes[i] for i in candidates)
    return outcomes[len(outcomes) // 2]


def build_portfolio_impact(
    candidate_amount: float,
    candidate_currency: str,
    portfolio_value: float,
    portfolio_currency: str,
    current_weight_of_symbol: float,
    single_position_limit: float | None,
    cash_available: float,
    region_weights: dict[str, float] | None,
    currency_weights: dict[str, float] | None,
    side: str = "buy",
    funding: str = "existing_cash",
    instrument_region: str = "",
    instrument_currency: str = "",
    funding_currency: str = "",
) -> PortfolioImpactBlock:
    """Plan §9 section 6: before/after weights from the user's own numbers.

    ``funding`` decides the denominator and is explicit because the two readings differ in
    every number (P1-01): ``"existing_cash"`` keeps the total (money moves inside the
    portfolio, cash falls); ``"external"`` grows the total by the amount and leaves the
    existing cash untouched. For ``side="sell"`` the amount is sold out of the holding — the
    weight goes down, the cash line goes up, and the plan's quality gate requires the tax and
    opportunity-cost lines to be spelled out. Region and currency weights are only produced
    when the caller actually has them — absent inputs yield absent fields, never zeros.
    """
    notes: list[str] = []
    base_value = portfolio_value
    fx_note = ""
    amount_in_base = candidate_amount
    if candidate_currency != portfolio_currency:
        # No silent conversion: without a rate the block says so and the weights are None.
        amount_in_base = float("nan")

    if side == "sell":
        return _sell_impact(
            amount_in_base, candidate_currency, portfolio_currency, portfolio_value,
            current_weight_of_symbol, cash_available, notes,
            region_weights=region_weights,
            currency_weights=currency_weights,
            instrument_region=instrument_region,
            instrument_currency=instrument_currency,
        )

    valid = amount_in_base == amount_in_base
    if funding == "external":
        total_after = base_value + (amount_in_base if valid else 0)
        cash_after = (cash_available / total_after) if total_after > 0 else None
        notes.append("资金来源：外部新增资金——总资产增加，现有现金不变。")
    else:
        total_after = base_value
        cash_after = (
            max(cash_available - amount_in_base, 0) / total_after
            if total_after > 0 and valid
            else None
        )
        notes.append("资金来源：账本现有现金——总资产不变，现金减少。")

    candidate_weight = (
        amount_in_base / total_after if total_after > 0 and valid else None
    )
    # R3-01: the OLD position's weight must be re-based on the SAME post-trade denominator.
    # External money grows the total, so the old holding dilutes BEFORE the new amount is
    # added — "old% + new/total" double-counted the old stake and overstated concentration
    # (20% holding + 10 000 external on 100 000 read as 29.09% instead of 27.27%, tripping
    # a 28% limit that was never breached). Existing-cash buys keep the old total, where
    # the old formula happened to be right.
    single_after = (
        (current_weight_of_symbol * base_value + amount_in_base) / total_after
        if candidate_weight is not None and total_after > 0 and valid
        else None
    )

    if candidate_currency != portfolio_currency:
        notes.append(
            f"候选金额以 {candidate_currency} 计，本位币为 {portfolio_currency}；"
            "未提供汇率，占比未折算（避免伪造精度）。"
        )
    if valid and funding != "external" and amount_in_base > cash_available:
        notes.append(
            f"候选金额 {amount_in_base:,.0f} {portfolio_currency} 超过可用现金 "
            f"{cash_available:,.0f} {portfolio_currency}，需要先入金或减少金额。"
        )

    # Region/currency impact (remediation WP1): the after-weights must CONSERVE — every
    # dimension still sums to 1. A buy moves weight between slices (cash→asset region,
    # funding currency→asset currency); external money scales every old bucket down first.
    # Inputs that do not conserve, or a move that would go negative, fail closed with the
    # reason — never a trimmed-to-zero guess.
    region_after: dict[str, float] = {}
    currency_after: dict[str, float] = {}
    if candidate_weight is not None:
        valid_regions = _validated_distribution(region_weights, "地区", notes)
        valid_currencies = _validated_distribution(currency_weights, "货币", notes)
        region_after = _region_after(
            valid_regions, instrument_region, candidate_weight, funding, notes)
        currency_after = _currency_after(
            valid_currencies, instrument_currency, funding_currency,
            candidate_weight, funding, notes)

    cash_before = (
        cash_available / base_value if funding == "external" and base_value > 0
        else (cash_available / base_value if base_value > 0 else None)
    )
    return PortfolioImpactBlock(
        currency=portfolio_currency,
        candidate_amount=candidate_amount,
        candidate_weight_after=candidate_weight,
        single_position_weight_after=single_after,
        single_position_weight_before=current_weight_of_symbol,
        single_position_limit_pct=single_position_limit * 100 if single_position_limit else None,
        cash_weight_after=cash_after,
        cash_weight_before=cash_before,
        region_weight_after=region_after,
        currency_weight_after=currency_after,
        region_weight_before=dict(region_weights) if region_weights else {},
        currency_weight_before=dict(currency_weights) if currency_weights else {},
        funding_currency=funding_currency,
        notes=notes + ([fx_note] if fx_note else []),
    )


def _sell_impact(
    amount_in_base: float,
    candidate_currency: str,
    portfolio_currency: str,
    portfolio_value: float,
    current_weight_of_symbol: float,
    cash_available: float,
    notes: list[str],
    region_weights: dict[str, float] | None = None,
    currency_weights: dict[str, float] | None = None,
    instrument_region: str = "",
    instrument_currency: str = "",
) -> PortfolioImpactBlock:
    """Sell-side impact (plan §9 quality gate): taxes, opportunity cost, and the weight change.

    Selling moves money from the holding to the cash line, so the portfolio total is roughly
    unchanged and what changes is the *shape*: the single-position weight falls, cash rises.
    Fees and any applicable tax make the received amount smaller than the nominal one — the
    exact figures live on the user's broker statement, so they stay a stated unknown here.
    """
    valid = amount_in_base == amount_in_base
    if valid and current_weight_of_symbol <= 0:
        notes.append("账本里该标的当前没有持仓占比，无法按卖出模拟。")
        valid = False

    # The sellable weight is the ONE number every after-state uses (recheck P1-02):
    # clamping only the position while cash grew by the full request produced a
    # 140% cash weight on over-sells.
    candidate_weight = None
    single_after = None
    cash_after = None
    sold_amount = None
    if valid and portfolio_value > 0:
        requested_weight = amount_in_base / portfolio_value
        candidate_weight = min(requested_weight, current_weight_of_symbol)
        if requested_weight > current_weight_of_symbol:
            notes.append(
                f"候选卖出金额约占组合 {requested_weight * 100:.1f}%，"
                f"超过该标的当前占比 {current_weight_of_symbol * 100:.1f}%——按可卖的 "
                f"{current_weight_of_symbol * 100:.1f}% 计算。"
            )
        sold_amount = candidate_weight * portfolio_value
        single_after = max(current_weight_of_symbol - candidate_weight, 0.0)
        cash_after = (cash_available + sold_amount) / portfolio_value

    # The plan's quality gate for sell analysis: fees, opportunity cost, portfolio impact.
    notes.append("卖出方向必须核对：佣金与印花税等费用以券商账单为准，本卡未计入，到手金额会小于名义金额。")
    if candidate_currency != portfolio_currency:
        notes.append(
            f"候选金额以 {candidate_currency} 计，本位币为 {portfolio_currency}；"
            "未提供汇率，占比未折算（避免伪造精度）。"
        )
    notes.append(
        "机会成本：卖出后不再承担该标的的涨跌，资金转为现金；重新买回还会有新的费用。"
    )

    # Sell after-weights (remediation WP1): the asset's region moves to cash; the proceeds
    # stay in the instrument's currency (no silent conversion), so the currency slice of the
    # instrument is unchanged — the money just changed shape inside it.
    region_after: dict[str, float] = {}
    currency_after: dict[str, float] = {}
    if candidate_weight is not None and valid:
        # Shared input gate (recheck-2 F-02): finite/non-negative/sum-1 on BOTH dimensions —
        # the sell path previously copied raw dicts and a 120% currency input went straight
        # to the UI as a confident-looking "after" distribution.
        valid_regions = _validated_distribution(region_weights, "地区", notes)
        valid_currencies = _validated_distribution(currency_weights, "货币", notes)
        if valid_regions:
            if not instrument_region:
                notes.append("未知标的所属地区：地区占比的「交易后」值未计算。")
            elif valid_regions.get(instrument_region, 0.0) < current_weight_of_symbol - _CONSERVATION_TOLERANCE:
                # Inconsistency check against the FULL position (recheck-2 F-02): comparing
                # against the clipped candidate_weight let "hold 20%, region slice 10%, sell 5%"
                # through — after which the position still held 15% inside a 5% region.
                notes.append(
                    f"地区切片「{instrument_region}」只有 {valid_regions.get(instrument_region, 0.0) * 100:.1f}%，"
                    f"小于该标的当前持仓 {current_weight_of_symbol * 100:.1f}%：输入不自洽，交易后占比未计算。")
            else:
                region_after = valid_regions
                region_after[instrument_region] = region_after[instrument_region] - candidate_weight
                region_after["cash"] = region_after.get("cash", 0.0) + candidate_weight
        if valid_currencies and instrument_currency:
            # Selling swaps asset for cash in the SAME currency: the slice total is unchanged.
            # The position must actually fit inside its currency slice, same rule as regions.
            if valid_currencies.get(instrument_currency, 0.0) < current_weight_of_symbol - _CONSERVATION_TOLERANCE:
                notes.append(
                    f"货币切片「{instrument_currency}」只有 "
                    f"{valid_currencies.get(instrument_currency, 0.0) * 100:.1f}%，"
                    f"小于该标的当前持仓 {current_weight_of_symbol * 100:.1f}%：输入不自洽，交易后占比未计算。")
            else:
                currency_after = dict(valid_currencies)
        elif valid_currencies:
            notes.append("未知标的计价货币：货币占比的「交易后」值未计算。")

        # Output invariant (recheck-2 F-02): any non-empty after-distribution we are about
        # to hand to the UI must itself be finite, non-negative and sum to 1 — fail closed.
        for label, dist in (("地区", region_after), ("货币", currency_after)):
            if not dist:
                continue
            import math
            if any(not math.isfinite(v) or v < -_CONSERVATION_TOLERANCE for v in dist.values()) or (
                abs(sum(dist.values()) - 1.0) > 1e-4
            ):
                notes.append(f"{label}交易后分布不自洽（合计 {sum(dist.values()):.4f}）：已清空，不显示。")
                if label == "地区":
                    region_after = {}
                else:
                    currency_after = {}

    cash_before = cash_available / portfolio_value if portfolio_value > 0 else None
    return PortfolioImpactBlock(
        currency=portfolio_currency,
        candidate_amount=amount_in_base if valid else 0.0,
        candidate_weight_after=candidate_weight,
        single_position_weight_after=single_after,
        single_position_weight_before=current_weight_of_symbol,
        single_position_limit_pct=None,
        cash_weight_after=cash_after,
        cash_weight_before=cash_before,
        region_weight_after=region_after,
        currency_weight_after=currency_after,
        region_weight_before=dict(region_weights) if region_weights else {},
        currency_weight_before=dict(currency_weights) if currency_weights else {},
        funding_currency=instrument_currency,
        notes=notes,
    )


_CONSERVATION_TOLERANCE = 1e-6


def _validated_distribution(
    weights: dict[str, float] | None,
    dimension_label: str,
    notes: list[str],
) -> dict[str, float]:
    """Shared buy/sell input gate (recheck-2 F-02): every value finite and >= 0 and the
    total within tolerance of 1 — otherwise the dimension fails closed with a note.

    NaN and infinities would otherwise slip through ``sum()`` comparisons silently; the
    old sell path copied the raw dict and could hand a 120% distribution to the UI.
    """
    if not weights:
        return {}
    import math

    for key, value in weights.items():
        if not math.isfinite(value) or value < 0:
            notes.append(
                f"{dimension_label}输入含非法值（{key}={value}）：交易后占比未计算。")
            return {}
    total = sum(weights.values())
    if abs(total - 1.0) > _CONSERVATION_TOLERANCE:
        notes.append(
            f"{dimension_label}权重输入合计 {total:.4f}≠100%，权重不守恒：交易后占比未计算。")
        return {}
    return dict(weights)


def _region_after(
    region_weights: dict[str, float] | None,
    instrument_region: str,
    candidate_weight: float,
    funding: str,
    notes: list[str],
) -> dict[str, float]:
    """Region weights after a buy, conserving the 1.0 total (remediation WP1)."""
    if not region_weights:
        return {}
    if sum(region_weights.values()) > 1 + _CONSERVATION_TOLERANCE or sum(region_weights.values()) < 1 - _CONSERVATION_TOLERANCE:
        notes.append(
            f"地区权重输入合计 {sum(region_weights.values()):.4f}≠100%，权重不守恒（合计≠100%）：交易后占比未计算。"
        )
        return {}
    if not instrument_region:
        notes.append("未知标的所属地区：地区占比的「交易后」值未计算。")
        return {}

    after: dict[str, float] = {}
    if funding == "external":
        # New money dilutes every old bucket; the candidate joins its region.
        scale = 1.0 - candidate_weight
        after = {k: v * scale for k, v in region_weights.items()}
        after[instrument_region] = after.get(instrument_region, 0.0) + candidate_weight
    else:
        # Existing cash: weight moves from the cash region to the asset's region.
        after = dict(region_weights)
        after["cash"] = after.get("cash", 0.0) - candidate_weight
        if after["cash"] < -_CONSERVATION_TOLERANCE:
            notes.append("现金地区占比不足：交易后占比未计算（输入可能缺少现金切片）。")
            return {}
        after[instrument_region] = after.get(instrument_region, 0.0) + candidate_weight
    return after


def _currency_after(
    currency_weights: dict[str, float] | None,
    instrument_currency: str,
    funding_currency: str,
    candidate_weight: float,
    funding: str,
    notes: list[str],
) -> dict[str, float]:
    """Currency weights after a buy, conserving the 1.0 total (remediation WP1)."""
    if not currency_weights:
        return {}
    total = sum(currency_weights.values())
    if total > 1 + _CONSERVATION_TOLERANCE or total < 1 - _CONSERVATION_TOLERANCE:
        notes.append(
            f"货币权重输入合计 {total:.4f}≠100%，权重不守恒（合计≠100%）：交易后占比未计算。"
        )
        return {}
    if not instrument_currency:
        notes.append("未知标的计价货币：货币占比的「交易后」值未计算。")
        return {}

    after: dict[str, float] = {}
    if funding == "external":
        scale = 1.0 - candidate_weight
        after = {k: v * scale for k, v in currency_weights.items()}
        after[instrument_currency] = after.get(instrument_currency, 0.0) + candidate_weight
    else:
        # Cash of the funding currency converts into the asset's currency.
        after = dict(currency_weights)
        if funding_currency not in after:
            notes.append(f"货币切片缺少 {funding_currency}（资金来源币种）：交易后占比未计算。")
            return {}
        after[funding_currency] = after[funding_currency] - candidate_weight
        if after[funding_currency] < -_CONSERVATION_TOLERANCE:
            notes.append("资金来源币种占比不足：交易后占比未计算。")
            return {}
        after[instrument_currency] = after.get(instrument_currency, 0.0) + candidate_weight
    return after


def build_invalidation(symbol: str, horizon_days: int | None) -> list[str]:
    """Plan §9 section 8: what future facts would reopen the question."""
    items = [
        f"{symbol} 的最新价数据源连续多日未更新或出现明显异常价（数据质量前提失效）。",
        "多源价格分歧持续超过你设定的容差，中位数不再可信。",
        "财务披露出现新报告期，收入/利润趋势方向改变。",
        "估值分位脱离当前区间（如从历史低位进入中位以上）。",
    ]
    if horizon_days:
        items.append(
            f"持有期限 {horizon_days} 天内基本面或价格出现与你当初判断相反的事实——"
            "证据卡不替你止损，但建议重新生成一次再评估。"
        )
    return items


def conclude(
    evidence_for: list[EvidenceItem],
    evidence_against: list[EvidenceItem],
    unknowns: list[EvidenceItem],
    risk_reasons: list[str],
    missing_data: list[str],
    side: str = "buy",
) -> Conclusion:
    """Plan §9 section 9: a status about the evidence, mechanically derived.

    The order matters and is the policy: risk-limit breaches outrank everything (the
    user's own stated limits are the one subjective input that must be able to stop a card
    cold); then insufficiency (the plan requires the card to be able to *stop*); then
    conflict; only then a leaning.

    ``risk_reasons`` carries one human-readable sentence per *actually triggered* limit
    (P1-R03) — the summary is built from what fired, never from a fixed template that
    could blame the wrong limit.
    """
    if risk_reasons:
        return Conclusion(
            status="risk_exceeded",
            summary="风险超限：" + "；".join(risk_reasons) + "。",
            reasons=list(risk_reasons),
        )

    # Insufficiency: too many unknown sections make any leaning dishonest.
    critical_missing = [m for m in missing_data if m in ("K线", "最新价")]
    if critical_missing or (len(unknowns) >= 4 and not evidence_for and not evidence_against):
        return Conclusion(
            status="insufficient",
            summary="证据不足：关键数据缺失，本卡到此为止，不做倾向性判断。",
            reasons=[f"缺失：{'、'.join(missing_data)}。"] if missing_data else ["未知项过多且无任何已证实据。"],
        )

    leaning = "卖出" if side == "sell" else "买入"
    if evidence_for and evidence_against:
        return Conclusion(
            status="conflicting",
            summary="证据冲突：正反两面都有已证实据，无法给出一致方向。",
            reasons=[
                f"支持{leaning}的证据 {len(evidence_for)} 条、反对的 {len(evidence_against)} 条，"
                "各自依据见上；冲突本身是结论，不是待修复的问题。"
            ],
        )

    if evidence_for and not evidence_against:
        return Conclusion(
            status="positive",
            summary=(
                f"证据偏向支持{leaning}：当前可得的已证实据中未发现反对项"
                "（这只描述证据构成，不是操作依据）。"
            ),
            reasons=[
                f"支持{leaning}的证据 {len(evidence_for)} 条；未知项 {len(unknowns)} 条。"
                "这只描述证据的当前构成，不是收益预测。"
            ],
        )

    if evidence_against and not evidence_for:
        return Conclusion(
            status="negative",
            summary=(
                f"证据偏向反对{leaning}：当前可得的已证实据中未发现支持项"
                "（这只描述证据构成，不是操作依据）。"
            ),
            reasons=[f"反对{leaning}的证据 {len(evidence_against)} 条；未知项 {len(unknowns)} 条。"],
        )

    return Conclusion(
        status="insufficient",
        summary="证据不足：没有足够的已证实据支持任何倾向。",
        reasons=[f"未知项 {len(unknowns)} 条，无正面/反面已证实据。"],
    )


def assemble_card(
    symbol: str,
    bars_payload: dict[str, Any],
    quote_payload: dict[str, Any] | None,
    valuation_payload: dict[str, Any] | None,
    financials_payload: dict[str, Any] | None,
    adjustment: str,
    portfolio: dict[str, Any] | None = None,
    as_of: str = "",
    side: str = "buy",
    price_tolerance_pct: float = 2.0,
) -> EvidenceCard:
    """Builds the full card from channel payloads. Pure: no I/O, no clock, no guessing.

    ``side`` is "buy" (default) or "sell". A sell card asks the opposite question about the
    same facts: evidence that supported adding becomes evidence against reducing, and the
    portfolio block must carry the plan's sell quality gate (taxes, opportunity cost, shape).
    """
    bars = bars_payload.get("data") or []
    quality, quality_items = build_data_quality(
        bars_payload, quote_payload, valuation_payload, financials_payload, adjustment,
        price_tolerance_pct=price_tolerance_pct,
    )
    # The card's date is the freshest *actual market-data* date, strictly ISO-parsed
    # (P1-R09): report periods like "2026Q1" are NOT dates and must never win a max() —
    # string comparison made a quarter label outrank every real date. Quote/valuation/bar
    # dates must parse as ISO; anything unparseable keeps its provenance but stays out of
    # the headline. Channels out of sync add a data-quality warning instead of hiding it.
    from datetime import date as _date_type

    iso_raw = [
        d for d in (quality.channel_as_of.get("bars"), quality.channel_as_of.get("quote"),
                    quality.channel_as_of.get("valuation"))
        if d and _is_iso_date(d)
    ]
    # Parse BEFORE comparing (recheck-2 F-03): a raw-string max() is lexicographic and any
    # future format slip would silently pick the wrong headline date.
    iso_dates = sorted(iso_raw, key=_date_type.fromisoformat)
    # No valid market date anywhere → UNKNOWN headline (recheck-3 R3-02): the old
    # fallback re-used the request's as_of or the raw last-bar date — values that never
    # passed the strict gate, so "最新行情截至 2026Q1/garbage" could reach the UI. The
    # raw values keep their provenance in channel_as_of plus a format warning; the
    # headline itself stays empty and the desktop shows 行情日期未知.
    as_of = iso_dates[-1] if iso_dates else ""

    financials_period = quality.channel_as_of.get("financials") or ""
    # Freshness (remediation WP4 + recheck P2-02/P2-03): ANY gap between market channels is
    # worth telling; unparseable dates are flagged as format anomalies (never silently kept
    # as if normal); report periods convert to a quarter index so staleness crosses year
    # boundaries correctly (2026-12 vs 2025Q1 is ~7 quarters, not a 1-year difference).
    stale_channels: list[str] = []
    format_anomalies: list[str] = []
    # Run whenever ANY channel has data (recheck-2 F-03): a card with only a malformed
    # financial period ("2026XX", no market dates) used to skip the scan entirely and
    # warn about nothing.
    if any(value for value in quality.channel_as_of.values()):
        from datetime import date as _date
        newest = max(iso_dates) if iso_dates else ""
        for name, value in quality.channel_as_of.items():
            if name == "financials" or not value:
                continue
            if not _is_iso_date(value):
                # Keep the raw provenance AND say the format is broken — the desktop shows
                # the string; without a flag the user reads garbage as a date (P2-02).
                format_anomalies.append(f"{name} 的日期「{value}」格式异常，无法判断新旧")
                continue
            if not newest:
                continue
            channel_date = _date.fromisoformat(value)
            gap = (_date.fromisoformat(newest) - channel_date).days
            if gap > 0:
                stale_channels.append(f"{name} 截至 {value}（落后最新行情 {gap} 天）")

        # Report periods: quarter index (year * 4 + quarter) so thresholds cross years.
        if financials_period:
            market_quarter = _quarter_index_from_iso(newest)
            financial_quarter = _quarter_index(financials_period)
            if financial_quarter is None:
                format_anomalies.append(f"财务报告期「{financials_period}」格式异常（应为 YYYYQn）")
            elif market_quarter is not None and market_quarter - financial_quarter >= 5:
                stale_channels.append(
                    f"财务报告期 {financials_period} 已落后最新行情约 "
                    f"{market_quarter - financial_quarter} 个季度，可能有未披露的更新报告")

    if format_anomalies:
        quality = DataQualityBlock(
            **{**quality.to_dict(),
               "warnings": quality.warnings + ["日期格式异常：" + "；".join(format_anomalies)]}
        )
    if stale_channels:
        quality = DataQualityBlock(
            **{**quality.to_dict(),
               "warnings": quality.warnings + ["通道数据不同步：" + "；".join(stale_channels)]}
        )
    valuation_block, valuation_items = build_valuation(valuation_payload)
    fundamentals_block, fundamentals_items = build_fundamentals(financials_payload)
    behaviour_block, behaviour_items = build_market_behaviour(bars_payload)
    historical_block, historical_items = build_historical_check(bars_payload)

    evidence_for = [i for i in quality_items + valuation_items + fundamentals_items if i.stance == "positive"]
    evidence_against = [i for i in quality_items + valuation_items + fundamentals_items if i.stance == "negative"]
    if side == "sell":
        # Same facts, opposite question: what was evidence for adding is evidence against
        # reducing, and vice versa. Unknowns stay unknown in both directions.
        evidence_for, evidence_against = evidence_against, evidence_for
    # Neutral facts (trend, volatility, conditional history) inform the reader but do not
    # count toward a leaning — a number that cuts both ways is not evidence for either.
    unknowns = [
        i
        for i in quality_items + valuation_items + fundamentals_items + behaviour_items + historical_items
        if i.stance == "unknown"
    ]

    impact: PortfolioImpactBlock | None = None
    risk_reasons: list[str] = []
    if portfolio:
        candidate = float(portfolio.get("candidate_amount") or 0)
        impact = build_portfolio_impact(
            candidate_amount=candidate,
            candidate_currency=str(portfolio.get("candidate_currency") or ""),
            portfolio_value=float(portfolio.get("portfolio_value") or 0),
            portfolio_currency=str(portfolio.get("portfolio_currency") or ""),
            current_weight_of_symbol=float(portfolio.get("current_weight_of_symbol") or 0),
            single_position_limit=portfolio.get("single_position_limit"),
            cash_available=float(portfolio.get("cash_available") or 0),
            region_weights=portfolio.get("region_weights"),
            currency_weights=portfolio.get("currency_weights"),
            side=side,
            funding=str(portfolio.get("funding") or "existing_cash"),
            instrument_region=str(portfolio.get("instrument_region") or ""),
            instrument_currency=str(portfolio.get("instrument_currency") or ""),
            funding_currency=str(portfolio.get("funding_currency") or ""),
        )
        limit = impact.single_position_limit_pct
        after = impact.single_position_weight_after
        if side == "buy" and limit is not None and after is not None:
            after_pct = after * 100
            if after_pct > limit:
                risk_reasons.append(
                    f"买入后单一持仓约 {after_pct:.1f}%，超过你设定的上限 {limit:.1f}%"
                )
                impact = PortfolioImpactBlock(
                    **{**impact.to_dict(), "notes": impact.notes + [
                        f"买入后单一持仓约 {after_pct:.1f}%，超过你设定的上限 {limit:.1f}%。"
                    ]}
                )

    # The user's own maximum-loss threshold is an auditable gate above any directional
    # leaning (P1-10/P1-R02): ``max_loss_pct`` is a 0~1 FRACTION on the API — the desktop UI
    # converts its percent input before sending. A drawdown history beyond what the user
    # said they can sit through must flip the conclusion to risk_exceeded, with its own
    # reason (P1-R03), not just append a local warning in the UI.
    max_loss_pct = None
    if portfolio:
        raw_max_loss = portfolio.get("max_loss_pct")
        if raw_max_loss is not None:
            candidate = float(raw_max_loss)
            if candidate <= 0 or candidate > 1:
                raise ValueError(
                    f"max_loss_pct 必须是 0~1 的小数（收到 {raw_max_loss}）；"
                    "百分比请在客户端先除以 100。"
                )
            max_loss_pct = candidate
    if max_loss_pct is not None:
        # max_drawdown_pct is stored signed-negative (-60.0 = a 60% fall); compare magnitudes.
        behaviour_dd = abs(float(behaviour_block.get("max_drawdown_pct") or 0)) / 100
        if behaviour_dd >= max_loss_pct:
            risk_reasons.append(
                f"窗口内历史最大回撤 {behaviour_dd:.1%} 已达到你设定的最大可承受损失 "
                f"{max_loss_pct:.1%}"
            )

    horizon_days = portfolio.get("horizon_days") if portfolio else None
    invalidation = build_invalidation(symbol, horizon_days)
    # The max-loss gate appends its fact to the invalidation list built above.
    if max_loss_pct is not None:
        behaviour_dd = abs(float(behaviour_block.get("max_drawdown_pct") or 0)) / 100
        if behaviour_dd >= max_loss_pct:
            invalidation = invalidation + [
                f"窗口内历史最大回撤 {behaviour_dd:.1%} 已达到你设定的最大可承受损失 "
                f"{max_loss_pct:.1%}。"
            ]
    conclusion = conclude(evidence_for, evidence_against, unknowns, risk_reasons, quality.missing, side)

    return EvidenceCard(
        symbol=symbol,
        side=side,
        as_of=as_of,
        market_as_of=as_of,
        data_quality=quality,
        valuation=valuation_block,
        fundamentals=fundamentals_block,
        market_behaviour=behaviour_block,
        historical_check=historical_block,
        portfolio_impact=impact,
        evidence_for=evidence_for,
        evidence_against=evidence_against,
        unknowns=unknowns,
        invalidation=invalidation,
        conclusion=conclusion,
    )
