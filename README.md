# OMNIX-Caishenfolio

**OMNIX** 品牌 · Windows 本地**个人多市场多资产理财工作台**。

- 产品名：OMNIX-Caishenfolio
- 版本：见 `ProductInfo.Version` / 界面右上角徽章
- 市场：A股 / 港股 / 美股 / 日股
- 品种：股票 / ETF / 场外公募基金 / 债券 / 可转债 / 外汇 / 现金
- 双主线：**我的资产**（总览 / 持仓 / 账本）+ **研究**（行情 / 计划 / 网格 / 回测 / 对比）
- 不做：真实券商下单、交易所执行
- 输出：所有研究/模拟结论必须标注「研究/模拟结论，非投资建议」

## 理财能力

| 能力 | 说明 |
|---|---|
| 多账户账本 | 证券 / 基金平台 / 银行 / 现金；同一账户可并存多币种余额 |
| 交易流水 | 买入、卖出、分红、送股、拆股、利息、出入金、费用、税、换汇、期初持仓/现金 |
| 成本与盈亏 | 移动加权平均成本；买入费税进成本；已实现 / 浮动盈亏分开呈现 |
| 本位币估值 | 默认 CNY；汇率支持直接、逆向与经 USD 三角换算 |
| 收益指标 | XIRR（资金加权）、Modified Dietz、时间加权收益、年化 |
| 资产配置 | 按品种 / 市场 / 货币 / 账户四个维度聚合 |
| 风险提醒 | 集中度上限（持仓/品种/市场/货币/现金）、历史最大回撤、相对目标配置的偏离金额、计划买卖价触发 |
| 偏好设置 | 总览页「设置」：本位币、五类集中度阈值、目标配置（合计须 100%）、导入旧版成交台账 |
| 导入导出 | 通用 CSV 流水导入（先预览校验后提交、重复导入幂等）；持仓 / 配置 / 流水导出 |

**取不到价格或汇率时，该持仓会显式标记「缺价格」并让估值报告为不完整——绝不按 0 计入总额。**

## 技术栈

| 层 | 技术 |
|---|---|
| Desktop | WPF + .NET 8（中文界面） |
| Host Core | C#（路径根、权限、脱敏、loopback、进程 broker、SQLite task mirror） |
| Analytics Core | Python 3.12+（stdlib HTTP） |
| 行情 | 默认 **auto 多源组合**（akshare/yfinance 免费 + 可填 tushare/AlphaVantage 密钥）；`fixture` 仅演示 |
| IPC | REST（默认仅 loopback `127.0.0.1`） |

## 当前阶段

理财重构 **R0~R5 已完成**（v0.10.0）：

- **R0** 数据语义：市场与品种分离、`Money`(decimal)、交易所注册表（含日股 TSE / 场外基金 FUND / 汇率 FX） ✅
- **R1** 账本内核：账户 / 标的 / 流水 / 持仓 + SQLite 版本化迁移 ✅
- **R2** 估值与收益：多币种折算、XIRR/TWR、资产配置 ✅
- **R3** 行情通道：报价 / 基金净值 / 汇率三通道，日股与债券 ✅
- **R4** 桌面双主线导航 ✅
- **R5** CSV 导入与报表导出 ✅

早期阶段 P0~P5（地基、安全、Task/Artifact/Audit、行情 UI、真实数据源、研究工作台）均已完成。

## 真实行情说明（重要）

1. **默认数据源：`akshare`**（公开网页/接口聚合，非券商实盘推送）。
2. **绝不伪造 K 线**：上游失败、未安装依赖、网络不通时返回错误，`data=null`。
3. **不是全市场本地库**：按需向线上源查询；覆盖能力取决于 AkShare/上游。
4. 安装真实行情依赖：

```powershell
pip install "akshare>=1.14.0" "pandas>=2.0"
# 或
pip install -e "python[market]"
```

5. 强制演示合成数据（仅开发/离线）：

```powershell
$env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
```

6. 系统代理导致 `ProxyError`（东方财富连不上）时：

```powershell
# 忽略无效系统代理，仍走真实行情（不造假）
$env:CAISHENFOLIO_HTTP_TRUST_ENV = "0"
# 请先关闭正在运行的 Desktop，再重新启动
dotnet run --project src\Caishenfolio.Desktop\Caishenfolio.Desktop.csproj
```

程序也会在代理失败时**自动尝试一次直连**；若仍失败则 fail-closed。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify_p3.ps1
```

联网真实行情单测（可选）：

```powershell
$env:CAISHENFOLIO_RUN_LIVE_MARKET_TESTS = "1"
$env:PYTHONPATH = "$PWD\python"
python -m unittest tests.python.test_p3_akshare_provider -v
```

## 启动

```powershell
# Desktop
dotnet run --project src\Caishenfolio.Desktop\Caishenfolio.Desktop.csproj

# 或手动 Core
$env:PYTHONPATH = "$PWD\python"
$env:CAISHENFOLIO_MARKET_PROVIDER = "akshare"
python -m caishenfolio_core.server --host 127.0.0.1 --port 8765
```

## 参考与边界

旧工程 `D:\Agent\Project\金融工作台` 仅作能力参考，不作为代码基线。
