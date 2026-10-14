# 第七轮复审与 Agent 整改任务书

日期：2026-10-07。范围：R6-01～R6-05 的本轮修复及对应回归。

## 1. 结论

**部分修复通过，暂不通过本批完整整改验收。原 P1 的正式目录误入/非法配置回退已修复；仍有 3 项 P2 需要处理。**

基础回归全部通过，不等于动态表单和异步页面状态已正确。本轮没有修改产品实现，没有提交、推送、发布、删除文件或调用正式账本写入链路。按照 `agent-dev-protocol` 同步复审、门禁和交接记录。

| 上轮编号 | 本轮判定 | 依据与后续 |
| --- | --- | --- |
| R6-01（P1） | 原 P1 核心缺陷关闭；完整绝对路径子项未完成 | 正式根探针已拒绝；MainWindow 的 Invalid 分支位于数据库初始化前。另有盘符相对路径漏检，见 R7-03（P2）。不能写成“所有相对路径均拒绝”。 |
| R6-02（P2） | 标题契约代码修复通过 | 两处改为 Caishenfolio，版本校验保留；尚未以当前代码重跑两次完整 UI 冒烟。 |
| R6-03（P2） | 部分修复，未关闭 | 买入→入金会清空数量/价格；现金→买入仍解析禁用的旧金额，见 R7-01。 |
| R6-04（P2） | 部分修复，未关闭 | 年份能更新，但失败/乱序响应仍造成标题与数字不一致；说明与年化标题也未统一正确，见 R7-02。 |
| R6-05（P2） | 本次点名的复制逻辑问题关闭 | 负例加载生产函数，生产主流程也调用同一函数；独立运行 3/3 通过。不是完整脚本故障注入或 UI 端到端验收。 |
| xUnit 警告 | 已消除 | 本轮完整构建 0 警告/0 错误。 |

R3-01/R3-02 等历史已关闭问题保持关闭，不要求重做历史功能。

## 2. 独立验证与候选范围

HEAD：`0c3e3b3460893588602a95f01c58b6c50560c678`；VERSION `0.13.3`；PHASE `R5`。这是未提交的工作区候选，不是该旧提交本身的验收。记录时有 61 个已跟踪改动、65 个未跟踪条目，暂存区为空（本报告新增前）。

| 检查 | 本轮结果 |
| --- | --- |
| `dotnet build Caishenfolio.slnx --no-restore` | 0 警告 / 0 错误 |
| C# 完整回归 | 602 通过，0 失败，0 跳过；Host 567 + Desktop 35 |
| managed venv，fixture，`python -W error` | 449 项运行：448 通过 / 1 跳过 |
| 显式开启的真进程链路 | 5/5 通过 |
| 生产辅助函数负例 | 3/3 通过 |
| 定向 WPF 控件逻辑探针 | 确认 R7-01、R7-02；同时确认正式根被拒绝及 R7-03 |
| `git diff --check` | exit 0；正常 LF/CRLF 提示不算空白错误 |
| 两次完整 UI 冒烟、DPI、窄屏 | 本轮未执行，不继承早于最后界面修改的旧报告 |
| Release MSI / 安装升级 / 真人十一项 / 结果卡读屏 | 本轮未执行或仍待验收 |

证据位置（本机 `.local` 诊断材料，不要求提交生成的 bin/obj）：

- [定向探针源码](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-7/Probe/Program.cs)
- [探针原始输出](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-7/probe-output.log)
- [负例报告](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-7/negative-report.json)
- [10 个关键源码文件 SHA-256](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-7/source-manifest.json)

探针只实例化未显示控件并调用实际构造/加载方法；使用内存 HTTP handler，不调用 Application.Run/Window.Show，不绑定账本模型、不连接数据库、不发真实网络请求。**这不是鼠标/Tab/真实页面记账的 UI E2E 证据。**

## 3. 待整改项

### R7-01（P2，承接 R6-03）：现金→买入仍被旧金额阻断

定位：[LedgerView.xaml.cs:134](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs:134)、[LedgerView.xaml.cs:256](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs:256)。

可达操作：在入金的金额框输入 `not-a-number`，尚未提交便切换买入，填合法标的/数量/价格。切换只禁用 EntryAmount，不清除旧金额；BuildTransaction 在 switch 前仍无条件 ParseNumber(EntryAmount.Text)。买入工厂并不使用这个金额，却会因此失败，且当前字段不可编辑。

