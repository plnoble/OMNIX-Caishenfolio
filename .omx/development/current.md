# Current Development

- Project: **OMNIX-Caishenfolio**
- Status: **R0~R5 完成 (v0.10.0)** — 已重构为个人多市场多资产理财软件，账本与研究双主线
- 覆盖: A股 / 港股 / 美股 / 日股；股票 / ETF / 场外基金 / 债券 / 可转债 / 外汇 / 现金
- **R0** 数据语义：`MarketRegion` 与 `AssetClass` 分离、`Money`(decimal)、交易所注册表、CN 代码分类
- **R1** 账本内核：账户/标的/13 种流水/移动加权平均成本、SQLite 版本化迁移、旧成交台账迁入
- **R2** 估值与收益：多币种折算（三角换算）、未定价 fail-closed、XIRR/Modified Dietz/TWR、资产配置
- **R3** 行情通道：quote / nav / fx 三通道，日股 TSE、场外基金净值、可转债与债券、汇率
- **R4** 桌面双主线：左栏「我的资产（总览/持仓/账本）」+「研究（行情/计划/网格/回测/对比）」+ 系统
- **R5** CSV 导入导出：预览校验后提交、按内容哈希幂等、报表导出
- 验证: `dotnet build` 0 错 0 警；C# 183 测试全过；Python 82 测试全过；桌面启动冒烟通过
- 未做: 旧研究页 code-behind 未 MVVM 化；akshare 债券/汇率接口未联网验证；安装包仍推迟
- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio
