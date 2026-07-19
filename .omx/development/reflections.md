# Reflections

## 2026-07-19 - P0 complete

- What worked: naming + dedicated root first avoided contaminating legacy tree; dual C#/Python security primitives keep the authority boundary honest from day one.
- Waste: first protocol init without cd into target directory.
- Improvement: bootstrap scripts should accept `-ProjectRoot` explicitly.
- Keep for next phase: verify script pattern (`scripts/verify_p0.ps1`) before claiming milestones done.

## 2026-07-19 - P1 complete

- What worked: dual-sided task store + fixture provider before rich UI; stdlib HTTP unblocked Desktop health without packaging Python deps.
- Waste: namespace `Market` collision — rename early when domain nouns are short.
- Improvement: keep `verify_pN.ps1` as the milestone gate; legacy workspace only holds pointer to Caishenfolio.

## 2026-07-19 - P2 complete

- What worked: building UI and research command on existing Core routes; Host SQLite mirror keeps durability without forcing Python to own State root yet.
- Waste: first SQLite tests failed only on cleanup due to connection pooling — disable pooling early for local DB files.
- Improvement: when adding persistence, set `Pooling=false` (or clear pools on dispose) and make cleanup best-effort in tests.
- Keep: `ITaskStore` abstraction so InMemory remains the unit-test default.

## 2026-07-19 - P3 Chinese UI + real market

- What worked: env-selected provider avoids polluting offline tests; fail-closed held when proxy blocked Eastmoney.
- Waste: none major; live probe needed network that CI/dev proxy may not allow.
- Improvement: never silent-fallback real→fixture (would lie about data truth).
- Keep: optional live tests gated by `CAISHENFOLIO_RUN_LIVE_MARKET_TESTS=1`.