生产方法探针结果：Buy，`NASDAQ:AAPL`，数量 1，价格 100，禁用金额框保留错误文本 → `无法解析数字「not-a-number」。`；仅清空无关金额后同一买入成功构造；把必需数量改成错误文本仍正确拒绝。

**要求：**

1. 按交易类型解析其实际使用字段，以 LedgerTransaction 各工厂参数为准。不要在分支前解析所有文本，也不要只新增一次 Clear 来代替输入边界修复。
2. UI 清空/隐藏/禁用规则与构造规则共用类型需求；初次加载也应用这些规则。不要放宽必需输入和超卖等领域校验。
3. 新增可长期运行的回归：买入→入金、买入→换汇、入金→买入；不相关字段残留错误不影响目标类型，相关字段错误必须拒绝。
4. 在隔离账本做真实页面切换与记账，再断言流水类型/金额/数量正确。不要把本轮未显示控件探针冒充这一项。

### R7-02（P2，承接 R6-04）：区间标题未与响应原子更新，且混淆历史区间与年化口径

定位：[FxCarryView.xaml.cs:203](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:203)、[FxCarryView.xaml.cs:212](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:212)、[FxCarryView.xaml:45](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:45)。

已复现两种状态错配（内存响应中的数字仅为区分响应的测试标记）：

| 顺序 | 当前实现的最终显示 |
| --- | --- |
| 1 年成功，范围 `1 ~ 2`；再查 5 年返回 HTTP 503 | 表头“5 年高低”，行仍为 `1 ~ 2`，状态为查询失败 |
| 发出 5 年请求，再发出 10 年；10 年先成功，5 年后成功 | 下拉选择 10，表头“10 年高低”，行却为 5 年响应的 `5 ~ 6`，状态又说“区间 5 年” |

原因：标题在 await 前更新；失败只修改 StatusText；只禁用刷新按钮，不阻止区间下拉发出并发请求，也没有响应版本校验。

同一时间口径修复还剩两处：

- 展开的「换汇盈亏说明」仍硬编码“过去一年”，切换 5/10 年后并未同步。
- `每 100 万 {years} 年化利差` 错把历史回看年份拼到年化标题。CarryOnMillionText 仍是当前 carry × 1,000,000，并不因历史区间为 5/10 年而变为该区间收益或其历史年化统计。

**要求：**

1. 区分“用户选择的请求区间”和“当前显示结果的有效区间”。最新有效响应的行、标题、解释、状态一起更新。
2. 使用请求序号/取消旧请求后校验归属，或明确串行禁用区间选择，保证旧响应不能覆盖新选择。仅取消而不检查回包归属不足以处理竞态。
3. 失败时可清空结果；也可保留旧结果，但须保留旧结果区间标题并明确标为上次成功结果。不能旧数值配新年份。
4. 年化金额列建议为“每 100 万年化利差（按当前利率）”；历史区间只用于高低/分位说明。不得为迎合标题把金额乘 5/10。
5. 更新展开说明和读屏文本的年份，保留实际历史覆盖不足提示，不把请求长度当成已取得完整数据。
6. 测试覆盖 1→5→10→1、请求失败、旧响应后到、连续快速切换。断言选择状态、有效区间、行数据、标题/tooltip/展开说明同步，而不只检查文案出现某个数字。

### R7-03（P2，R6-01 的剩余子项）：IsPathRooted 不等于完整绝对路径

定位：[TestIsolation.cs:84](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Host/Security/TestIsolation.cs:84)。

`C:caishenfolio-r7-probe` 是盘符相对路径，不是 `C:\caishenfolio-r7-probe`。当前检查使用 IsPathRooted，会让它进入 GetFullPath 并依赖进程当前目录。把当前目录设为临时目录后，生产函数实测：

```text
IsFullyQualified = false
DriveRelativeAccepted = true
ProductionAccepted = false
```

正式根保护仍然有效，所以**不是重新打开原 P1，也没有证据表明本反例污染正式账本**。但这违反上轮明确的“输入必须是完整绝对路径”要求，测试启动结果仍取决于工作目录。

**要求：**

1. 在 GetFullPath 前使用 Path.IsPathFullyQualified 验证输入，保留 UNC、白名单、正式目录及重解析点守卫。
2. 增加盘符相对路径 `C:child`、根相对路径 `\child`、普通相对路径的测试；改变工作目录到允许的临时目录后也必须拒绝。
3. 测试完整绝对临时独立根仍可用；非法配置 Resolve 必须为 Invalid，不能回退 Production。
4. 补启动路径级与链接越界回归，使用自建临时目录/链接；不要为验证拒绝行为而真正访问正式数据库。

## 4. Agent 执行顺序

1. **先补反例测试，再修三个边界。** 建议顺序 R7-03 → R7-01 → R7-02；范围限定在对应生产文件与测试，避免同时做外观重构或新增金融能力。
2. **补长期测试。** 把 `.local` 探针中的反例迁移为正式测试，不只保留一次性截图或临时日志。拆出纯逻辑/可注入异步加载层时保持改动小，不要求重写整页架构。
3. **冻结候选。** 记录 HEAD、未提交差异及实际源码哈希；一次验收周期里不再改 XAML/资源/脚本。如果又改，相关门禁重新跑。
4. **重跑自动门禁与两次隔离 UI 冒烟。** 本轮 602/449/5/3 是当前候选结果，后续修复不能直接继承。正常冒烟必须确认本次拥有的核心进程、fixture/synthetic、独立账本、逐路径费用关系及零关键 skipped。
5. **再做最终显示/人验。** 当前冻结版上的 100/125/150%、1280×720、点击命中、Tab 完整遍历、结果卡逐卡读屏及真人十一项。200% 如不支持，明确支持边界，不报通过，也不擅自扩大既定验收矩阵。
6. **发布准备分开记。** 构建 Release MSI 可作为本地验证；安装/升级/卸载需隔离环境或用户确认。提交、推送、发布均不由此任务书自动授权。根目录 0 字节 `%s` 仍在，不应顺手无记录删除。

基础命令（在仓库根运行）：

```powershell
dotnet build Caishenfolio.slnx --no-restore
dotnet test Caishenfolio.slnx --no-build --no-restore
$env:PYTHONPATH = "$PWD\python"
$env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
$reviewPython = "$env:LOCALAPPDATA\Caishenfolio\state\.venv\Scripts\python.exe"
& $reviewPython -W error -m unittest discover -s tests/python -p "test_*.py"
$env:CAISHENFOLIO_RUN_EXTENDED_CHECK_TESTS = "1"
dotnet test tests/Caishenfolio.Host.Tests/Caishenfolio.Host.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~BackgroundExtendedCheckIntegrationTests|FullyQualifiedName~EvidenceCardFundingCurrencyChainTests|FullyQualifiedName~FxPathCostChainTests'
scripts/ui_smoke_negative.ps1 -ReportPath .local/acceptance-final/negative-report.json
scripts/ui_smoke.ps1 -ReportPath .local/acceptance-final/run-1-report.json
scripts/ui_smoke.ps1 -ReportPath .local/acceptance-final/run-2-report.json
git diff --check
```

各步检查退出码；构建失败不得继续用旧二进制跑验收。UI 两次串行执行，不停掉不属于测试的已有进程。报告文件如已存在，使用新的候选目录，不覆盖旧证据。

## 5. 交付记录口径

- 允许写：“本次三个反例已修复；该候选自动门禁通过；以下真人/显示/发布项待完成。”不允许把“代码修复”写成“完整产品验收通过”。
- current/handoff 顶部与 Current Objective 必须一致；下方旧记录标为历史，避免继续指导 Agent 重复第五轮工作。
- 本轮只读观测到正式 `portfolio.db` 为 110592 字节，最后写入时间 **2026-10-07 10:51:12（北京时间）**，早于本轮测试证据。不能继续沿用更早交接里的“仍为 2026-09-06”。本轮没有打开该数据库，也没有写入来源证据，不能仅凭时间戳断言发生了污染。后续验收应在运行前后记录元数据/指纹并核对。
- 请交付：每项修复与测试定位、冻结指纹、完整门禁结果、两份新冒烟报告、显示矩阵/真人实际记录、未测清单。届时按本文件反例逐项复审，不再增加无关功能范围。
