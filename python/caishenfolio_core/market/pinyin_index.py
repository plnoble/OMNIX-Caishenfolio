"""Pinyin matching for the A-share name index (plan §6: 搜索支持名称、拼音、简称和代码).

Full pinyin conversion needs :mod:`pypinyin`, which is part of the ``market`` extras — a
real install always has it. Offline/fixture runs do not, so the seed table carries its
pinyin verbatim and the converter is injectable: the matching logic itself is pure and
unit-testable either way. When neither a converter nor a stored pinyin exists, pinyin
matching simply matches nothing — it never falls back to guessing.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache


@dataclass(frozen=True, slots=True)
class PinyinName:
    """The searchable romanizations of one Chinese name."""

    initials: str
    full: str


# Hand-written pinyin for the offline seed table — checked, not generated, so fixture mode
# has real coverage (e.g. ``pfyh`` → 浦发银行).
_SEED_PINYIN: dict[str, tuple[str, str]] = {
    "平安银行": ("payh", "pinganyinhang"),
    "浦发银行": ("pfyh", "pufayinhang"),
    "贵州茅台": ("gzmt", "guizhoumaotai"),
    "五粮液": ("wly", "wuliangye"),
    "宁德时代": ("ndsd", "ningdeshidai"),
    "中国平安": ("zgpa", "zhongguopingan"),
    "隆基绿能": ("ljln", "longjilvneng"),
    "万科A": ("wka", "wanke"),
    "招商银行": ("zsyh", "zhaoshangyinhang"),
    "兴业银行": ("xyyh", "xingyeyinhang"),
    "美的集团": ("mdjt", "meidijituan"),
    "比亚迪": ("byd", "byd"),
    "东方财富": ("dfcf", "dongfangcaifu"),
    "沪深300ETF": ("hs300etf", "hushen300etf"),
    "创业板ETF": ("cybetf", "chuangyebanetf"),
    "黄金ETF": ("hjetf", "huangjinetf"),
}


@lru_cache(maxsize=1)
def _pypinyin_converter():
    """Returns the romanizer, or None when pypinyin is not installed."""
    try:
        from pypinyin import lazy_pinyin, Style  # type: ignore
    except Exception:  # noqa: BLE001
        return None

    def convert(name: str) -> tuple[str, str]:
        full = "".join(lazy_pinyin(name))
        initials = "".join(lazy_pinyin(name, style=Style.FIRST_LETTER))
        return initials, full

    return convert


def pinyin_of(name: str, converter=None) -> PinyinName | None:
    """Romanized forms of one name, or None when it cannot be done honestly."""
    if not name:
        return None

    if converter is not None:
        # The injected-converter protocol (recheck P2-04): returning None means "cannot
        # romanize this name" — the documented way tests simulate a pypinyin-free
        # environment without depending on what the interpreter happens to have installed.
        result = converter(name)
        if result is None:
            return None
        initials, full = result
        return PinyinName(initials.lower(), full.lower())

    convert = _pypinyin_converter()
    if convert is not None:
        initials, full = convert(name)
        return PinyinName(initials.lower(), full.lower())

    stored = _SEED_PINYIN.get(name)
    if stored is not None:
        return PinyinName(stored[0], stored[1])

    return None


def pinyin_matches(query: str, name: str, converter=None) -> bool:
    """True when the ASCII query is a prefix of (or contained in) the name's romanizations."""
    q = (query or "").strip().lower()
    if not q or not q.isascii() or not q.isalpha():
        return False

    forms = pinyin_of(name, converter)
    if forms is None:
        return False

    return (
        forms.initials.startswith(q)
        or forms.full.startswith(q)
        or (len(q) >= 3 and q in forms.full)
    )
