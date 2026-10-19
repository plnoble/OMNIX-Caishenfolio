"""Exchange-path comparison: which pot of money converts into more of the target currency.

The question this module answers is deliberately *relative*: "the yen I already hold — bought
at my actual historical rate — converts to more dollars than fresh yuan converts to, by how
much?" It refuses to answer the absolute one ("when is the best time to convert") — nobody
knows that, and pretending otherwise is how novices get hurt.

Everything is computed in :class:`decimal.Decimal`. Rates arrive as floats from the market
providers and are converted through ``str`` (never ``Decimal(float)``, which would import
binary noise); arithmetic is exact decimal; values are rounded once, at the boundary, with
ROUND_HALF_UP to 6 decimal places (documented, stable, testable).

The canonical worked example — fixed acceptance sample from the product plan §7, reference
rates around 2026-08-28 (Federal Reserve H.10 style reference rates, **not** bank executable
quotes):

- USD/CNY = C = 6.7260      (1 USD = 6.7260 CNY)
- USD/JPY = J = 159.97      (1 USD = 159.97 JPY)
- implied CNY/JPY = J / C = 23.783824...
- 100 CNY direct to USD: 100 / C = 14.867678 USD
- 2,450 JPY (bought at 24.5 JPY per CNY) to USD: 2450 / J = 15.315372 USD
- held-path advantage = E / (J / C) - 1 = 24.5 × 6.7260 / 159.97 - 1 = 3.011%
"""

from __future__ import annotations

from dataclasses import dataclass, field
from decimal import Decimal, ROUND_HALF_UP
from typing import Any

_QUANT = Decimal("0.000001")
_HUNDRED = Decimal("100")


def _dec(value: Any) -> Decimal:
    """Decimal through str, never through float — the boundary rule of this module."""
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def _q(value: Decimal | None) -> Decimal | None:
    """Single, documented rounding point: HALF_UP to 6 dp, only at serialization time."""
    return None if value is None else value.quantize(_QUANT, rounding=ROUND_HALF_UP)


