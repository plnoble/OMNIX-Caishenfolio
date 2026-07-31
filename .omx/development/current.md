# Current Development

- Project: **OMNIX-Caishenfolio**
- Status: **R0 完成 (v0.10.0)** — 重构为个人多市场多资产理财软件，账本与研究双主线
- 重构路线: R0 数据语义 → R1 账本内核 → R2 估值与收益 → R3 行情通道 → R4 桌面双主线 UI → R5 CSV 导入与报表
- **R0**: `MarketRegion`/`AssetClass` 分离、`Money`(decimal)、交易所注册表（含日股 TSE / 场外基金 FUND / 汇率 FX / 银行间 CNIB）、
  CN 代码分类（可转债/国债/ETF/指数按交易所判定）、阶段号单一来源
- 验证: `dotnet build` 成功；C# 83 测试全过；Python 60 测试全过（此前 6 个陈旧失败已收敛）
- Prior: P5 侧栏工作台、计划买卖价位、网格研究、对比、MA 回测
- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio
