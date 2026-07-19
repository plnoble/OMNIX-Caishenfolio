from __future__ import annotations

from typing import Any


def compare_normalized_closes(
    series: dict[str, list[dict[str, Any]]],
) -> dict[str, object]:
    """
    Align multiple symbols by date and normalize each series to 100 at first common bar.
    series: symbol -> list of bar dicts with timestamp_utc, close
    """
    if not series:
        return {"ok": False, "error": "未提供任何序列。", "items": []}

    # map symbol -> {date_str: close}
    by_sym: dict[str, dict[str, float]] = {}
    for sym, bars in series.items():
        m: dict[str, float] = {}
        for b in bars:
            ts = str(b.get("timestamp_utc") or "")[:10]
            if not ts:
                continue
            try:
                m[ts] = float(b["close"])
            except (KeyError, TypeError, ValueError):
                continue
        if m:
            by_sym[sym] = m

    if len(by_sym) < 2:
        return {
            "ok": False,
            "error": "对比至少需要 2 个有效标的序列。",
            "items": [],
            "disclaimer": "研究/模拟结论，非投资建议。",
        }

    common = None
    for m in by_sym.values():
        keys = set(m.keys())
        common = keys if common is None else (common & keys)
    if not common:
        return {
            "ok": False,
            "error": "各标的无共同交易日，无法对比。",
            "items": [],
            "disclaimer": "研究/模拟结论，非投资建议。",
        }

    dates = sorted(common)
    base = {sym: by_sym[sym][dates[0]] for sym in by_sym}
    points: list[dict[str, object]] = []
    for d in dates:
        row: dict[str, object] = {"date": d, "values": {}}
        vals: dict[str, float] = {}
        for sym, m in by_sym.items():
            if base[sym] == 0:
                vals[sym] = 100.0
            else:
                vals[sym] = 100.0 * (m[d] / base[sym])
        row["values"] = vals
        points.append(row)

    # summary returns
    summary = []
    for sym in sorted(by_sym.keys()):
        first = by_sym[sym][dates[0]]
        last = by_sym[sym][dates[-1]]
        ret = (last / first) - 1.0 if first else None
        summary.append(
            {
                "symbol": sym,
                "start_close": first,
                "end_close": last,
                "return": ret,
                "normalized_end": 100.0 * (last / first) if first else None,
            }
        )

    return {
        "ok": True,
        "date_count": len(dates),
        "start": dates[0],
        "end": dates[-1],
        "summary": summary,
        "points": points[-500:],  # cap
        "disclaimer": "研究/模拟结论，非投资建议。研究对比用归一化收盘价（起点=100）。",
        "warnings": ["normalized_close_comparison", "not_for_investment_decisions"],
    }
