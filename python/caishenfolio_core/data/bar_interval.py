from __future__ import annotations

from enum import StrEnum


class BarInterval(StrEnum):
    M1 = "1m"
    M5 = "5m"
    M15 = "15m"
    M30 = "30m"
    M60 = "60m"
    DAILY = "daily"
    WEEKLY = "weekly"
    MONTHLY = "monthly"
    QUARTERLY = "quarterly"
    YEARLY = "yearly"

    @classmethod
    def parse(cls, raw: str | None) -> BarInterval:
        text = (raw or "daily").strip().lower().replace("ｋ", "k").replace("K", "k")
        aliases: dict[str, BarInterval] = {
            "1m": cls.M1,
            "1min": cls.M1,
            "min1": cls.M1,
            "1分钟": cls.M1,
            "5m": cls.M5,
            "5min": cls.M5,
            "5分钟": cls.M5,
            "15m": cls.M15,
            "15min": cls.M15,
            "15分钟": cls.M15,
            "30m": cls.M30,
            "30min": cls.M30,
            "30分钟": cls.M30,
            "60m": cls.M60,
            "60min": cls.M60,
            "1h": cls.M60,
            "60分钟": cls.M60,
            "d": cls.DAILY,
            "day": cls.DAILY,
            "daily": cls.DAILY,
            "1d": cls.DAILY,
            "日": cls.DAILY,
            "日k": cls.DAILY,
            "w": cls.WEEKLY,
            "week": cls.WEEKLY,
            "weekly": cls.WEEKLY,
            "1w": cls.WEEKLY,
            "周": cls.WEEKLY,
            "周k": cls.WEEKLY,
            "m": cls.MONTHLY,
            "month": cls.MONTHLY,
            "monthly": cls.MONTHLY,
            "1mo": cls.MONTHLY,
            "月": cls.MONTHLY,
            "月k": cls.MONTHLY,
            "q": cls.QUARTERLY,
            "quarter": cls.QUARTERLY,
            "quarterly": cls.QUARTERLY,
            "季": cls.QUARTERLY,
            "季k": cls.QUARTERLY,
            "y": cls.YEARLY,
            "year": cls.YEARLY,
            "yearly": cls.YEARLY,
            "年": cls.YEARLY,
            "年k": cls.YEARLY,
        }
        if text in aliases:
            return aliases[text]
        try:
            return cls(text)
        except ValueError as exc:
            raise ValueError(
                f"不支持的K线周期 '{raw}'。可选：1m/5m/15m/30m/60m/daily/weekly/monthly/quarterly/yearly。"
            ) from exc

    @property
    def label_zh(self) -> str:
        return {
            BarInterval.M1: "1分钟",
            BarInterval.M5: "5分钟",
            BarInterval.M15: "15分钟",
            BarInterval.M30: "30分钟",
            BarInterval.M60: "60分钟",
            BarInterval.DAILY: "日K",
            BarInterval.WEEKLY: "周K",
            BarInterval.MONTHLY: "月K",
            BarInterval.QUARTERLY: "季K",
            BarInterval.YEARLY: "年K",
        }[self]

    @property
    def is_intraday(self) -> bool:
        return self in {self.M1, self.M5, self.M15, self.M30, self.M60}

    @property
    def is_aggregate_from_daily(self) -> bool:
        return self in {self.QUARTERLY, self.YEARLY}
