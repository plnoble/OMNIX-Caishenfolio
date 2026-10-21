# Agent Handoff

## Latest — 2026-10-21 WP-04 待开工

main 在 `ed71b86`（v0.14.0，已推送），工作区干净。任务书
`docs/work-packages/WP-04-能删能改与小白瘦身.md` 已立项，**还没有 agent 开工**。

接手须知：

- 三条反馈是用户原话，统筹已全部接受并认了两处自己的设计错误。**不要重新论证该不该删、
  该不该少写字**，照任务书做。
- schema 要升 v11。A7 向量是整包最关键的一条：**迁移前后 `Replay` 逐字相同**。
  迁移改了任何已有数字就是失败，不是「可接受的差异」。
- F4 的五处跳过点必须全覆盖。只改 `Replay` 会让总览对、而 FX 批次和贡献拆分悄悄错。
- **F12 是本包最意外的发现**：小白首页上一个记账入口都没有（顶部只有「刷新」和「切换到
  专业模式」）。小白要记一笔必须自己翻到账本页，用那个八字段表单。用户说的「最简单的操作」
  不是太复杂，是**不存在**。B0 向量在修复前必须是红的。
- 换汇只换入口，**不新建第二条落库路径**（F14：逻辑已在 `RecordWindow` 的换汇分支里）。
  用户原话「重点是汇率」，而换汇现在藏在账本页类型下拉的第 N 项里。
- 任务书是 8 小时六阶段，带时间预算表。**阶段 6（全套检查 + Codex + 交付 MD）的 1 小时
  不可挪用**，第 7 小时必须停功能开始跑门禁。
- 原 WP-04（Codex #1/#2/#5 + WP-03 残余四项）顺延为 **WP-05**，不要在本包里顺手做。
  例外：预先裁定 5 必须在本包里定下来，因为作废天然会制造「追溯后余额为负」。
- 不碰用户真实账本与 `price_plan.json`（用户那个文件里现有 6 条计划线，是本包第 3 条的现场）。

## Previous — 2026-10-21 WP-03 收尾（未合并）

分支 `wp-03-balance-identity`，实现停在 `d61e3c2`，本窗口只加文档。不合并、不推送。

- 永久别名的运行时查找（`LegacyRuleKeys` / `LegacyAlertKeys` / `KeysOf`）已不在代码里。`LegacyLevel` 是不入库的派生值。`PortfolioAlert.LegacyRuleLevel` 仍在，但是空属性，没有使用点。
- 全新 worktree `wp-03-verify9` 对 `d61e3c2` 的全套检查退出码全是 0（Debug/Release 0 警告，C# 747，Python 467 加 1 skip，两个冒烟都过）。
- 第七轮 Codex（`0.163.0-alpha.11`，指定的 `0.163.0-alpha.10.2` 目录已不在）结论是「必须修」。报告：`docs/work-packages/WP-03-审核报告-第七轮.md`。交付报告 §5.7 与 §9 列出待统筹决定的条目。本窗口没有改产品代码。
- 下一窗口不要把 §10.1 里「五条由构造消失」当成已确认。审查认为 `C5-3` 未发现残留，其余四条仍在，而且「迁移失败就保持旧键」只对没有 Id 的规则成立。

## Previous — 2026-10-19 FX / Android 审查交付

本轮用户要求先阅读代码，再审查“外汇资产管理/换汇决策优先”的新定位，不立即重写。审查报告：
`D:/Agent/Worktrees/OMNIX-Caishenfolio/reviews/fx-android-architecture-review-2026-10-18.md（仓库外：含真实交易金额，公开仓库不放）`。原 PC 基线为 `a3afe7c`，开始时工作区干净。

- 已核对数据库 v9、回单/跨账户换汇/冲正/CSV、均价成本池、估值、FX 数据源与计算、提醒和相关页面。旧文档“跨账户/换汇 CSV 未做”不是当前代码事实。
- 当前没有 Lot/消耗链；旧五路径属于历史成本对当前参考价，旧 breakeven 单位 JPY/CNY，不是未来 USD/JPY 的路线临界。旧 split 也不是多档换汇。
- 建议保留正确事实/测试/普通资产功能；新增独立 FX 领域，在 Kotlin/JVM 本地实现确定性内核；Room 存事实，DataStore 存偏好，Worker 周期检查，不承诺秒级触线通知。新架构只是建议，尚未实施。
- 报告列出实证缺陷 FX-01/02 与负现金探针，以及提醒身份/UNKNOWN 的静态链路问题。不要误把旧测试全绿当成新需求已满足。
- 2026-10-18 验证：Python FX 64 pass；使用系统 3.13.7（预期 managed venv 不存在）；Host 与测试项目编译成功，testhost 连接超时，TRX 0 executed。隔离证据 `.local/fx-android-review-20261018/`；未启动正式桌面、触碰个人正式账本或外发通知。
- 本轮仅报告与开发记录变化，未改 C#/Python/XAML/schema/VERSION/PHASE，未提交/推送/发布。继续开发时以本次用户新定位为优先，先落实 A0 语义和迁移规则；不要自动恢复下方旧 TR1 分派任务。

## Historical Active Stage — 2026-10-08 TR1 试用准备

用户明确要求修复后继续安排下一阶段，而非停在报告。任务书：`agent-stage-trial-readiness-2026-10-08.md`。
已分派三个执行 Agent：A 结果卡可访问语义、B 数据失败/恢复契约、C 安装布局安全冒烟。
各包仅改自己文件及独立交付 MD；统筹串行构建/启动和集中审查，少量问题直接收口。
当前未提交成果不迁移至旧 HEAD；不安装/改显示/外发/提交/发布。FA1 通过作为历史基线，TR1 尚待本轮验证。

## Latest Completion — 2026-10-08 FA1 工程收尾

详见 `final-candidate-repair-2026-10-08.md`。统筹直接完成引导按 PID/窗口类型等待与关闭确认、
独立 attempt 日志及拒绝覆盖、时间/退出码/源码与二进制指纹、StartupOnly 冷启动模式和 10 项新增负例。
只改两个验收脚本；前批测试宿主 P1 保持修复，产品 C#/Python/XAML/MSI 未变。
10 月 7 日构建 0/0、C# 616、Python 448+1skip、负例 13、固定冷启动 5/5、完整 UI 两次各 18/18；
10 月 8 日补跑真进程 5/5，复算 292 文件零漂移、正式主账本指纹一致、无核心/相关端口残留。
证据 `.local/fa1-repair-20261007/`；attempt-index 列全 8 次启动：一次窗口/标题文本误判失败留档，
修正并冻结后五次冷启动和两次完整冒烟均过。原历史超时日志不可恢复，不再无据归为环境抖动。
FA1-R02/R03 关闭；剩余仅本批之外的显示/读屏/陌生新手/安装与授权渠道验收，完整产品验收未代签。
未提交/推送/发布/安装；用户无需再转交本批小修。

## Review History — 2026-10-07 FA1 集中终审

见 `final-candidate-review-2026-10-07.md`。R7-02 关闭，R7-01/R7-03 保持关闭；完整 FA1 未通过。
统筹直接修复新发现的 P1：WpfTestHost 创建生产 App 后泵 Dispatcher 仍会启动真实 MainWindow。
改为普通 Application 仅加载真实资源，并补空窗口/空 StartupUri 的长期断言。仅三个测试侧文件变化，
原候选 + review-1 指纹见 `.local/review-fa1-20261007/review-overlay-final.json`；不修改原冻结清单。
独立构建 0/0、C# 616（570+46）、真进程 5/5、负例 3/3。产品/Python/XAML/脚本/MSI 未变。
首次普通单测确实留下正式路径 cache+upstream 核心，本轮已核对归属后清理；最终无核心/相关端口残留，
账本主文件指纹与交付一致，不外推为正式目录未访问。历史“无真实桌面、五项全自启”声明不再直接采信。
剩余自动化范围：首次引导按 PID 等待与确认、每次尝试独立日志、冷启动超时定位、最后两次完整冒烟。
不要以重试绿灯推断纯环境原因；两份旧 JSON 保留为功能证据。人工/DPI/读屏/安装清单仍开放。
未提交/推送/发布/安装；不要求再次分发本轮已直接修复的小问题。

## FA1 Delivery — 2026-10-07（执行方历史交付；结论由上节终审覆盖）

执行 Agent 已按 `agent-stage-final-acceptance-2026-10-07.md` 完成收尾，交付索引
`final-candidate-delivery.md`，证据目录 `.local/acceptance-final/fa1-20261007-1410/`：
- A 包：`LoadCarryAsync`/`BuildTransaction` 可测试化（最小改动），新增 WpfTestHost + 10 个正式回归，
  Desktop 35→45；R7-02 请求归属、R7-01 类型边界全部有正式断言。
- B 包：同一冻结源码上门禁全过——构建 0/0，C# 615（570+45），managed Python 448+1skip（按设计），
  真进程 5/5（本轮五项均自启 fixture，8765 前后空闲），负例 3/3，`git diff --check` 0，
  两次隔离冒烟 18/18（含新增区间切换与类型切换记账真实页面步骤）。
- C 包：Release MSI 0/0 并核对版本/内容/指纹（未安装）；200% 当前缩放探针（真实鼠标命中 6/6、
  Tab 全页遍历、1020×680 逻辑窄窗）；100/125/150%、1280×720、讲述人逐卡、真人十一项、安装升级
  已集中列入 `acceptance/final-candidate-human-verification-2026-10-07.md`（blocked/not_run 如实）。
- 数据/进程安全：正式账本主文件前后指纹一致；本会话 8 个遗留 fixture 孤儿核心经归属验证后
  精准清理并留审计；未提交/推送/发布。
- 待统筹关注：约 4/11 次冒烟出现隔离核心 60 秒未就绪的环境抖动（详见交付 §4/§8），
  最终两次连续运行完整通过。

## Latest Execution Brief — 2026-10-07 最终候选验收任务包（已执行完毕，见上节）

用户采用“统筹规划—分包开发—集中验收”，任务书为 `agent-stage-final-acceptance-2026-10-07.md`。
开发方称第八轮剩余代码已修；静态核对看到了请求序号覆盖成功/catch/finally、加载时禁用区间、
首次失败状态与 XAML 说明修正。本次只制定任务，没有构建/测试重跑，不直接关闭 R7-02。
执行 Agent 负责一次完成长期回归、冻结候选、完整自动门禁和两次隔离 UI、显示/人验协调及 MSI 构建；
交付 `final-candidate-delivery.md` 和候选独立证据目录后再集中终审。不要继续逐个小修往返。
当前是大量未提交成果，不能从旧 HEAD 建空白分支遗漏代码；默认同一执行者在现有工作区收尾，
不得未经授权提交/推送/发布/停止既有进程。具体范围与完成层级见任务书。

## Review History — 2026-10-07 第八轮整改复审

详见 `code-review-2026-10-07-remediation-recheck-8.md`。R7-01、R7-03 功能缺陷关闭；
仅 R7-02（P2）未闭环：5 年旧响应仍覆盖新选 10 年结果，展开说明仍为过去一年，
初始年化标题未统一；首次失败还需区分没有成功结果。按报告补请求归属及正式回归。
独立实测构建 0/0，C# 605（570+35），managed Python 448 pass/1 skip，真进程 5/5，负例 3/3。
8765 预先已有 cache+upstream 核心；一个后台空账本测试按设计复用它，不能声称五项均自启 fixture。
未停止已有进程；两次 UI、DPI、窄屏、读屏、真人与 MSI 未执行。正式账本主文件测试前后指纹一致。
仅更新诊断和交接文档，无产品代码修改/提交/发布。完整整改及 UI 验收仍未通过，不重复开发已关闭项。

## Review History — 2026-10-07 第七轮整改复审

部分通过，完整整改验收暂不通过。详见 `code-review-2026-10-07-remediation-recheck-7.md`。
原 R6-01 P1（正式根接受/非法配置回退）修复，R6-02 标题代码契约与 R6-05 复制逻辑问题关闭。
剩余 3 P2：R7-01 现金→买入仍解析禁用旧金额；R7-02 请求失败/乱序导致区间标题与数据错配，
展开说明仍写一年，历史区间还被拼到当前年化利差标题；R7-03 IsPathRooted 仍接受盘符相对路径。
独立验证构建 0/0，C# 602（567+35），managed Python 448 pass/1 skip，真进程 5/5，负例 3/3。
反例来自未显示控件/生产函数，未连接账本，不是 UI E2E；两次 UI、DPI、窄屏、MSI 与真人未重跑。
只读账本元数据已为 2026-10-07 10:51（早于本轮测试），不得沿用旧“9 月 6 日未变”声明。
下一步按第七轮 §4 小步修复并冻结后重验；本轮未改产品代码、未提交、未发布。

## Review History — 2026-10-07 第六轮交付复审

暂不通过最终验收，详见 `code-review-2026-10-07-delivery-recheck-6.md`。
发现 R6-01（P1）：测试根接受正式根，非法配置静默回退正式根；
R6-02～05（P2）：品牌更名后冒烟仍匹配旧标题、入金仍解析隐藏数量旧值、
5/10 年查询仍标“1 年”、负例前两项只测复制逻辑。
独立复验：构建 0 错误/2 条 xUnit1030 警告；C# 599；Python 448 pass/1 skip；
门控真进程 5/5；负例脚本报告 3/3，但不等于生产守卫已被覆盖。
标题与隔离根经生产函数探针确认，隐藏字段反例经未显示控件的实际构造方法确认；
均未启动正式账本。当前完整 UI/DPI/MSI 未重跑，09:24 后界面变化不继承旧报告通过。
下一步按报告 §5 整改并冻结候选后复验；历史人验观察予以保留，不与当前版本验收混记。

## Delivery Execution — 2026-10-07

`agent-delivery-plan-2026-10-07.md` 的可执行部分已经落地，完整结论见
`delivery-report.md`。R5-01 现在拒绝非法测试根、只允许 fixture 强制、端口占用失败，
并用 `/health.process_id` 对上本次进程树。R5-02 的两次冒烟包含 0+0、0+1 USD、
3 USD 对 2 USD 防串线，以及绕行/持仓成本留空。R5-03 保留真实失败文本，并把焦点口径
限定为 UIA 可写；本机 SetFocus 全部失败，鼠标和 Tab 仍未测。

验证：构建 0/0；C# 599（Host 564 + Desktop 35）；managed Python 449 项
（448 pass / 1 skip）；门控真进程 5/5；`.local/acceptance/run-1-report.json` 与
`run-2-report.json` 均 passed。没有提交，没有发布，没有改用户账本。DPI 100/125/150、
窄窗口、鼠标命中、Tab、读屏和真人任务仍按
`acceptance/manual-acceptance-2026-10-07.md` 待用户或独立验收人执行。

## Execution Brief — 2026-10-07

用户要求给其他 Agent 一份可执行开发文件，已生成 `agent-delivery-plan-2026-10-07.md`。
该任务书随后已由本文件上方的 Delivery Execution 执行；本节保留为当时“只编写、未执行”
的历史记录。

## Status Check — 2026-10-07（非正式复审）

见 `development-status-2026-10-07.md`。第五轮整改实现与两份 2026-09-08 的 14 步
passed 报告已核对；今天独立重跑构建 0/0、C# 587、managed Python 449 项
（448 pass / 1 skip）通过。未重跑 UI、门控真进程或 MSI，仍需正式复审，不以状态盘点关闭问题。
盘点开始时 main 上有 57 个已跟踪改动、58 个未跟踪文件，暂存区为空；最新本地提交仍为
2026-08-20 的 0c3e3b3。下一步：正式复审 → 真人/DPI/无障碍验收 → 整理提交及发布。
下方旧 Verification 数字及部分“未做”清单已滞后，请按状态报告先校准，避免重复实现已有功能。

## Latest — 2026-09-08 第五轮整改交付（待复审）

第五轮复审的三项问题已全部整改，详见 `code-review-2026-09-08-ui-acceptance-recheck-5.md`
与 quality-gates.md 同日整改节：

- **R5-01 测试环境隔离**：桌面支持 Host 授予的 `CAISHENFOLIO_TEST_STATE_ROOT`（整棵状态树
  进隔离根，生产不设此变量零影响）；broker 在凭据注入**之后**应用
  `CAISHENFOLIO_TEST_FORCE_PROVIDER`（仅测试变量优先，生产优先级边界不变）；冒烟启动前
  预置隔离账本（全表 schema v9 + 一笔 100 CNY→2100 JPY 回单）；新增
  `core health reachable` 步骤——HTTP /health 断言 provider=fixture 且 synthetic=true，
  错源当场带 provider 名失败。负例脚本 `.local/ui-smoke/negative-provider-check.ps1`
  验证 auto 核心（cache+upstream/synthetic=false）被 gate 拒绝。
- **R5-02 路径级断言**：持有币种经真实 UI 路径（点「按账本填入」→ 断言 2100/100 与
  FxHeldCombo=JPY）；`Get-CardText` 从 Label 元素上溯卡片容器收集（Label 与金额是不同
  UIA 元素）；持仓卡（"已持有 JPY 直接换"）与新资金卡（"新 CNY 直接换"）逐卡独立断言
  0+0 净=毛、0+1USD 净=毛−1（±0.01）；绕行卡费用留空断言「成本输入缺失」；等
  "已比较 N 条路径"标记翻转后才取当前渲染（不匹配旧结果）。
- **R5-03**：失败文本先赋值再读 `.Current.Name`；焦点检查口径收紧为"UIA 可写/可聚焦/
  非屏外（鼠标命中与 Tab 遍历未测）"。
- **验证**：连续两次完整冒烟（`.local/ui-smoke/run-1-report.json` / `run-2-report.json`
  均 status=passed、14 步、failure=null）；构建 0/0；C# 587；managed venv 449（-W error）；
  门控真进程 5/5；MSI 0/0。用户正式状态不受影响（隔离根在仓库 .local 内，跑完即弃）。
- **仍开放（人工）**：DPI 三档、窄窗口/低分辨率、鼠标点击命中、Tab 遍历、真人新手任务。

## Review History — 2026-09-08 第五轮复审（原始结论，已被上方整改交付覆盖）

**第四轮代码通过仍有效；本轮 UI 补充验收暂不通过。** 详见
`code-review-2026-09-08-ui-acceptance-recheck-5.md`。
焦点/可写、布局、九页导航独立通过；新增净额 E2E 连续两次在场景 A 请求失败（HTTP 422），
场景 B 与后续设置窗口未执行到。实际 /health 为 cache+upstream、synthetic=false，
父进程 fixture 被桌面凭据配置覆盖；422 具体详情尚未取到，不推断为计算回归。
另有未选持有币种、首条文本断言无法证明持仓路径的覆盖缺口。
待修：R5-01 测试环境隔离、R5-02 路径级断言（P2）、R5-03 失败诊断/口径（P3）。
构建 0/0、C# 587、managed Python 449 项（448 pass / 1 skip）通过。
先按第五轮报告修验收脚本，再做 DPI / 窄窗口 / 鼠标 / Tab / 真人验收。

## Review History — 2026-09-07 第四轮代码复审及后续交付声明

本整改批次代码修复复审通过，R3-01（P1）与 R3-02（P2）已关闭，详见
`code-review-2026-09-07-remediation-recheck-4.md`。独立实测：单仓 27.2727%、28% 不误报、
非法日期不进标题；.NET 587、Python 449 项（448 pass/1 skip）、真进程 5/5、MSI 全过。
**后续进展（同日）**：复审 §5 前两项已程序化进 `scripts/ui_smoke.ps1`——成本框
焦点/可写/非屏外检查 + 真实表单→净额双场景端到端（0+0 净==毛；0+1USD 净=毛−1），
扩展冒烟 12 步全绿。**仍开放的人工项**：100/125/150% DPI、窄窗口/低分辨率、Tab 顺序
完整遍历、真人新手无指导操作。上述“12 步全绿”为当时交付记录，新增 E2E 的当前
验收状态以第五轮 Latest Review 为准；R3-01/R3-02 不需重做。

## Review History — 2026-09-07 第三轮复审（历史，已由第四轮覆盖）

暂不通过完整验收；详见 `code-review-2026-09-07-remediation-recheck-3.md`。
上轮布局重叠和卖出分布反例已关闭。待修：R3-01 外部资金买入未稀释旧持仓权重，
实际 27.27% 被算成 29.09% 并误报 28% 上限（P1，已有测试期望也错误）；R3-02 全部
市场日期无效时回退原始字符串进入标题（P2）。先完成这两项，再补桌面净额与多 DPI/
键盘验收。已独立验证：.NET 586、Python 441（1 skip）、真进程 4/4、UI 布局冒烟、
MSI 全过。下方“DoD 全过”属于整改交付时声明，现以本节及复审报告的验收边界为准。

## Current Objective

R0~R5、N0~N5 已交付。**当前：FA1 工程收尾通过；TR1 试用准备三包并行执行中，
完整产品验收仍等待人工/环境项目。** FA1 以 `final-candidate-repair-2026-10-08.md` 为最新结论；
人工清单仍见 `acceptance/final-candidate-human-verification-2026-10-07.md`。
不重复开发已关闭项、不扩功能、不未经授权提交或发布。

## What Changed (recheck-3 修复, 2026-09-07)

- **R3-01**：外部资金买入的单一持仓改金额口径 `(旧市值+新金额)/新总额`（复审反例
  27.2727% 而非 29.09%，28% 上限不再误触发）；旧错误测试期望一并纠正；跨维度一致性
  断言（标的 ≤ 地区/货币 after）；真链路断言 27.2727% + 非 risk_exceeded。
- **R3-02**：删除无有效市场日期时的回退分支——标题只来自严格校验过的日期；桌面空值
  显示「行情日期未知」；garbage/20250719/2026Q1/外部 garbage as_of 四反例（标题空、
  原值保留于 channel_as_of、格式警告在）。
- 门禁：C# 587 / managed venv 449（-W error）/ 真进程 5/5（新增外部资金链路）/ 冒烟含
  布局断言 / MSI 0/0。
- **UI 完整 DoD 未完成**（未测项如实）：成本框 Tab 焦点与点击命中、表单输入→净额的
  页面级端到端、100/125/150% DPI 三档、窄窗口——归真人 UI 验收。

## What Changed (recheck-2 修复, 2026-09-07)

- **F-01**：汇率页父 Grid 5 行（比例 Row2/固定费 Row3/结果 Row4）；**冒烟新增
  `Invoke-CostInputLayoutCheck`**——UIAutomation 断言 6 个成本输入框宽高>0 且两两不重叠。
- **F-02**：`_validated_distribution` 共用校验器（有限/非负/合计 1，买卖同源）；卖出地区
  切片与**完整持仓**比较（非钳制卖出量）；货币切片同规则；输出端二次不变量清空不自洽分布。
- **F-03**：`_is_iso_date` 正则+round-trip（拒 20250719、闰日边界正确）；主日期按解析值
  排序；任一通道有数据即扫描（仅异常财报期也告警）。
- **F-04**：预填 tooltip 改移动加权表述；费用完整性提示存字段、结果渲染后写回。
- 测试 +10（Python 441 / C# 586）；冒烟含布局断言；Release MSI 0/0。
- **遗留**：显式 CashTransfer、§10 真人 UI 验收（含多 DPI 目视）。

## What Changed (recheck 修复, 2026-09-06 夜)

- **P1-01** funding_currency 贯通（客户端参数+桌面传值+真进程链路测试：CNY 0.6→0.5/USD 0.4→0.5）；
  **P1-02** 卖出统一实际可卖权重（超卖现金 70% 非 140%；守恒校验共用；切片<持仓 fail-closed）；
  **P1-03** 汇率页固定费+币种输入（tri-state；非法文本提交前报错；真链路 0 点差+1 USD→净额=毛额−1）；
  **P1-04** 预填改 `LedgerHeldCostPrefill`（移动加权 200≠180；未知入账整体留空+说明）。
- **P2**：before/after DTO+BeforeAfterLines 渲染；日期格式异常警告（provenance 保留）；
  季度序号陈旧度（含 ISO→季度新 helper）；pypinyin None 协议双环境测试；
  quality-gates 三处记录更正；**正式 Python 门禁改 managed venv**。
- 测试：C# 586 / managed venv Python 434（-W error 零警告）/ 门控真进程链路 4/4。
- **教训沉淀**（error-ledger + skill-candidates）：参数贯通改动的验证必须是真进程链路
  测试，直调内核的测试只能证明内核。

## What Changed (整改执行, 2026-09-06 晚)

- **WP1** 权重守恒（地区/币种买入对冲、外部资金缩放、卖出转 cash、不守恒 fail-closed）；
  **WP2** 费用三态 None=未知/0=显式零（四路径+提醒+UI；顺带修复固定费未知当零）；
  **WP3** 外币成本逐笔重放（同日不再净额抵消；跨账户转账保守方案）；
  **WP4** market_as_of + 桌面分频道日期 + 分级新鲜度警告；
  **WP5** PercentInputParser 生产化、分批假测试更正、三组性质不变量、门禁记录诚实更正；
  **WP6** requests 链版本锁定。
- 测试：C# 579 / Python 425（`-W error` 零警告）/ 真进程后台 2 / UI 冒烟。
- **遗留（下一工作包）**：显式 `CashTransfer`（双账户原子转账 + CSV + 旧数据候选提示）；
  §13 九项真人 UI 验收；N7 收尾。

## What Changed (复审修复, 2026-09-06)

- **口径**：跨币种买入的目标/来源币种暴露分开更新；分批换汇排名值统一「每 base_amount
  总支出的目标币净额」量纲；两段路径固定费实扣；外币成本池按日聚合净变化、覆盖全部流水
  （提现/消费/手续费/买卖/利息）；显式 0% 目标保留（不再被 Validated 丢弃）。
- **契约**：max_loss API 统一 0~1（服务端拒绝越界、UI 一次换算并校验 0<x≤100）；
  风险原因结构化（持仓上限/最大损失各自成句，多触发并列）。
- **数据**：通道日期严格 ISO（报告期不参与顶部日期、旧超一年出警告）；北交所裸代码
  （43/83/87/88/92）不再落 FUND；后台 FxPathFacts 缓存键含费用模型。
- **基础设施**：schema v9 冲正过滤唯一索引（双实例守护）；证据卡地区/币种交易后权重
  贯通（未知明说）；15 处 SQLite 连接 contextlib.closing——`-W error::ResourceWarning` 全绿。
- 新增测试 +45（C# 571 / Python 406），含真进程后台链路 2 例（57s）。

## What Changed (审核修复, 2026-09-05)

- **口径修复**：模拟买入资金来源（ExistingCash/ExternalContribution，C#+Python+UI）；新增资金
  再平衡按「最小新总额」；TWR 快照=流后约定（当日入金不再是收益）；FX 路径比较统一
  comparison_basis；分批固定费全扣；净到账提醒必须带费用模型；外币持仓成本改移动加权
  （HeldCurrencyCostCalculator）；压力测试现金按币种聚合。
- **证据卡门禁**：数据质量降为 neutral 门禁不投票；真实报价容差贯通；max_loss_pct 贯通
  UI→客户端→结论门禁（risk_exceeded 优先）；as_of=各通道真实数据日 + channel_as_of。
- **数据完整性**：跨账户换汇 CSV 往返（模板「对方账户」列+确定性 ID）；裸代码市场歧义
  拒绝解析（IBM/7203 反例）；同批双冲正拒绝；回撤归因跨账户聚合守恒。
- **桌面端**：证据卡方向（买入/卖出）与资金来源下拉；净到账规则费用输入。
- 新增测试 +42（C# 541 / Python 391），全部为本轮反例或口径钉死。

## What Changed (N5 第一批, 2026-09-05)

- **`PortfolioContributionAnalyzer`**：盈亏贡献 + 汇率/涨跌精确拆分。三数模型：
  总览浮动盈亏（成本按今日汇率折算，构造上=涨跌部分）、汇率部分
  （成本×(r_now−r_cost)，成本日=最早建仓流水日，汇率取不晚于该日的最新快照）、
  相对实际投入（之和）。缺成本日汇率 → 「未知」+警告，不插值。
- **`TradeWhatIfAnalyzer`**：买入模拟（前后单一持仓/货币/现金占比 vs 自设阈值）；
  未定价拒绝、新标的需计价货币、现金不足明示；绝不写账本。
- **`PortfolioStressAnalyzer`**：用户填冲击（股票跌%/USD%/JPY%/最大持仓额外跌%）；
  债券/可转债/场外基金不套用股票跌幅（假设清单写明）；未定价排除并计数；
  现金按币种随汇率冲击；结果=假设分析。
- **`RebalanceComparisonAnalyzer`**：卖出超配 vs 新增资金补低配；只新增资金只能稀释超配
  （注记说明）；估值不完整拒绝。
- **应急金**：`RiskAnalyzer.Analyze` 新参 `emergencyFundBase`（workspace 传
  `Settings.EmergencyFundAmount`）；闲置现金=现金−应急金，警告点名应急金。
- **UI**：`RiskLabView`（新「风险」页：贡献表/买入模拟/压力情景/再平衡比较），
  `PortfolioViewModel.Workspace` 暴露 store（汇率历史）；冒烟九页导航。

## What Changed (N4 证据卡, 2026-09-05)

- **`research/evidence_card.py`**：九段证据卡（数据质量/估值/基本面/市场行为/历史
  检验/组合影响/正反未知证据/失效条件/结论状态）。纯组装、无模型、无时钟；
  **每条证据带 basis 溯源**；中性事实不进倾向计数；跨币种候选金额拒折算
  （None 非 0）；关键数据缺失 → 卡片停在「证据不足」。
- **结论顺序即策略**：风险超限 > 证据不足 > 冲突 > 倾向——用户自设上限一票否决。
- **禁语子串扫描**：`BANNED_PHRASES` 扫描全部字符串（含禁语"该买/买入依据"等
  否定句形式）；新增文案必须过措辞测试。
- **`POST /research/evidence-card`**：Task/Artifact/Audit 映射（artifact=卡片 JSON
  全文）；四通道（bars/quote/valuation/financials）独立降级——失败只进数据质量
  段的 missing，不垮整卡。
- **`PostEvidenceCardAsync` + `EvidenceCardView`**：结论横幅（状态色）、九段卡片、
  证据三列表格（立场/事实/依据）、历史回撤 vs 用户可承受损失对照提示；
  标的输入走 SymbolResolver（免内部代码）；左栏双模式可见；冒烟八页导航。
- 遗留（current.md「未做」）：滚动样本外与参数敏感性未进卡（可接既有
  `out_of_sample_report`）；卖出方向卡未做（需税费/机会成本列）。

- **N1 拼音检索**：`market/pinyin_index.py`（pypinyin 入 market extras；离线种子表
  内置人工核对拼音；转换器可注入）；`fuzzy_search_a_share` 对 ASCII 查询加拼音档，
  中文查询行为不变。
- **N1 TWR**：`WorkspaceSnapshot.Twr`（**同日外部流从 flows 剔除**——快照点已含当日
  流，双算会把平账记成 -50%，有测试钉死）；总览页 TWR KPI + 术语解释。
- **N1 折叠**：回测页成本/涨跌停六项进 Expander（默认收起）。
- **N1 流动性**：`PortfolioSettings.LiquidityNeed`（anytime/3m/1y/long）+ 引导第④问
  （七问全集）+ 首页档案卡。
- **N2 差值**：`fx_paths.py` 每路径 `diff_vs_best`（abs/pct/abs_in_target/basis）——
  **全比较统一单一基线**（全体净额或全体毛额，绝不混用；首版混用被探针测试逼出）；
  汇率页路径卡显示"距最优 …"。
- **N2.4 补全**：`NetArrivalAbove`（净到账 ≥ 目标，缺成本=note 不猜）与
  `BreakevenCrossed`（优势 ≥0 即越过平衡，重入语义覆盖方向翻转）；求值器重构为
  `FxPathFacts` 统一注入；设置页四类规则编辑（阈值标签随类型切换）。
- **N3 日历/时区**：`MarketCalendar`（CN/HK/US/JP 固定节假日 2025–2027；农历/浮动
  **明示不建模**，文案"约 N 个交易日"）+ `MarketClock`（交易所时区今天）；陈旧度
  按标的所属市场计算；§8 失败模式专项测试（限流/休眠漏跑/重复唤醒，按计划原词命名）。
- 事故 2 起（error-ledger）：差值首版基线混用（比较口径是**集合属性**）；
  split 段重犯 None 多义守卫缺失（修同类 bug 必须全模块扫调用点）。

- **去重/冷却/重入（`AlertGatekeeper` + `AlertNotificationState`）**：
  事件身份 = kind+symbol+title；条件重新进入（消失后再现）立即提醒，持续触发在
  冷却期内静默；**送达与观察分离**——只有真实送达才推进冷却，投递失败自动重试。
  状态 `state/alert-state.json`，30 天剪枝，损坏按空读。
- **`HeadlessNotifier` 两层架构**：层 1 打新时限永远跑（只读账本）；层 2
  （设置开关 `BackgroundExtendedChecks`）注入式扩展检查跑完整
  `PortfolioWorkspace.RefreshAsync`——同一求值器/阈值/交叉核对，前后台是同一句话；
  层 2 异常捕获为 limitation，绝不阻塞层 1；两层结果按身份合并。
- **`BackgroundExtendedCheck`（Host）**：探活 8765 复用已开核心；否则临时启动
  （venv 复用→broker→健康≤60s）并只停自己启动的。门控集成测试
  （`CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS=1`）真实验证过全链路。
- **消息卫生**：出站消息剔除「（备注：…）」+ 盘符路径脱敏（`SensitiveValueRedactor`
  新增 `LocalPathRegex`）。前台 UI 保留完整备注。
- **N2.4 换汇提醒规则**：`FxAlertRule`（RateBand 区间 / HeldAdvantageAbove 优势阈值，
  存 `NotificationSettings.FxAlertRules`）+ `FxAlertRuleEvaluator` 纯求值器（注入查询，
  缺数据=note 不猜测；措辞只有"你设定的事实被触发"，禁用语有测试守护）+
  `BackgroundExtendedCheck` 接线（区间走 `GetFxRateAsync` 去重缓存；优势从账本取
  持仓成本后调 `PostFxPathsAsync` 复用汇率页同一数学）+ 设置页规则编辑器
  （类型联动表单 + 启用勾选/删除）。新 `AlertKind.FxBand/FxAdvantage` 自动继承
  去重/冷却/重入与脱敏管道。
- **陈旧度交易日口径**：周末不计入报价年龄（周五报价周一查 = 1 个交易日）；
  节假日未建模，文案明示口径。
- **设置页**：「后台扩展检查」开关（声明卡即时联动）+ 冷却时间输入（1–720 小时，
  默认 20）；`NotifyLogReader.Read` 新增 `extendedChecks` 参数驱动动态 Scope 文案。
- **App.xaml.cs**：`--notify` 按设置组装扩展检查（核心根上溯 + 凭据 + 状态库）。
- 新设置字段：`NotificationSettings.BackgroundExtendedChecks` / `.AlertCooldownHours`
  （加密 JSON 自动持久化，旧设置读默认值）。

## How to navigate

1. **首页**（小白模式默认落地）— 四问 + 待办 + 档案 + 模拟入口
2. **总览** — KPI + 配置四维度 + 提醒区（含覆盖率红字条）
3. **持仓** — 明细、原币/本位币市值、浮动盈亏
4. **账本** — 建账户、记一笔（**免内部代码 + 换汇向导**）、CSV 导入、**冲正**
5. **看清楚**：估值/打新（真实账本）；**汇率**（研究模拟：利差 + **路径比较**）
6. **研究**（专业模式显示；小白模式从首页徽标入口进）
7. **系统** — 核心、数据源、导出、缓存

## Verification

```powershell
dotnet build Caishenfolio.slnx           # 0 警告
dotnet test Caishenfolio.slnx             # 480 pass（Host 459 + Desktop 21）
$env:PYTHONPATH="$PWD\python"; $env:CAISHENFOLIO_MARKET_PROVIDER="fixture"
python -m unittest discover -s tests/python -p "test_*.py"   # 371 pass (1 skip)
scripts\ui_smoke.ps1                      # 引导跳过 + 八页导航（含证据卡）+ 设置窗口
# 手工：Caishenfolio.Desktop.exe --notify → exit 0，无窗口，notify.log 追加
# 「已检查：打新时限 …」行；开启扩展检查后应显示「…价格与集中度」
```

XAML 有改动时**必须**再启动一次应用冒烟（见 error-ledger 2026-07-31）。
**edit 工具改 .ps1 后必须检查 BOM**（前 3 字节 EF BB BF，见 error-ledger 2026-09-05）。

## Conventions worth keeping

- 冲正链约束在 `PortfolioStore.ValidateReversals`（写入事务内）——任何新写入方自动受约束；
  新的流水入口不要再加"删除"逃生门。
- `RiskCoverage` 是纯领域语义（N5 归因还会用），别把它做成 UI 类型。
- 换汇记账按回单两边金额入账（`FxExchange`），UI 只展示成交汇率——**永不反推**。
- 路径比较的数学只在 `research/fx_paths.py`；账本事实（持仓/成本/余额）只在
  C# 侧读出后作参数传入——两侧解耦，别让 Python 直接读账本。
- **后台提醒的规则复用 = 跑同一个 `PortfolioWorkspace.RefreshAsync`**，不是重新拼装
  求值器；新增提醒类型时改 `PortfolioAlertEvaluator` 一处，前后台同时生效。
- **去重状态的双重事实**：`WasInRange/LastSeenAt` 每次观察更新，`LastNotifiedAt`
  只在真实送达后推进——新提醒类型自动继承该语义，无需单独处理。
- **换汇提醒的措辞由测试锁死**：`NoWordingEverClaimsAGoodTimeToConvert` 断言
  「最佳换汇时机/建议换汇/该换了」永不出现；新增 FX 规则类型时把其文案加进该测试。
- **优势类计算一律调 `/research/fx-paths`**：后台提醒、汇率页、未来任何入口共用同一
  数学（decisions「N2.4 优势规则复用 /research/fx-paths」）；不要在 C# 重写公式。
- 小白首页的待办推导是 `NoviceTaskList.Derive` 纯函数——改优先级/文案先改它和它的测试。
- 术语解释只有 `TermGlossary` 一个来源——新页面引用 key，不要另写一套解释文案。
- 载荷字段命名：`_pct` 结尾 = 百分数单位；`PathCost` 空 = 未知（gross_only），非免费。
- **外发消息走 `NotificationDispatcher`**：新增渠道无需关心脱敏；新加含用户输入的
  提醒文案时注意「（备注：…）」后缀会被出站剥离（前台不受影响）。
- MainWindow 已由 N1/N2 批次按计划 §13（Agent C 角色）修改过；后续 Agent 仍应避免堆逻辑
  进 MainWindow，新页面走独立 View。

## Next (optional)

- **真人验收**：§6 七项任务 + §12 十一脚本的真人执行（无口头指导的真实新手）；无障碍专项
- **N5 收尾**：行业集中度/相关性/ETF 重叠（计划 §10 后续能力，数据质量门槛未到）；利率→价格需久期数据后才可建模
- N4 补充：滚动样本外+参数敏感性进卡；卖出方向卡（税费/机会成本）
- N1 真人验收（计划 §6 七项任务）；N2 跨账户换汇/CSV 导入；旧研究页 MVVM 化；
  净值曲线图表；通知设置多渠道 UI；冒烟脚本 BOM 根治（迁 pwsh 7 或去中文注释）
- 本机状态库 onboarding 已被冒烟标记完成——手工验证引导窗口用首页「填写/修改档案」重开
