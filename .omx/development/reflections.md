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


## 2026-07-31 - R0~R5 理财软件重构

- What worked：先重构语义（市场/品种分离 + Money）再建账本，后面每一层都不需要回头改类型；
  每个阶段都用「先写会失败的断言、再实现」的方式，三处真实缺陷（FX 残差、超卖、未定价按 0）都是被测试逼出来的。
- What worked：把账本门面 `PortfolioWorkspace` 放在 Host 而不是桌面层，让整条理财工作流不开窗口也能测（183 个单测）。
- Waste：一次性把 `local:` XAML 前缀改指到新命名空间，撞坏了已有的 `PricePlanView`/`GridView`；
  新增前缀而不是改动既有前缀，成本为零。
- Waste：`Setter Property="Resources"` 走了一个编译期看不出的死胡同（见 error-ledger）。
- Improvement：涉及 XAML 的阶段，把「启动冒烟」写进验证清单，与 `dotnet test` 并列。
- Keep：fail-closed 一致贯穿到估值层——「取不到价」必须表现为估值不完整，绝不能表现为资产变少。
