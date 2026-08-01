# Current Development

- Project: **OMNIX-Caishenfolio**
- Status: **R0~R5 + V1~V4 + A1~A4 + S1~S2 + F1~F5 + G1~G4 + H1~H4 完成 (v0.12.0 / R5)**
- 覆盖: A股 / 港股 / 美股 / 日股；股票 / ETF / 场外基金 / 债券 / 可转债 / 外汇 / 现金

## 已交付

- **R0~R5**：数据语义 → 账本内核 → 估值与收益 → 行情通道 → 桌面双主线 → CSV 导入导出
- **V1~V4**：版本号单一来源（`VERSION` / `PHASE` + 漂移守护测试）、WiX MSI 打包、
  应用内更新检查（GitHub Releases）、CI / Release 工作流
- **A1~A4**（移植自归档 FinWorkbench）：Python 运行时自动供给（uv/venv + 依赖哈希）、
  UI 启动冒烟脚本、组合风险指标（集中度/回撤/配置偏离）、价格提醒
- **S1~S2**：偏好设置持久化（schema v5 `settings`）与设置窗口
- **F1~F5**：技术指标库、诚实回测（成本/样本外/回撤/连亏）、估值分位与基本面、
  汇率利差看板、打新记录
- **G1~G4**：解读层（分档 + 白话解释 + 历史条件收益）、打新日历、界面全接、回测报告页
- **H1~H4**：
  - H1 类型化数据源错误（`market/errors.py`）+ 仅对可重试错误退避重试
  - H2 第二数据源：baostock（A股历史）、天天基金 pingzhongdata（场外基金净值，纯标准库）
  - H3 真实政策利率：纽约联储 EFFR / 欧央行 MRO / 香港金管局 / LPR / 日本央行，
    缓存一天，取数失败回落内置值并标记 `stale`（界面标红）
  - H4 外部通知：企业微信/飞书/钉钉/Telegram/Discord/Slack/自定义 webhook + SMTP 邮件；
    `--notify` 无界面模式 + Windows 计划任务注册（仅用户点击时执行）；
    凭据经 DPAPI 加密后才写入账本

## 验证

```powershell
dotnet build Caishenfolio.slnx; if ($?) { dotnet test Caishenfolio.slnx }   # 338 通过 / 0 警告
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
python -m unittest discover -s tests/python -p "test_*.py"                  # 316 通过
scripts\ui_smoke.ps1                             # XAML 改动必跑；含设置窗口加载检查
dotnet build packaging\windows\Omnix.Installer.wixproj -c Release           # 出 MSI
```

后台检查手工验证：`Caishenfolio.Desktop.exe --notify` 应无窗口退出（code 0），
并在 `%LOCALAPPDATA%\Caishenfolio\logs\notify.log` 追加一行。

## 未做

- 旧研究页（1225 行 code-behind）未 MVVM 化
- akshare 债券 / 汇率接口未联网验证
- `release_bundle.ps1`（manifest + checksums + 分步报告）未移植，当前由 release.yml 简化承担
- 净值曲线尚无图表展示（数据已存在 `valuation_snapshots`，只用于算回撤）
- 估值分位仅 A 股（上游数据限制）
- 后台 `--notify` 只查打新时限；价格/集中度提醒需要分析核心，未在无界面模式下启动
- 通知设置界面目前只支持一个 webhook 渠道（数据模型已支持多个）

- GitHub: https://github.com/plnoble/OMNIX-Caishenfolio
