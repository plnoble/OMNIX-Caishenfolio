# OMNIX-Caishenfolio

**OMNIX** 品牌 · Windows 本地金融研究与模拟工作台。

- 产品名：OMNIX-Caishenfolio
- 版本：见 `ProductInfo.Version` / 界面右上角徽章

- 范围：研究、模拟、回测、导出、报告
- 不做：真实券商下单、交易所执行
- 输出：所有研究/模拟结论必须标注「研究/模拟结论，非投资建议」

## 技术栈

| 层 | 技术 |
|---|---|
| Desktop | WPF + .NET 8（中文界面） |
| Host Core | C#（路径根、权限、脱敏、loopback、进程 broker、SQLite task mirror） |
| Analytics Core | Python 3.12+（stdlib HTTP） |
| 行情 | 默认 **auto 多源组合**（akshare/yfinance 免费 + 可填 tushare/AlphaVantage 密钥）；`fixture` 仅演示 |
| IPC | REST（默认仅 loopback `127.0.0.1`） |

## 当前阶段

- **P0** 地基 + 安全 + 数据语义 ✅
- **P1** Task/Artifact/Audit + Fixture 行情 + Core Health ✅
- **P2** 行情 UI + SQLite task mirror + Research v0 ✅
- **P3** 中文界面 + 真实行情 provider（进行中/已接入代码） ✅ 门禁

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
