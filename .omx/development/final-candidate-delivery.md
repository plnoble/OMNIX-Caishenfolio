# Final Candidate Delivery — fa1-20261007-1410

> 最新状态（2026-10-08）：统筹已直接完成 FA1-R01/R02/R03 收尾，机器门禁通过，完整人验未完成。以 `final-candidate-repair-2026-10-08.md` 为准；下文保留原交付及第一次终审的历史口径。

日期：2026-10-07。任务包：`agent-stage-final-acceptance-2026-10-07.md`（FA1）。
证据目录：`.local/acceptance-final/fa1-20261007-1410/`（本文档只做索引，不复制证据正文）。

**统筹复核（同日）：R7 功能整改通过，完整 FA1 暂不通过。** 最新结论见
`final-candidate-review-2026-10-07.md`。统筹直接修复了本包 WpfTestHost 会误启动真实桌面的 P1，
修复后 C# 616、独立真进程 5/5、负例 3/3；产品源码和 MSI 未变。下表保留执行方原始交付计数，
但旧普通单测的隔离保证、全部真进程自启推断与启动抖动归因不可直接沿用。
两次 UI 成功 JSON 保留；首次引导等待/归属、重试日志覆盖、启动超时与人工项目仍待闭环。

---

## 1. 候选身份

| 项 | 值 |
| --- | --- |
| 候选 ID | `fa1-20261007-1410` |
| 分支 / HEAD | `main` / `0c3e3b3460893588602a95f01c58b6c50560c678`（未提交工作区，与本任务书开始时一致） |
| 版本 / 阶段 | 0.13.3 / R5（`VERSION` / `PHASE`，未改动） |
| 源码指纹 | `source-manifest.txt`（287 个文件 SHA-256，含全部未跟踪实现；冻结于 A 包完成后、门禁执行前） |
| 冻结后漂移 | 仅 `scripts/ui_smoke.ps1`（冒烟步骤扩展）与 `scripts/ui_dpi_probe.ps1`（坐标缺陷修正），均为验收脚本；产品/测试源码零漂移。清单前后对比：`source-manifest.pre-freeze.txt` |
| 环境 | `env-manifest.json`：.NET SDK 10.0.204，managed venv Python 3.12.11，冒烟 exe（Debug）SHA-256，显示器 200%（192 DPI） |

## 2. 本次变更清单（相对第八轮复审时的工作区）

生产代码仅两处最小可测试性改动，R7-02 的请求归属状态机本身是开发方既有实现，本轮用正式测试验证：

| 文件 | 变更 | 原因 |
| --- | --- | --- |
| `src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs` | `LoadButton_OnClick` 改为薄封装；核心提为 `internal Task LoadCarryAsync()`（逻辑逐行未变） | 任务书 A1：可 await 的逻辑入口，测试不靠点 UI |
| `src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs` | `BuildTransaction` private→internal（加注释） | 任务书 A2：正式测试走真实构造路径 |
| `tests/.../WpfTestHost.cs`（新） | 单 STA 线程持有桌面 App + Dispatcher 的测试宿主 | WPF 控件级测试的公共基础设施 |
| `tests/.../FxCarryRequestOwnershipTests.cs`（新） | 7 个请求归属回归 | A1 长期回归 |
| `tests/.../LedgerEntryTypeBoundaryTests.cs`（新） | 3 个类型边界回归 | A2 长期回归 |
| `scripts/ui_smoke.ps1` | 新增 2 个关键步骤（`fx range switch e2e`、`ledger type switch e2e`）；关键步骤跳过即整体失败；清理隔离根前留存 `core-runtime.log` | 任务书 B：当前阶段的真实页面验证 |
| `scripts/ui_dpi_probe.ps1` | 鼠标命中坐标去掉 `/scale` 除法 | 200% 下命中全落在上方表格（探针缺陷，非产品缺陷）；100% 行为不变（除以 1） |

## 3. 验收映射（任务书条目 → 正式测试/证据 → 状态）

| 任务书条目 | 对应正式测试（`dotnet test` 入口） | 状态 |
| --- | --- | --- |
| A1 顺序 1→5→10→1：标题/行/说明同区间、请求长度正确 | `FxCarryRequestOwnershipTests.SequentialRangeChangesKeepRowsHeaderHelpAndStatusOnOneRange`（断言 days=365/1825/3650/365） | passed |
| A1 1 年成功后 5 年失败 | `...FailureAfterASuccessKeepsTheLastSuccessfulRange` | passed |
| A1 首次请求失败（不宣称已有成功结果） | `...FirstRequestFailureNeverClaimsAPreviousSuccess` | passed |
| A1 5 年慢、10 年快（旧成功不覆盖；等待旧任务真实结束，无 sleep） | `...LateOldSuccessDoesNotOverrideTheNewerSelection` | passed |
| A1 新成功之后旧失败 | `...LateOldFailureDoesNotOverrideTheNewerResult` | passed |
| A1 旧 finally 不解除当前 busy；当前请求结束后恢复 | `...FinishedOldRequestDoesNotReleaseTheBusyWindowOfTheRunningRequest` | passed |
| A1 初始/核心未就绪/各区间：年化列量纲恒定、说明不固定一年 | `...AnnualizedColumnHeaderIsConstantAcrossInitialNoCoreAndEveryRange` | passed |
| A2 入金→买入（无关旧金额不阻断；必需数量/价格仍拒绝） | `LedgerEntryTypeBoundaryTests.DepositToBuyIgnoresStaleAmountButStillRejectsRequiredTradeFields`（断言 Kind/Quantity/Price/Currency） | passed |
| A2 买入→入金（无关旧标的/数量/价格/费税不阻断；金额必需） | `...BuyToDepositIgnoresStaleTradeFieldsButStillRequiresAnAmount`（断言 CashAmount/Currency/Symbol 为空） | passed |
| A2 买入→换汇（只解析回单字段；两边金额校验不放宽） | `...BuyToFxExchangeParsesOnlyTheReceiptFieldsAndKeepsBothAmountsRequired`（断言双边金额/币种/费率） | passed |
| A2 R7-03 相对路径/正式根回归保持 | 既有 `PathRootPolicyTests` / `TestIsolation` 负例（上轮已入正式入口） | passed（随 C# 全量） |
| B 真实页面：区间切换与加载中禁用/恢复 | `ui_smoke.ps1` 步骤 `fx range switch e2e` | passed（两次冒烟均过） |
| B 真实页面：隔离账本中类型切换与记账 | `ui_smoke.ps1` 步骤 `ledger type switch e2e`（含 R7-01 页面级残留金额场景） | passed（两次冒烟均过） |
| B 其余冒烟步骤（品牌/版本、九页导航、设置窗口、6 成本框布局/可写、逐路径净额、防串线、成本留空不伪装净额） | `ui-run-1-report.json` / `ui-run-2-report.json` 18 步全过、无关键 skipped | passed |
| C1 当前缩放（200%）鼠标命中/Tab/可见性 | `dpi-current-probe-clickfix.json` | passed（仅当前缩放，见 §5） |
| C1 100/125/150% 矩阵、1280×720 正式工作区、讲述人逐卡 | `final-candidate-human-verification-2026-10-07.md` | blocked（需用户切换显示环境；历史通过保留为旧版证据） |
| C2 真人十一项 + §6 七项 | 同上清单第 4 项 | not_run（需真人） |
| C3 Release MSI 构建/内容核对 | `msi-build.log` / `msi-fingerprint.json` | passed（安装/升级未授权，未执行） |

## 4. 机器门禁总表（全部在最终源码上执行；时间线与每次重试见 gates-log.md）

| 门禁 | 命令 | 退出码 | 结果 | 日志 |
| --- | --- | --- | --- | --- |
| 完整构建 | `dotnet build Caishenfolio.slnx --no-restore` | 0 | 0 警告 / 0 错误（脚本改动后增量复核 `build-final.log` 亦 0/0） | build.log |
| C# 全量 | `dotnet test Caishenfolio.slnx --no-build --no-restore` | 0 | **615 通过**（Host 570 + Desktop 45，含新增 10 项），0 失败 / 0 跳过 | csharp.log |
| Python 全量 | managed venv，`PYTHONPATH=python`，fixture，`-W error`，`unittest discover` | 0 | 449 项：**448 通过 / 1 skip**（`AkshareLiveNetworkTests`，按设计需 `CAISHENFOLIO_RUN_LIVE_MARKET_TESTS=1` 的真实联网测试） | python.log |
| 真进程链路 | `CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS=1` + 三类 filter | 0 | **5/5**：`StartsOrReusesACoreValuesTheLedgerAndStopsWhatItStarted`、`CostedNetArrivalRulesEvaluateIndependentlyThroughTheRealChain`、`ExplicitZeroSpreadWithFixedFeeProducesANetFigureThroughTheRealChain`、`ExistingCnyCashBuyingUsdConservesCurrencyWeightsThroughTheRealChain`、`ExternalFundingDilutesTheOldPositionThroughTheRealChain`。**本轮 8765 在测试前后均空闲（netstat 复核），五项均为本轮自启 fixture 核心**（与第八轮“复用既有核心”情况不同） | real-process.log |
| 生产负例 | `scripts/ui_smoke_negative.ps1` | 0 | **3/3**（最终版冒烟脚本上重跑，关键步骤清单扩展后仍通过） | negative-report.json |
| UI 冒烟 #1 | `scripts/ui_smoke.ps1 -ReportPath <证据>/ui-run-1-report.json` | 0 | **18/18 步 passed**，无关键 skipped；隔离状态根、provider=fixture、synthetic=true、健康 pid 属本次桌面进程树 | ui-run-1-report.json |
| UI 冒烟 #2 | 同上（串行、同一冻结源码） | 0 | **18/18 步 passed** | ui-run-2-report.json |
| 空白检查 | `git diff --check` | 0 | 无空白错误（仅仓库固有的 LF/CRLF 提示） | git-diff-check.log |
| Release MSI | `dotnet build packaging/windows/Omnix.Installer.wixproj -c Release` | 0 | 0 警告 / 0 错误 | msi-build.log |

### UI 冒烟重试记录（如实）

执行方记录：#1 被既有核心占用阻挡，#2/#3/#4 修正脚本缺陷，#5–#8 与 #10 出现核心 60 秒未就绪，
#9 出现净额等待超时，#11 与 run-2 的 JSON 为 18/18 成功。统筹核对发现总尝试数、#9 结束、#11
完整命令/退出码记录不齐，失败现场又被同名日志覆盖，不能声称“11 次全部完整留档”或精确计算失败率。
启动失败原因仍为 unresolved；成功运行与桌面无异常不足以排除代码/脚本问题。最终两份成功报告不能
单独覆盖这个可靠性缺口。见终审 FA1-R02/R03；原证据不改写、不补造缺失现场。

## 5. 显示 / 读屏 / 真人矩阵

新候选独立记录（旧报告保留为历史，不迁移）：

| 项 | 执行者 | 环境 | 结果 | 证据 |
| --- | --- | --- | --- | --- |
| 当前缩放（200%/192DPI）：6 成本框可见、真实鼠标点击命中并取得焦点、Tab 顺序遍历全页并进入导航、焦点可离开、1020×680 逻辑窄窗下全部可用 | 执行 Agent（探针 + UIA） | 本机 200% | **passed（仅当前缩放）** | dpi-current-probe-clickfix.json（6/6 hit=focus=目标框）、dpi-current-probe.json |
| 100/125/150% DPI 矩阵 | 待用户 | 需切换系统缩放 | **blocked** | 清单第 1 项 |
| 1280×720 正式工作区 | 待用户 | 需切换缩放/分辨率 | **blocked**（200% 下逻辑桌面仅 1440×900，无法物理重现 1280×720 逻辑工作区；1020×680 逻辑窄窗证据为补充） | 清单第 2 项 |
| 讲述人逐卡读取结果卡（币种、毛/净、成本未知状态） | 待用户 | 讲述人 | **blocked** | 清单第 3 项 |
| 真人十一项 + §6 七项（无口头指导） | 待真人 | — | **not_run** | 清单第 4 项 |
| MSI 安装/升级 | 待授权 | — | **not_run** | 清单第 5 项 |

历史参考（旧构建，不作为本候选通过依据）：同日早些时候用户配合完成过 100/125/150%、窄窗与讲述人基础项，
见 `acceptance/manual-acceptance-2026-10-07.md`。

## 6. MSI 产物

| 项 | 值 |
| --- | --- |
| 路径 | `packaging/windows/bin/x64/Release/OMNIX-Caishenfolio.msi` |
| 大小 / SHA-256 | 2,375,680 字节 / `3F600334285F4F04A7329BAE86F525EB94FBDC729C319C8F6E527F72CEB4A405`（msi-fingerprint.json） |
| 版本 / 产品 | 0.13.3 / OMNIX-Caishenfolio（ProductCode `{A1CE1683-…}`，管理提取核对） |
| 内容核对 | 内嵌 Python 源码与工作区字节一致（fx_paths.py / evidence_card.py / pinyin_index.py 抽样全量比对）；无账本、凭据、缓存或日志数据（`credentials.py` 为产品源码模块，非数据） |
| 安装/升级 | **未执行**（未获授权；构建成功不等于安装升级成功，已列入待验收清单） |

## 7. 正式数据与进程安全记录

- 正式账本主文件（`%LOCALAPPDATA%\Caishenfolio\state\portfolio.db`）：验收开始、门禁结束、最终收尾
  三次指纹一致（SHA-256 `2BBA1EA0…C0BB`，110,592 字节，最后写入 2026-10-07 10:51:12 UTC+8）；
  三次观测均无 WAL/SHM 边车（ledger-before.json / ledger-after.json / ledger-after-final.json）。
  此结论仅覆盖主文件指纹，不外推为整个状态目录审计。
- 进程清理（均为本会话创建、验证归属后处理）：8 个 `caishenfolio_core.server` fixture 进程
  （14:22–14:58 间由执行 Agent 的 fx/carry 探针遗留）按进程级精准停止（orphan-core-cleanup.json）；
  KeepOpen 诊断残留 1 桌面 + 1 核心链按 pid 停止；最终收尾时发现 exe 可启动性诊断桌面（pid 22972，
  已停）的孙进程核心链仍占 8765（探针/诊断脚本只清理直接子进程的缺口），验证归属后停止并复验
  账本指纹未变（orphan-core-cleanup-2.json）。两次正式冒烟与探针各自清理自有进程树。
  会话开始时 8765 空闲，不存在用户自有核心被波及；最终状态无监听、无 python 残留。
- 未提交、未推送、未发布、未安装/卸载，未改动 VERSION/PHASE、正式数据目录与全局显示设置。

## 8. 最终剩余问题与需要用户决定的事项

1. **启动可靠性未闭环**：原因未定位，不能归为“与源码无关”。需按终审 FA1-R02/R03 修正引导等待/
   归属与逐次证据留存后诊断复验；本轮发现的测试宿主误启动已修复，但不宣称它解释全部历史失败。
2. **需要用户/真人的集中清单**：`acceptance/final-candidate-human-verification-2026-10-07.md`
   （DPI 矩阵、1280×720、讲述人逐卡、真人十一项、MSI 安装升级、真实数据源/断网/外发提醒、提交/推送/发布授权）。
3. 既有遗留（非本阶段范围）：`ui_smoke.ps1` 的 UIA SetFocus 在本机对 6 个成本框持续不可用
   （冒烟步骤如实记录“UIA 可写”，鼠标命中已由探针补足）；冒烟/探针输出在 GBK 控制台有乱码（内容不受影响）。
4. 完成层级：统筹已接收并完成本次审核，R7 功能通过；完整阶段未通过，不能把剩余工作全部归给真人。
