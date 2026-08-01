# Agent Handoff

## Current Objective

R0~R5 已交付：OMNIX-Caishenfolio 从单标的研究工作台重构为个人多市场多资产理财软件（v0.10.0）。

## What Changed

- **数据语义**：`MarketRegion`(cn/hk/us/jp/global) 与 `AssetClass`(股票/ETF/场外基金/债券/可转债/外汇/现金/商品/REITs) 分离；
  `Money`(decimal + 货币)；`ExchangeRegistry`(C#) 与 `data/markets.py`(Python) 是交易所→地区/货币/时区/Yahoo 后缀的唯一来源。
- **账本**：`Host/Portfolio` 下 Account / Instrument / LedgerTransaction / PositionCalculator / PortfolioStore(SQLite v2)。
- **估值**：`FxConverter`(直接→逆→三角) + `ValuationEngine` + `ReturnMetrics`(XIRR / Modified Dietz / TWR)。
- **行情**：provider 新增 `latest_quote` / `nav_series` / `fx_rate`；路由 `/market/quote|nav|fx`；
  `AnalyticsCoreClient` 有对应类型化方法；`PortfolioPricingService` 把两者接起来。
- **桌面**：左栏两大区；新增 `Wealth/` 下 `WealthOverviewView` / `HoldingsView` / `LedgerView` + `PortfolioViewModel`。
- **导入导出**：`TransactionCsvImporter`(预览→提交，幂等) 与 `PortfolioReportExporter`。

## How to navigate

1. **总览** — 总资产 / 累计盈亏 / 成本 / 现金 / XIRR + 四个维度的资产配置
2. **持仓** — 各市场持仓明细、原币与本位币市值、浮动盈亏、占比、导出
3. **账本** — 新建账户、手工记一笔、CSV 导入（先预览后提交）、流水表、导出
4. **研究** — 行情 / 计划 / 网格 / 回测 / 对比（沿用既有实现）
5. **系统** — 核心、数据源、导出、缓存

## Verification

```powershell
dotnet build Caishenfolio.slnx
dotnet test Caishenfolio.slnx                     # 338 pass
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
python -m unittest discover -s tests/python -p "test_*.py"   # 290 pass
```

## 通知推送（H4）

设置在「理财偏好设置」窗口底部。填机器人 Webhook 地址 → 「发送测试通知」确认能收到 →
「注册每日后台检查」写一个 Windows 计划任务，每天 09:00 跑 `Caishenfolio.Desktop.exe --notify`。
凭据用 DPAPI（当前 Windows 账户）加密后存进账本的 `settings` 表，明文不落盘。
后台模式只查打新时限——它不需要行情，所以不会因为取不到价格而静默什么都不报。
每次运行都会往 `%LOCALAPPDATA%\Caishenfolio\logs\notify.log` 写一行，失败也写。

XAML 有改动时**必须**再启动一次应用冒烟（见 error-ledger 2026-07-31）。

## Next (optional)

- 旧研究页（1225 行 code-behind）MVVM 化，与新理财页统一
- 联网验证 akshare 债券 / 汇率接口；补券商专用对账单解析
- 净值曲线与收益归因图表；持仓再平衡建议
- 安装包仍推迟
