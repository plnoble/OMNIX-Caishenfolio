"""Per-share fundamentals, shared by the fetchers in ``market`` and the series builder in ``research``.

Lives in ``data`` so neither of those packages has to import the other.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from typing import Any


@dataclass(frozen=True, slots=True)
class PerShareFundamental:
    """Earnings and book value per share, with the date the market could first act on them.

    ``effective_date`` is the point of the whole record. Valuing January prices against earnings
    that were announced in April would make every historical multiple look better-informed than
    it was, so the series builder keys on this rather than on ``period_end``.
    """

    period_end: date
    effective_date: date
    eps_ttm: float | None = None
    bps: float | None = None
    currency: str = ""
    #: True when ``effective_date`` is an assumed lag rather than a reported announcement date.
    effective_date_estimated: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "period_end": self.period_end.isoformat(),
            "effective_date": self.effective_date.isoformat(),
            "eps_ttm": self.eps_ttm,
            "bps": self.bps,
            "currency": self.currency,
            "effective_date_estimated": self.effective_date_estimated,
        }
