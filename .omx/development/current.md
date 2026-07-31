# Current Development

- Project: **OMNIX-Caishenfolio**
- Status: **R0~R5 + V1~V4 + A1~A4 完成 (v0.10.0 / R5)** — 个人多市场多资产理财软件，已产品化
- 覆盖: A股 / 港股 / 美股 / 日股；股票 / ETF / 场外基金 / 债券 / 可转债 / 外汇 / 现金

## 已交付

- **R0~R5**：数据语义 → 账本内核 → 估值与收益 → 行情通道 → 桌面双主线 → CSV 导入导出
- **V1~V4**：版本号单一来源（`VERSION` / `PHASE` + 漂移守护测试）、WiX MSI 打包、
  应用内更新检查（GitHub Releases）、CI / Release 工作流
- **A1~A4**（移植自归档 FinWorkbench）：Python 运行时自动供给（uv/venv + 依赖哈希）、
  UI 启动冒烟脚本、组合风险指标（集中度/回撤/配置偏离）、价格提醒
- **S1~S2**：偏好设置持久化（schema v4 `settings`）与设置窗口（本位币/阈值/目标配置/导入旧台账）

## 验证

```powershell
dotnet build Caishenfolio.slnx; if ($?) { dotnet test Caishenfolio.slnx }   # 249 通过
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
python -m unittest discover -s tests/python -p "test_*.py"                  # 82 通过
scripts\ui_smoke.ps1                             # XAML 改动必跑；含设置窗口加载检查
dotnet build packaging\windows\Omnix.Installer.wixproj -c Release           # 出 MSI
```

## 未做

- 旧研究页（1225 行 code-behind）未 MVVM 化
- akshare 债券 / 汇率接口未联网验证
- `release_bundle.ps1`（manifest + checksums + 分步报告）未移植，当前由 release.yml 简化承担
- 净值曲线尚无图表展示（数据已存在 `valuation_snapshots`，只用于算回撤）

- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio
