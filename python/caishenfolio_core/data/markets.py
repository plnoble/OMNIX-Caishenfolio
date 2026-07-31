"""Market identity: where an instrument trades, what it is, and in which currency.

Mirrors the C# side (``Caishenfolio.Host.Data.ExchangeRegistry`` / ``MarketRegion`` /
``AssetClass``). Region and asset class are deliberately separate — the previous ``Market``
enum mixed them (it had an ``ETF`` member), so "an ETF listed in the US" had no representation.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum


class MarketRegion(StrEnum):
    CN = "cn"
    HK = "hk"
    US = "us"
    JP = "jp"
    GLOBAL = "global"


class AssetClass(StrEnum):
    EQUITY = "equity"
    ETF = "etf"
    INDEX = "index"
    #: Off-exchange open-end fund priced by daily NAV (场外公募基金).
    MUTUAL_FUND = "mutual_fund"
    BOND = "bond"
    CONVERTIBLE_BOND = "convertible_bond"
    FX = "fx"
    CASH = "cash"
    COMMODITY = "commodity"
    REIT = "reit"


#: Pseudo-venues: off-exchange funds and currency pairs still need a stable EXCHANGE:CODE identity.
FX_EXCHANGE = "FX"
CN_FUND_EXCHANGE = "FUND"


@dataclass(frozen=True, slots=True)
class ExchangeInfo:
    code: str
    display_name: str
    region: MarketRegion
    #: Quote currency, or "" when it depends on the code (FX pairs).
    currency: str
    timezone: str
    default_asset: AssetClass
    #: Suffix used by Yahoo-style vendors, e.g. TSE:7203 -> 7203.T
    yahoo_suffix: str = ""


_EXCHANGES: tuple[ExchangeInfo, ...] = (
    ExchangeInfo("SSE", "上海证券交易所", MarketRegion.CN, "CNY", "Asia/Shanghai", AssetClass.EQUITY, ".SS"),
    ExchangeInfo("SZSE", "深圳证券交易所", MarketRegion.CN, "CNY", "Asia/Shanghai", AssetClass.EQUITY, ".SZ"),
    ExchangeInfo("BSE", "北京证券交易所", MarketRegion.CN, "CNY", "Asia/Shanghai", AssetClass.EQUITY, ".BJ"),
    ExchangeInfo("CNIB", "银行间债券市场", MarketRegion.CN, "CNY", "Asia/Shanghai", AssetClass.BOND),
    ExchangeInfo(CN_FUND_EXCHANGE, "场外公募基金", MarketRegion.CN, "CNY", "Asia/Shanghai", AssetClass.MUTUAL_FUND),
    ExchangeInfo("HKEX", "香港交易所", MarketRegion.HK, "HKD", "Asia/Hong_Kong", AssetClass.EQUITY, ".HK"),
    ExchangeInfo("NASDAQ", "纳斯达克", MarketRegion.US, "USD", "America/New_York", AssetClass.EQUITY),
    ExchangeInfo("NYSE", "纽约证券交易所", MarketRegion.US, "USD", "America/New_York", AssetClass.EQUITY),
    ExchangeInfo("AMEX", "美国证券交易所", MarketRegion.US, "USD", "America/New_York", AssetClass.EQUITY),
    ExchangeInfo("TSE", "东京证券交易所", MarketRegion.JP, "JPY", "Asia/Tokyo", AssetClass.EQUITY, ".T"),
    ExchangeInfo(FX_EXCHANGE, "外汇", MarketRegion.GLOBAL, "", "UTC", AssetClass.FX),
)

_BY_CODE: dict[str, ExchangeInfo] = {item.code: item for item in _EXCHANGES}

_EXCHANGE_ALIASES: dict[str, str] = {
    "SH": "SSE",
    "SHSE": "SSE",
    "SZ": "SZSE",
    "BJ": "BSE",
    "HK": "HKEX",
    "SEHK": "HKEX",
    "OF": CN_FUND_EXCHANGE,
    "CNFUND": CN_FUND_EXCHANGE,
    "TYO": "TSE",
    "JPX": "TSE",
    "FOREX": FX_EXCHANGE,
}

#: Minor units per currency — JPY/KRW have none. Money itself lives in the C# ledger.
CURRENCY_MINOR_UNITS: dict[str, int] = {
    "CNY": 2,
    "HKD": 2,
    "USD": 2,
    "JPY": 0,
    "EUR": 2,
    "GBP": 2,
    "TWD": 2,
    "SGD": 2,
    "AUD": 2,
    "CAD": 2,
    "CHF": 2,
    "KRW": 0,
}

_REGION_ALIASES: dict[str, MarketRegion] = {
    "cn": MarketRegion.CN,
    "ashare": MarketRegion.CN,
    "a_share": MarketRegion.CN,
    "a-share": MarketRegion.CN,
    "china": MarketRegion.CN,
    # Legacy values that described the asset class, not the venue.
    "etf": MarketRegion.CN,
    "fund": MarketRegion.CN,
    "hk": MarketRegion.HK,
    "hongkong": MarketRegion.HK,
    "hong_kong": MarketRegion.HK,
    "us": MarketRegion.US,
    "usa": MarketRegion.US,
    "jp": MarketRegion.JP,
    "japan": MarketRegion.JP,
    "global": MarketRegion.GLOBAL,
    "world": MarketRegion.GLOBAL,
}

_ASSET_ALIASES: dict[str, AssetClass] = {
    "equity": AssetClass.EQUITY,
    "stock": AssetClass.EQUITY,
    "etf": AssetClass.ETF,
    "index": AssetClass.INDEX,
    "mutual_fund": AssetClass.MUTUAL_FUND,
    "mutualfund": AssetClass.MUTUAL_FUND,
    # Legacy: "fund" meant off-exchange open-end fund before ETFs got their own class.
    "fund": AssetClass.MUTUAL_FUND,
    "bond": AssetClass.BOND,
    "convertible_bond": AssetClass.CONVERTIBLE_BOND,
    "convertiblebond": AssetClass.CONVERTIBLE_BOND,
    "cb": AssetClass.CONVERTIBLE_BOND,
    "fx": AssetClass.FX,
    "forex": AssetClass.FX,
    "currency": AssetClass.FX,
    "cash": AssetClass.CASH,
    "deposit": AssetClass.CASH,
    "commodity": AssetClass.COMMODITY,
    "reit": AssetClass.REIT,
}

REGION_LABELS: dict[MarketRegion, str] = {
    MarketRegion.CN: "A股",
    MarketRegion.HK: "港股",
    MarketRegion.US: "美股",
    MarketRegion.JP: "日股",
    MarketRegion.GLOBAL: "全球",
}

ASSET_LABELS: dict[AssetClass, str] = {
    AssetClass.EQUITY: "股票",
    AssetClass.ETF: "ETF",
    AssetClass.INDEX: "指数",
    AssetClass.MUTUAL_FUND: "场外基金",
    AssetClass.BOND: "债券",
    AssetClass.CONVERTIBLE_BOND: "可转债",
    AssetClass.FX: "外汇",
    AssetClass.CASH: "现金",
    AssetClass.COMMODITY: "商品",
    AssetClass.REIT: "REITs",
}


def all_exchanges() -> tuple[ExchangeInfo, ...]:
    return _EXCHANGES


def resolve_exchange(exchange: str | None) -> ExchangeInfo | None:
    """Look up a venue, resolving aliases (``SH`` -> ``SSE``). None for unknown venues."""
    if not exchange:
        return None
    code = str(exchange).strip().upper()
    code = _EXCHANGE_ALIASES.get(code, code)
    return _BY_CODE.get(code)


def canonical_exchange(exchange: str | None) -> str | None:
    info = resolve_exchange(exchange)
    return info.code if info else None


def parse_region(value: str | None) -> MarketRegion | None:
    """Accepts legacy market strings persisted before the region/asset split."""
    if not value:
        return None
    return _REGION_ALIASES.get(str(value).strip().lower())


def parse_asset_class(value: str | None) -> AssetClass | None:
    if not value:
        return None
    return _ASSET_ALIASES.get(str(value).strip().lower())


def fx_pair(symbol: str) -> tuple[str, str] | None:
    """Splits ``FX:USDCNY`` into ``("USD", "CNY")``; None for non-FX or unknown currencies."""
    exchange, _, code = str(symbol or "").partition(":")
    if canonical_exchange(exchange) != FX_EXCHANGE:
        return None
    code = code.strip().upper()
    if len(code) != 6:
        return None
    base, quote = code[:3], code[3:]
    if base not in CURRENCY_MINOR_UNITS or quote not in CURRENCY_MINOR_UNITS:
        return None
    return base, quote


def fx_symbol(base_currency: str, quote_currency: str) -> str:
    return f"{FX_EXCHANGE}:{base_currency.strip().upper()}{quote_currency.strip().upper()}"


def region_of(symbol: str) -> MarketRegion:
    """Region for an ``EXCHANGE:CODE`` symbol; GLOBAL when the venue is unknown."""
    exchange, _, _ = str(symbol or "").partition(":")
    info = resolve_exchange(exchange)
    return info.region if info else MarketRegion.GLOBAL


def quote_currency(symbol: str) -> str | None:
    """Quote currency for a symbol. FX pairs carry it in the code (``FX:USDCNY`` -> CNY)."""
    exchange, _, _ = str(symbol or "").partition(":")
    info = resolve_exchange(exchange)
    if info is None:
        return None
    if info.code == FX_EXCHANGE:
        pair = fx_pair(symbol)
        return pair[1] if pair else None
    return info.currency or None


def yahoo_ticker(symbol: str) -> str | None:
    """Maps ``EXCHANGE:CODE`` to a Yahoo-style ticker (``TSE:7203`` -> ``7203.T``)."""
    exchange, _, code = str(symbol or "").partition(":")
    info = resolve_exchange(exchange)
    if info is None or not code:
        return None
    code = code.strip().upper()
    if info.code == FX_EXCHANGE:
        pair = fx_pair(symbol)
        return f"{pair[0]}{pair[1]}=X" if pair else None
    if info.code == CN_FUND_EXCHANGE:
        return None  # Off-exchange funds are NAV-priced; Yahoo has no ticker for them.
    if info.code == "HKEX":
        # Yahoo uses four digits: HKEX:00700 and HKEX:700 both become 0700.HK.
        code = (code.lstrip("0") or "0").zfill(4)
    if info.code == "TSE":
        code = code.lstrip("0") or code
    return f"{code}{info.yahoo_suffix}"


def is_nav_priced(asset: AssetClass) -> bool:
    """True when the instrument is priced by daily NAV instead of OHLCV bars."""
    return asset is AssetClass.MUTUAL_FUND


# --- CN listed-code classification -------------------------------------------------
# Display-level classification only; bars always come from the real provider and stay
# fail-closed. Exchange matters: SSE 000001 is 上证指数 while SZSE 000001 is 平安银行.

_SSE_CONVERTIBLE_PREFIXES = ("110", "111", "112", "113", "118", "132")
_SZSE_CONVERTIBLE_PREFIXES = ("123", "127", "128", "117")


def cn_exchange_for_code(code: str) -> str:
    """Guesses the CN venue for a bare 6-digit code.

    Ambiguity is real: SSE convertible bonds (``110***``/``113***``) and SZSE ETFs
    (``159***``) both start with ``1``, so bond prefixes are matched before the generic rule.
    """
    digits = "".join(ch for ch in str(code or "") if ch.isdigit()).zfill(6)
    if digits.startswith(_SSE_CONVERTIBLE_PREFIXES):
        return "SSE"
    if digits.startswith(_SZSE_CONVERTIBLE_PREFIXES):
        return "SZSE"
    if digits.startswith(("4", "8")):
        return "BSE"
    if digits.startswith(("5", "6", "9", "019", "020")):
        return "SSE"
    return "SZSE"


def classify_cn_code(code: str, exchange: str | None = None) -> AssetClass:
    """Best-effort asset class for a CN listed code."""
    digits = "".join(ch for ch in str(code or "") if ch.isdigit()).zfill(6)
    venue = canonical_exchange(exchange) or cn_exchange_for_code(digits)

    if venue == CN_FUND_EXCHANGE:
        return AssetClass.MUTUAL_FUND
    if venue == "BSE":
        return AssetClass.EQUITY

    if venue == "SSE":
        if digits.startswith(_SSE_CONVERTIBLE_PREFIXES):
            return AssetClass.CONVERTIBLE_BOND
        if digits.startswith(("018", "019", "020", "0197", "0198")):
            return AssetClass.BOND
        if digits.startswith(("510", "511", "512", "513", "515", "516", "517", "518", "560", "561", "562", "563", "588")):
            return AssetClass.ETF
        if digits.startswith("50"):
            return AssetClass.MUTUAL_FUND
        if digits.startswith(("600", "601", "603", "605", "688", "689", "900")):
            return AssetClass.EQUITY
        if digits.startswith("000"):
            return AssetClass.INDEX
        return AssetClass.EQUITY

    # SZSE
    if digits.startswith(_SZSE_CONVERTIBLE_PREFIXES):
        return AssetClass.CONVERTIBLE_BOND
    if digits.startswith("159"):
        return AssetClass.ETF
    if digits.startswith(("15", "16", "18")):
        return AssetClass.MUTUAL_FUND
    if digits.startswith(("10", "11", "12", "13")):
        return AssetClass.BOND
    if digits.startswith("399"):
        return AssetClass.INDEX
    if digits.startswith(("00", "30", "20")):
        return AssetClass.EQUITY
    return AssetClass.EQUITY