@dataclass(frozen=True, slots=True)
class MarketQuote:
    """One quote in the explicit convention: 1 ``base`` = ``rate`` ``quote``.

    The direction is part of the data. A quote supplied in the inverted direction is
    normalized on the way in and the inversion is recorded in ``provenance``, so every
    displayed rate can say which way it points and where it came from.
    """

    base_currency: str
    quote_currency: str
    rate: Decimal
    provider: str = ""
    as_of: str = ""
    provenance: dict[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        # The boundary rule: Decimal through str, never Decimal(float).
        object.__setattr__(self, "rate", _dec(self.rate))
        object.__setattr__(self, "base_currency", self.base_currency.strip().upper())
        object.__setattr__(self, "quote_currency", self.quote_currency.strip().upper())

    def inverted(self) -> "MarketQuote":
        return MarketQuote(
            base_currency=self.quote_currency,
            quote_currency=self.base_currency,
            rate=Decimal(1) / self.rate,
            provider=self.provider,
            as_of=self.as_of,
            provenance={**self.provenance, "inverted_from": f"{self.base_currency}/{self.quote_currency}"},
        )

    def to_dict(self) -> dict[str, Any]:
        rate = _q(self.rate)
        return {
            "pair": f"{self.base_currency}/{self.quote_currency}",
            "direction": f"1 {self.base_currency} = {rate} {self.quote_currency}",
            "rate": float(rate) if rate is not None else None,
            "provider": self.provider,
            "as_of": self.as_of,
            "provenance": dict(self.provenance),
        }


@dataclass(frozen=True, slots=True)
class PathCost:
    """What one conversion costs. Every field optional — absent means *unknown*, never zero.

    A path whose cost model is missing is still comparable on gross amounts, but its net is
    reported as ``None`` with status ``gross_only`` rather than pretending the exchange is
    free (the plan's "成本输入缺失" state).
    """

    spread_pct: Decimal | None = None
    fixed_fee: Decimal | None = None
    fee_currency: str = ""
    fee_pct: Decimal | None = None
    slippage_pct: Decimal | None = None

    def __post_init__(self) -> None:
        for name in ("spread_pct", "fixed_fee", "fee_pct", "slippage_pct"):
            value = getattr(self, name)
            if value is not None:
                object.__setattr__(self, name, _dec(value))
        object.__setattr__(self, "fee_currency", (self.fee_currency or "").strip().upper())

    @classmethod
    def zero(cls) -> "PathCost":
        """Explicitly free — different from the default, which means *unknown*.

        Remediation WP2: BOTH cost components must be explicit zeros; a zero spread with an
        unstated fixed fee was still "unknown" under the tri-state contract.
        """
        return cls(spread_pct=Decimal("0"), fixed_fee=Decimal("0"))

    @property
    def is_known(self) -> bool:
        return any(
            v is not None for v in (self.spread_pct, self.fixed_fee, self.fee_pct, self.slippage_pct)
        )

    @property
    def is_variable_cost_known(self) -> bool:
        """True only when the user explicitly stated the percentage costs — including 0.

        Remediation WP2: ``None`` means UNKNOWN (never silently zero); only an explicit
        ``spread_pct=0`` (or a filled fee/slippage component) says "confirmed no spread".
        """
        return self.multiplicative_fraction is not None

    @property
    def fixed_fee_is_known(self) -> bool:
        """True when the fixed fee is explicitly stated — including an explicit 0."""
        return self.fixed_fee is not None

    @property
    def multiplicative_fraction(self) -> Decimal | None:
        """The fraction of notional lost to percentage costs, or None when unknown."""
        parts = [v for v in (self.spread_pct, self.fee_pct, self.slippage_pct) if v is not None]
        if not parts:
            return None
        fraction = Decimal(0)
        for pct in parts:
            if pct < 0:
                raise ValueError("成本比例不能为负。")
            fraction += pct / _HUNDRED
        if fraction >= 1:
            raise ValueError("成本比例合计达到或超过 100%，输入可能有误。")
        return fraction

    def to_dict(self) -> dict[str, Any]:
        def pct(v: Decimal | None) -> float | None:
            return None if v is None else float(_q(v))

        return {
            "spread_pct": pct(self.spread_pct),
            "fixed_fee": float(_q(self.fixed_fee)) if self.fixed_fee is not None else None,
            "fee_currency": self.fee_currency,
            "fee_pct": pct(self.fee_pct),
            "slippage_pct": pct(self.slippage_pct),
        }


@dataclass(frozen=True, slots=True)
class HeldPosition:
    """Money already held in a foreign currency, with what it actually cost — from the receipt."""

    currency: str
    amount: Decimal
    cost_in_base: Decimal

    def __post_init__(self) -> None:
        object.__setattr__(self, "amount", _dec(self.amount))
        object.__setattr__(self, "cost_in_base", _dec(self.cost_in_base))
        object.__setattr__(self, "currency", self.currency.strip().upper())

    @property
    def implied_rate(self) -> Decimal:
        """Units of held currency obtained per 1 unit of base, at the historical purchase."""
        if self.cost_in_base <= 0:
            raise ValueError("历史成本必须大于 0。")
        return self.amount / self.cost_in_base


@dataclass(frozen=True, slots=True)
class PathResult:
    """One conversion path: what you start with, what you end up with, and how it was computed."""

    key: str
    label: str
    status: str  # ok | gross_only | missing_quote | no_exchange
    starting_asset: str
    opportunity_cost: str
    legs: list[dict[str, Any]] = field(default_factory=list)
    gross_amount: Decimal | None = None
    cost_detail: dict[str, Any] = field(default_factory=dict)
    net_amount: Decimal | None = None
    #: Net target amount per ``base_amount`` of base-currency spending — the only
    #: apples-to-apples number across paths whose starting pots differ in size.
    net_per_base_amount: Decimal | None = None
    #: Gross target amount per ``base_amount``; the comparison basis when costs are unknown.
    gross_per_base_amount: Decimal | None = None
    #: Distance to the best comparable path, filled in after all paths exist.
    #: ``basis`` says which amount the numbers were computed on; never mixes net with gross.
    diff_vs_best: dict[str, Any] | None = None
    formula: str = ""
    note: str = ""

    @property
    def compare_basis(self) -> str | None:
        if self.net_per_base_amount is not None:
            return "net"
        if self.gross_per_base_amount is not None:
            return "gross"
        return None

    @property
    def compare_value(self) -> Decimal | None:
        if self.net_per_base_amount is not None:
            return self.net_per_base_amount
        return self.gross_per_base_amount

    def to_dict(self) -> dict[str, Any]:
        return {
            "key": self.key,
            "label": self.label,
            "status": self.status,
            "starting_asset": self.starting_asset,
            "opportunity_cost": self.opportunity_cost,
            "legs": self.legs,
            "gross_amount": float(_q(self.gross_amount)) if self.gross_amount is not None else None,
            "cost_detail": self.cost_detail,
            "net_amount": float(_q(self.net_amount)) if self.net_amount is not None else None,
            "net_per_base_amount": (
                float(_q(self.net_per_base_amount)) if self.net_per_base_amount is not None else None
            ),
            "diff_vs_best": self.diff_vs_best,
            "formula": self.formula,
            "note": self.note,
        }


@dataclass(frozen=True, slots=True)
class PathComparison:
    base_currency: str
    target_currency: str
    base_amount: Decimal
    held: HeldPosition | None
    quotes: dict[str, MarketQuote]
    paths: list[PathResult]
    #: Held-path vs new-money path, on gross amounts, per base-currency unit. None when
    #: either path could not be computed.
    held_advantage_gross_pct: Decimal | None = None
    held_advantage_net_pct: Decimal | None = None
    #: Historical held-currency rate per 1 base at which the two paths tie (gross basis).
    breakeven_held_rate: Decimal | None = None
    implied_cross: MarketQuote | None = None
    warnings: list[str] = field(default_factory=list)
    #: Best exchange path on the single shared basis; None when nothing is comparable.
    best_path: str | None = None
    #: Which amounts the whole comparison is computed on: "net" when every comparable path
    #: has a net figure, "gross" otherwise, "" when nothing is comparable. ONE basis for the
    #: top-level best path and every diff (P1-04).
    comparison_basis: str = ""

    def to_dict(self) -> dict[str, Any]:
        def pct(v: Decimal | None) -> float | None:
            return None if v is None else float((v * _HUNDRED).quantize(_QUANT, rounding=ROUND_HALF_UP))

        return {
            "base_currency": self.base_currency,
            "target_currency": self.target_currency,
            "base_amount": float(_q(self.base_amount)),
            "held": None
            if self.held is None
            else {
                "currency": self.held.currency,
                "amount": float(_q(self.held.amount)),
                "cost_in_base": float(_q(self.held.cost_in_base)),
                "implied_rate": float(_q(self.held.implied_rate)),
            },
            "quotes": {key: quote.to_dict() for key, quote in self.quotes.items()},
            "implied_cross": self.implied_cross.to_dict() if self.implied_cross else None,
            "paths": [p.to_dict() for p in self.paths],
            "comparison_basis": self.comparison_basis,
            "best_path": self.best_path,
            "held_advantage_gross_pct": pct(self.held_advantage_gross_pct),
            "held_advantage_net_pct": pct(self.held_advantage_net_pct),
            "breakeven_held_rate": (
                float(_q(self.breakeven_held_rate)) if self.breakeven_held_rate is not None else None
            ),
            "warnings": self.warnings,
            "disclaimer": (
                "研究/模拟结论，非投资建议。行情为参考价，未必是银行可成交报价；"
                "相对优势只说明「现在换」与「历史成本」的关系，不预测未来汇率，软件不替用户选择兑换时点。"
            ),
        }


def compare_paths(
    base_amount: Decimal | float | int | str,
    base_currency: str,
    target_currency: str,
    quotes: dict[str, MarketQuote],
    held: HeldPosition | None = None,
    costs: dict[str, PathCost] | None = None,
    split_fraction: Decimal | float | str | None = None,
) -> PathComparison:
    """Compares up to five ways of obtaining ``target_currency`` (plan §7 N2.2).

    ``quotes`` is keyed by pair (either direction accepted, e.g. ``"USD/CNY"`` or
    ``"CNY/USD"``); each must state its own direction and is normalized internally.
    Required: TARGET/BASE (e.g. USD/CNY) and, when a held position is given,
    TARGET/HELD (e.g. USD/JPY). Missing quotes fail closed — paths that need them
    come back ``missing_quote`` and nothing is fabricated.
    """
    base = base_currency.strip().upper()
    target = target_currency.strip().upper()
    amount = _dec(base_amount)
    if amount <= 0:
        raise ValueError("比较金额必须大于 0。")
    if base == target:
        raise ValueError("基准货币与目标货币不能相同。")
    if held is not None and held.amount <= 0:
        raise ValueError("已持有金额必须大于 0。")

    cost_models = costs or {}
    warnings: list[str] = []

    # --- normalize quotes: TARGET/BASE and TARGET/HELD, both stated as 1 TARGET = X OTHER.
    tb = _normalize_quote(quotes, target, base)
    th = _normalize_quote(quotes, target, held.currency) if held is not None else None

    missing = []
    if tb is None:
        missing.append(f"{target}/{base}")
    if held is not None and th is None:
        missing.append(f"{target}/{held.currency}")
    if missing:
        warnings.append(f"缺少关键报价：{'、'.join(missing)}。相关路径按未知处理，不估算。")

    paths: list[PathResult] = []

    # ---- path 1: held -> target (one leg) --------------------------------------
    p1_gross = p1_net = p1_per_base = None
    if held is not None and th is not None:
        p1_gross = held.amount / th.rate
        p1_cost = cost_models.get("held_to_target", PathCost())
        p1_net, p1_cost_detail, p1_note = _apply_costs(
            p1_gross, p1_cost, base, target, tb.rate if tb else None, held.currency
        )
        p1_per_base = None if p1_net is None else p1_net * amount / held.cost_in_base
        rate_txt = _fmt(th.rate)
        paths.append(
            PathResult(
                key="held_to_target",
                label=f"已持有 {held.currency} 直接换 {target}",
                status="ok" if p1_net is not None else "gross_only",
                starting_asset=f"{_fmt(held.amount)} {held.currency}（当时实付 {_fmt(held.cost_in_base)} {base}）",
                opportunity_cost=f"放弃继续持有 {_fmt(held.amount)} {held.currency}",
                legs=[
                    {
                        "from": held.currency,
                        "to": target,
                        "rate": float(_q(th.rate)),
                        "direction": f"1 {target} = {rate_txt} {held.currency}",
                    }
                ],
                gross_amount=p1_gross,
                cost_detail=p1_cost_detail,
                net_amount=p1_net,
                net_per_base_amount=p1_per_base,
                gross_per_base_amount=held.amount / th.rate * amount / held.cost_in_base,
                formula=f"毛额 = {held.amount} ÷ {th.rate} = {p1_gross:.6f} {target}",
                note=p1_note,
            )
        )
    elif held is not None:
        paths.append(_missing_quote_path("held_to_target", f"已持有 {held.currency} 直接换 {target}"))

    # ---- path 2: base -> target (one leg) --------------------------------------
    p2_gross = p2_net = None
    if tb is not None:
        p2_gross = amount / tb.rate
        p2_cost = cost_models.get("base_to_target", PathCost())
        p2_net, p2_cost_detail, p2_note = _apply_costs(p2_gross, p2_cost, base, target, tb.rate, base)
        paths.append(
            PathResult(
                key="base_to_target",
                label=f"新 {base} 直接换 {target}",
                status="ok" if p2_net is not None else "gross_only",
                starting_asset=f"{_fmt(amount)} {base}（新资金）",
                opportunity_cost=f"放弃持有 {_fmt(amount)} {base}",
                legs=[
                    {
                        "from": base,
                        "to": target,
                        "rate": float(_q(tb.rate)),
                        "direction": f"1 {target} = {_fmt(tb.rate)} {base}",
                    }
                ],
                gross_amount=p2_gross,
                cost_detail=p2_cost_detail,
                net_amount=p2_net,
                net_per_base_amount=p2_net,
                gross_per_base_amount=p2_gross,
                formula=f"毛额 = {amount} ÷ {tb.rate} = {p2_gross:.6f} {target}",
                note=p2_note,
            )
        )
    else:
        paths.append(_missing_quote_path("base_to_target", f"新 {base} 直接换 {target}"))

    # ---- path 3: base -> held -> target (two legs) ------------------------------
    if tb is not None and th is not None and held is not None:
        # Cross rate: units of held per 1 base, implied by the two target quotes.
        cross = th.rate / tb.rate
        held_equiv = amount * cross
        gross3 = held_equiv / th.rate
        cost3 = cost_models.get("base_via_held_to_target", PathCost())
        # Percentage costs bite once per leg; fixed fee once per path (per leg would double
        # count a single bank charge, and the honest story here is "two spreads").
        # P1-R05: a supplied fixed fee is deducted in ALL branches — "only a fixed fee
        # supplied" previously returned the gross untouched, inconsistent with every other path.
        frac3 = cost3.multiplicative_fraction
        net3 = None
        detail3: dict[str, Any] = {}
        fee3_in_target = _fixed_fee_in_target(cost3, base, target, tb.rate)
        fee3_unconvertible = cost3.fixed_fee is not None and fee3_in_target is None
        if frac3 is None and not cost3.is_known:
            detail3 = {"status": "成本输入缺失（毛额比较）"}
        elif fee3_unconvertible:
            detail3 = {"status": "固定费币种无法折算"}
        elif frac3 is None:
            # WP2: an unknown percentage cost blocks the net figure on EVERY path — a lone
            # fixed fee with a silently-zeroed spread would overstate the arrival here too.
            detail3 = {"status": "比例成本未填写（未知≠0），净额未计算"}
        elif cost3.fixed_fee is None:
            # FX-01: an unknown fixed fee blocks the net here exactly as it does on a single
            # leg. This branch used to fall through and subtract nothing, returning status=ok
            # with a confident net figure for a cost it had never been told — the one thing
            # this module exists to prevent.
            detail3 = {
                "status": "固定费未填写（未知≠0），净额未计算",
                "multiplicative_fraction_per_leg": float(_q(frac3)),
            }
        else:
            # (1 - f)^2 because the money crosses two spreads.
            kept = (Decimal(1) - frac3) ** 2
            before_fixed = gross3 * kept
            fee_in_target = _fixed_fee_in_target(cost3, base, target, tb.rate)
            if cost3.fixed_fee is not None and fee_in_target is None:
                detail3 = {
                    "status": "固定费币种无法折算",
                    "multiplicative_fraction": float(_q(frac3)),
                }
            else:
                net3 = before_fixed if fee_in_target is None else before_fixed - fee_in_target
                detail3 = {
                    "multiplicative_fraction_per_leg": float(_q(frac3)),
                    "combined_fraction": float(_q(Decimal(1) - kept)),
                    "fixed_fee_in_target": float(_q(fee_in_target)),
                }
        paths.append(
            PathResult(
                key="base_via_held_to_target",
                label=f"新 {base} 先换 {held.currency} 再换 {target}",
                status="ok" if net3 is not None else "gross_only",
                starting_asset=f"{_fmt(amount)} {base}（新资金）",
                opportunity_cost=f"绕行 {held.currency}，承受两次点差",
                legs=[
                    {
                        "from": base,
                        "to": held.currency,
                        "rate": float(_q(cross)),
                        "direction": f"1 {base} = {_fmt(cross)} {held.currency}（隐含交叉汇率 {th.rate} ÷ {tb.rate}）",
                    },
                    {
                        "from": held.currency,
                        "to": target,
                        "rate": float(_q(th.rate)),
                        "direction": f"1 {target} = {_fmt(th.rate)} {held.currency}",
                    },
                ],
                gross_amount=gross3,
                cost_detail=detail3,
                net_amount=net3,
                net_per_base_amount=net3,
                gross_per_base_amount=gross3,
                formula=(
                    f"隐含 {base}/{held.currency} = {th.rate} ÷ {tb.rate} = {cross:.6f}；"
                    f"毛额 = {amount} × {cross:.6f} ÷ {th.rate} = {gross3:.6f} {target}"
                ),
                note="两段换汇各付一次点差；若无特别优惠，通常不如直换。此路径列出是为了让代价可见。",
            )
        )
    elif held is not None:
        paths.append(_missing_quote_path("base_via_held_to_target", f"新 {base} 先换 {held.currency} 再换 {target}"))

    # ---- path 4: hold (no exchange) --------------------------------------------
    if held is not None and th is not None:
        value_now = held.amount / th.rate
        paths.append(
            PathResult(
                key="hold",
                label="暂不兑换（持有现状）",
                status="no_exchange",
                starting_asset=f"{_fmt(held.amount)} {held.currency}",
                opportunity_cost="不产生新的兑换成本，也不获得目标货币",
                legs=[
                    {
                        "from": held.currency,
                        "to": target,
                        "rate": float(_q(th.rate)),
                        "direction": f"1 {target} = {_fmt(th.rate)} {held.currency}（仅估值，非兑换）",
                    }
                ],
                gross_amount=value_now,
                cost_detail={"status": "无兑换，无成本"},
                net_amount=None,
                net_per_base_amount=None,
                formula=f"当前价值 = {held.amount} ÷ {th.rate} = {value_now:.6f} {target}",
                note="这不是一条兑换路径：只是「什么都不做」时现有持仓的当前价值。",
            )
        )

    # ---- path 5: split ----------------------------------------------------------
    if held is not None and th is not None and tb is not None:
        frac = _dec(0.5) if split_fraction is None else _dec(split_fraction)
        if frac < 0 or frac > 1:
            raise ValueError("分批比例必须在 0%–100% 之间。")
        from_held = held.amount * frac
        from_base = amount * (Decimal(1) - frac)
        gross_split = from_held / th.rate + from_base / tb.rate
        c1 = cost_models.get("held_to_target", PathCost())
        c2 = cost_models.get("base_to_target", PathCost())
        f1 = c1.multiplicative_fraction
        f2 = c2.multiplicative_fraction
        net_split = None
        detail5: dict[str, Any] = {}
        # Tri-state on BOTH legs (remediation WP2): percentage AND fixed costs must each be
        # explicitly stated; either one unknown blocks the split's net figure.
        if f1 is None or f2 is None:
            detail5 = {"status": "比例成本未填写（未知≠0），净额未计算"}
        elif c1.fixed_fee is None or c2.fixed_fee is None:
            detail5 = {"status": "固定费未填写（未知≠0），净额未计算"}
        else:
            kept1 = Decimal(1) - f1
            kept2 = Decimal(1) - f2
            fee1 = _fixed_fee_in_target(c1, base, target, tb.rate)
            fee2 = _fixed_fee_in_target(c2, base, target, tb.rate)
            # None from _fixed_fee_in_target means *no fixed fee* unless a fee was actually
            # supplied and could not be converted — the two cases must not be conflated.
            if (c1.fixed_fee is not None and fee1 is None) or (c2.fixed_fee is not None and fee2 is None):
                detail5 = {"status": "固定费币种无法折算"}
            else:
                # A fixed fee is charged once per conversion that actually runs — a 50/50
                # split executes BOTH conversions, so BOTH fees bite in full (P1-05). A 0%
                # share skips that leg entirely; a 100% share runs only one.
                runs_held = frac > 0
                runs_base = frac < 1
                net_split = (
                    (from_held / th.rate * kept1 - (fee1 or Decimal(0)) if runs_held else Decimal(0))
                    + (from_base / tb.rate * kept2 - (fee2 or Decimal(0)) if runs_base else Decimal(0))
                )
                detail5 = {
                    "held_share_pct": float(_q(frac * _HUNDRED)),
                    "base_share_pct": float(_q((Decimal(1) - frac) * _HUNDRED)),
                    "fixed_fees_charged": (
                        float(_q((fee1 or Decimal(0)) * (Decimal(1) if runs_held else Decimal(0))
                                + (fee2 or Decimal(0)) * (Decimal(1) if runs_base else Decimal(0))))
                    ),
                }
        # Normalized per base_amount, on the SAME cost formula as net_split (P1-05): every
        # executed conversion's fixed fee included, no second bookkeeping.
        per_base_split = None
        if net_split is not None and f1 is not None and f2 is not None:
            kept1 = Decimal(1) - f1
            kept2 = Decimal(1) - f2
            fee1 = _fixed_fee_in_target(c1, base, target, tb.rate)
            # P1-R04: one unit, same as every other path — "target units obtained per
            # base_amount of base-currency spending". The split spends a mix of historically
            # bought held currency (cost_in_base × frac) and fresh base money (amount ×
            # (1-frac)), so the comparable per-base value divides the net outcome by that
            # total base outlay, scaled to base_amount. No second application of frac.
            total_base_outlay = held.cost_in_base * frac + amount * (Decimal(1) - frac)
            per_base_split = (
                net_split * amount / total_base_outlay if total_base_outlay > 0 else None
            )
        paths.append(
            PathResult(
                key="split",
                label=f"分批兑换（{_fmt(frac * _HUNDRED)}% 用已持有，其余用新资金）",
                status="ok" if net_split is not None else "gross_only",
                starting_asset=f"{_fmt(from_held)} {held.currency} + {_fmt(from_base)} {base}",
                opportunity_cost="两条路径各承担一部分",
                legs=[],
                gross_amount=gross_split,
                cost_detail=detail5,
                net_amount=net_split,
                net_per_base_amount=per_base_split,
                # Same unit as the net variant: gross per base_amount of total base outlay.
                gross_per_base_amount=(
                    gross_split * amount
                    / (held.cost_in_base * frac + amount * (Decimal(1) - frac))
                ),
                formula=(
                    f"毛额 = {from_held} ÷ {th.rate} + {from_base} ÷ {tb.rate} = {gross_split:.6f} {target}"
                ),
                note="分批平滑的是「这一次换多少」的不确定性，不改变任何一条路径的汇率。",
            )
        )

    # ---- headline numbers -------------------------------------------------------
    advantage_gross = advantage_net = breakeven = None
    if held is not None and tb is not None and th is not None:
        e = held.implied_rate
        # Gross: held path per base_amount vs new path per base_amount.
        held_gross_per_base = held.amount / th.rate * amount / held.cost_in_base
        new_gross_per_base = amount / tb.rate
        if new_gross_per_base > 0:
            advantage_gross = held_gross_per_base / new_gross_per_base - Decimal(1)
        if p1_per_base is not None and p2_net is not None and p2_net != 0:
            advantage_net = p1_per_base / p2_net - Decimal(1)
        breakeven = th.rate / tb.rate
        if e > 0:
            warnings.append(
                f"历史实际 {base}/{held.currency} 成交价 {_fmt(e)}；当前隐含 {base}/{held.currency} = "
                f"{_fmt(breakeven)}（= {th.rate} ÷ {tb.rate}）。"
                + (
                    "历史价高于隐含价，毛金额上已有路径占优。"
                    if e > breakeven
                    else "历史价不高于隐含价，毛金额上新资金路径占优。"
                )
            )

    # ---- pairwise differences vs the best path (plan §7: 绝对差和百分比差) ---------
    # Only exchange paths compare; "hold" is not a conversion and missing-quote paths have
    # no number. ONE basis for the whole comparison — computed once, reused by the top-level
    # best path and every diff object so they can never disagree (P1-04).
    comparable = [
        p
        for p in paths
        if p.status != "no_exchange" and p.compare_value is not None
    ]
    use_net = bool(comparable) and all(p.net_per_base_amount is not None for p in comparable)
    basis_name = "net" if use_net and comparable else ("gross" if comparable else "")
    best_key: str | None = None

    def basis_value(p: PathResult) -> Decimal:
        value = p.net_per_base_amount if use_net else p.gross_per_base_amount
        assert value is not None
        return value

    if comparable:
        best = max(comparable, key=basis_value)
        best_value = basis_value(best)
        best_key = best.key
        for path in comparable:
            if path is best or best_value == 0:
                if path is best:
                    object.__setattr__(path, "diff_vs_best", {
                        "best_path": best.key,
                        "basis": basis_name,
                        "abs": 0.0,
                        "abs_in_target": f"0 {target}",
                        "pct": 0.0,
                    })
                continue
            difference = basis_value(path) - best_value
            object.__setattr__(path, "diff_vs_best", {
                "best_path": best.key,
                "basis": basis_name,
                "abs": float(_q(difference)),
                "abs_in_target": f"{_fmt(difference)} {target}",
                "pct": float(_q(difference / best_value * _HUNDRED)),
            })

    implied_cross = None
    if tb is not None and th is not None and held is not None:
        implied_cross = MarketQuote(
            base_currency=base,
            quote_currency=held.currency,
            rate=th.rate / tb.rate,
            provider=f"隐含（{th.provider or '来源未知'} / {tb.provider or '来源未知'}）",
            as_of=th.as_of or tb.as_of,
            provenance={
                "formula": f"1 {base} = ({th.rate} ÷ {tb.rate}) {held.currency}",
            },
        )

    # The quotes the math actually used, in their canonical TARGET/… direction, with the
    # inversion provenance preserved — the plan's "交叉汇率必须显示公式和原始来源".
    normalized_quotes: dict[str, MarketQuote] = {}
    if tb is not None:
        normalized_quotes[f"{target}/{base}"] = tb
    if th is not None and held is not None:
        normalized_quotes[f"{target}/{held.currency}"] = th

    return PathComparison(
        base_currency=base,
        target_currency=target,
        base_amount=amount,
        held=held,
        quotes=normalized_quotes,
        paths=paths,
        held_advantage_gross_pct=advantage_gross,
        held_advantage_net_pct=advantage_net,
        breakeven_held_rate=breakeven,
        implied_cross=implied_cross,
        warnings=warnings,
        best_path=best_key,
        comparison_basis=basis_name,
    )


def _apply_costs(
    gross: Decimal,
    cost: PathCost,
    base: str,
    target: str,
    rate_tb: Decimal | None,
    held_currency: str,
) -> tuple[Decimal | None, dict[str, Any], str]:
    """Net of one-leg costs. Unknown costs → net None + status, never a silent zero.

    Remediation WP2: the percentage cost must be EXPLICITLY stated (0 counts — "confirmed
    none"); a lone fixed fee with an unknown spread cannot produce a net figure, because
    assuming the missing spread is zero would overstate the arrival.
    """
    if not cost.is_variable_cost_known:
        return None, {"status": "比例成本未填写（未知≠0），净额未计算"}, (
            "比例成本（点差等）未显式填写：未知不等同于零，仅比较毛到账金额。"
        )

    frac = cost.multiplicative_fraction
    detail: dict[str, Any] = {}
    after_pct = gross * (Decimal(1) - frac)
    detail["multiplicative_fraction"] = float(_q(frac))
    detail["pct_cost_in_target"] = float(_q(gross - after_pct))

    # The fixed fee is tri-state too (remediation WP2): None = UNKNOWN (never zero).
    # A known spread with an unstated fee produced a full "ok" net that quietly ignored it.
    if cost.fixed_fee is None:
        return None, {**detail, "status": "固定费未填写（未知≠0），净额未计算"}, (
            "固定手续费未显式填写：未知不等同于零，仅比较毛到账金额。"
        )

    fee_in_target = _fixed_fee_in_target(cost, base, target, rate_tb)
    if fee_in_target is None:
        return None, {**detail, "status": "固定费币种无法折算"}, (
            f"固定费币种 {cost.fee_currency} 无法折算到 {target}。"
        )

    detail["fixed_fee_in_target"] = float(_q(fee_in_target))
    return after_pct - fee_in_target, detail, ""


def _fixed_fee_in_target(
    cost: PathCost, base: str, target: str, rate_tb: Decimal | None
) -> Decimal | None:
    """Converts the fixed fee into target currency; None when it cannot be done honestly."""
    if cost.fixed_fee is None:
        return None
    fee_ccy = (cost.fee_currency or target).strip().upper()
    if fee_ccy == target:
        return cost.fixed_fee
    if fee_ccy == base and rate_tb is not None and rate_tb != 0:
        return cost.fixed_fee / rate_tb
    return None


def _normalize_quote(quotes: dict[str, MarketQuote], target: str, other: str) -> MarketQuote | None:
    """Finds the TARGET/OTHER quote in either direction and normalizes to 1 TARGET = X OTHER."""
    for quote in quotes.values():
        b = quote.base_currency.upper()
        q = quote.quote_currency.upper()
        if b == target and q == other:
            return quote
        if b == other and q == target:
            return quote.inverted()
    return None


def _missing_quote_path(key: str, label: str) -> PathResult:
    return PathResult(
        key=key,
        label=label,
        status="missing_quote",
        starting_asset="—",
        opportunity_cost="—",
        legs=[],
        gross_amount=None,
        cost_detail={"status": "缺少关键报价"},
        net_amount=None,
        net_per_base_amount=None,
        formula="",
        note="缺少关键报价，本路径不估算（fail-closed）。",
    )


def _fmt(value: Decimal | Any) -> str:
    v = _dec(value)
    return f"{v.quantize(_QUANT, rounding=ROUND_HALF_UP):f}"
