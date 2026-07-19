# Caishenfolio Data Semantics (P0)

## Coverage (product intent)

A-share, Hong Kong, US equities, and ETF. P0 defines contracts; live providers come later.

## Symbol Identity

Internal format: `EXCHANGE:SYMBOL`

Examples:

- `SSE:600000`
- `SZSE:000001`
- `HKEX:00700`
- `NASDAQ:AAPL`
- `NYSE:BRK.B`

Display layers may localize codes; storage, tasks, and APIs use the internal form.

## Markets and Asset Classes

- Markets: `ASHARE`, `HK`, `US`, `ETF` (ETF may also appear as asset class on an equity exchange)
- Asset classes: `EQUITY`, `ETF`, `INDEX`, `FUND`

## Adjustment

Explicit policy required for historical bars:

- `raw`
- `forward`
- `backward`
- `unknown`

Do not mix adjustment policies in one analysis window without conversion metadata.

## OHLCV Bar

Required conceptual fields:

- `timestamp_utc`
- `open`, `high`, `low`, `close`
- `volume` (amount optional)
- `currency`
- `adjustment`
- `provider`
- field-level `provenance` when available

## Provider Result

Every provider call returns:

- `data` or empty
- `warnings` (quality / delay / cache / structure)
- `provider`
- `ok` success flag
- optional latency / cache metadata

Failure mode: **fail-closed**. Never invent prices to fill gaps.
