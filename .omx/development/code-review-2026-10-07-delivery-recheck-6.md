# 第六轮交付复审与整改意见（2026-10-07）

## 1. 结论

**当前交付暂不通过最终验收：发现 1 项 P1、4 项 P2。不是只补跑一次冒烟即可关闭。**

已核实此前保存的两次功能冒烟、三档 DPI 与窄屏相关报告；这些证明相应旧代码和当时场景取得了进展。
但 09:24 之后又改了全局下拉模板、账本表单、汇率区间、布局和产品名称，旧报告不能覆盖当前版本。

本轮没有修改业务代码，没有提交、推送、安装或发布，也没有以隔离反例启动正式账本。
第四轮 R3-01 / R3-02 继续关闭，本轮没有重新打开金额稀释或日期标题问题。

代码标识仍为 HEAD=0c3e3b3460893588602a95f01c58b6c50560c678、版本 0.13.3 / R5，但实际审核对象是当前未提交工作区。
[证据与关键源码哈希](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-6/observations.json)记录了本次核查的 10 个关键文件，不能只凭旧 HEAD 复现本次代码。

## 2. 本轮独立验证与旧证据边界

| 项目 | 本轮结果 / 边界 |
|---|---|
| 最新完整构建 | 成功，0 错误、**2 警告**：xUnit1030，AnalyticsCoreClientTests.cs:275、361 |
| C# 基础测试 | 599 通过：Host 564 + Desktop 35 |
| managed Python -W error | 449 项：448 通过、1 跳过 |
| 开启门控的真实进程测试 | 5/5 通过 |
| ui_smoke_negative.ps1 | 报告 3/3 通过；其中 2 项存在生产覆盖缺口，见 R6-05 |
| 标题契约探针 | 当前标题 Caishenfolio  v0.13.3 不匹配脚本旧品牌断言 |
| 隔离根生产函数探针 | 正式数据根被接受为测试根，见 R6-01；未按此配置启动应用 |
| 账本表单方法探针 | 隐藏数量字段里的旧错误文本仍能阻断 100 CNY 入金，见 R6-03 |
| git diff --check | 通过，无空白错误 |
| 当前代码完整 UI / DPI 矩阵 | **未重跑**。标题契约已能独立证明失败；未改脚本绕过，也未拿打开窗口当成完整通过 |
| Release MSI / 安装升级 | 本轮未执行；仍为交付缺项 |
| 真人十一项 / 结果卡逐卡读屏 | 未新增验收；保留用户说明的未完成状态 |

本轮 negative 报告：[negative-report.json](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-6/negative-report.json)。
表单探针源码：[Program.cs](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-6/Probe/Program.cs)。
探针未运行 Application.Run、未显示主窗口、未绑定 PortfolioViewModel 或数据库；它是方法级反例，不是冒充页面端到端验收。

保存的旧 UI 证据时间均以 Asia/Shanghai 表示：

- run-1 / run-2：2026-10-07 08:52；各 16 步 passed。
- dpi-100 / 125 / 150：约 09:12 / 09:15 / 09:17 的完整冒烟 passed。
- narrow-1280x720：09:24:06 完整冒烟 passed。
- 三项负例原报告：08:52:57 passed。
- 上述文件都早于用户明确列出的后续界面调整，不得改日期后作为新验收证据。

## 3. 必须整改的问题

### R6-01（P1）：测试根校验未真正隔离正式状态，非法配置还会静默回退

定位：

- [TestIsolation.cs:71](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Host/Security/TestIsolation.cs:71)：接受整个 LocalAppData / Temp 子树。
- [MainWindow.xaml.cs:59](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/MainWindow.xaml.cs:59)：先设正式根；TryResolveStateRoot 返回 false 时忽略 reason，继续使用正式根。

两个可达问题：

1. **有效但不隔离**：传入正式产品根 LocalAppData/Caishenfolio，TryNormalizeStateRoot 返回 true，规范化结果仍是正式根。本轮直接调用当前编译的生产函数确认。再配合 fixture 强制开关，就能在正式状态上进入“测试模式”。
2. **无效时失败开放**：显式配置非法测试根后，TryResolveStateRoot 返回 false；桌面没有区分“未配置”和“配置错误”，因此转回正式数据根，而不是中止测试启动。

这不是说当前唯一临时目录的正常冒烟已污染账本；本轮未发现这种污染证据。问题是隔离契约不能防止错误配置落入正式数据，属于仍未闭环的 R5-01 安全边界。

整改要求：

- 区分未启用测试模式、有效测试配置、无效测试配置。显式启用但校验失败时，在任何正式状态初始化前中止并显示脱敏原因。
- 仅接受受控测试父目录下本次拥有的独立目录；拒绝正式根及其父/子树，不能以整个 LocalAppData 为充分隔离条件。
- 输入必须是完整绝对路径；先 GetFullPath 再 IsPathRooted 并不能证明原输入是绝对路径。
- 检查目录链接/重解析点及路径解析后的归属，避免文本前缀合法却指向正式状态。
- 测试 provider 强制与验证后的隔离上下文绑定，不能分别读取变量后各自猜测是否隔离。
- 加启动路径级测试：正式根、非法根、相对路径、越界、链接越界、合法独立根；必须断言错误配置不会进入正式状态初始化，而不只是 helper 返回 false。

验收：反例函数不接受正式根；错误测试配置在打开任何正式数据库/配置前失败；正常唯一测试根仍能完成两次冒烟。

### R6-02（P2）：更名后当前冒烟仍按旧窗口标题判断，现版必然不能全绿

定位：

- [ProductInfo.cs:12](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Host/ProductInfo.cs:12)：Name 已改为 Caishenfolio。
- [MainWindow.xaml.cs:48](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/MainWindow.xaml.cs:48)：标题来自 ProductInfo.Name 与版本。
- [ui_smoke.ps1:174](D:/Agent/Project/OMNIX-Caishenfolio/scripts/ui_smoke.ps1:174)、[ui_smoke.ps1:862](D:/Agent/Project/OMNIX-Caishenfolio/scripts/ui_smoke.ps1:862)：仍匹配 OMNIX-Caishenfolio。

独立无界面探针：

~~~text
CurrentWindowTitle = Caishenfolio  v0.13.3
PassesCurrentSmokeTitleRegex = false
~~~

这是确定的测试契约回归，不是猜测“可能因 UI 改动失败”。

整改与验收：

- 统一显示名称的唯一来源或明确的测试契约；修改两处旧判断，保留准确的版本与本次进程身份校验。
- 不得通过接受任意非空标题或跳过标题检查获得通过。
- 不要顺手改正式数据目录、更新仓库 URL、安装升级身份来配合显示更名，这些有兼容性风险，应另行验证。
- 在最终更名代码上连续完成两次完整冒烟，报告标题、版本、代码指纹对应同一份候选。

### R6-03（P2）：隐藏的无关输入仍参与解析，可阻断入金等现金操作

定位：

- [LedgerView.xaml.cs:127](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs:127)、[LedgerView.xaml.cs:139](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs:139)：切换入金后禁用/隐藏数量与价格，但未清理其文本。
- [LedgerView.xaml.cs:251](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/LedgerView.xaml.cs:251)：BuildTransaction 在按交易类型分支前，无条件解析数量、价格、金额、费用、税费。

反例：

1. 在买入数量框留下无法解析的文本；
2. 切换为入金，数量框隐藏；
3. 输入合法入金金额 100；
4. 构造入金仍报“无法解析数字「not-a-number」”，而出错框此时不可见。

本轮在未显示的 LedgerView 上按对应隐藏/禁用状态调用实际 BuildTransaction，确认异常。此解析缺陷可能早于本轮；本轮“按类型隐藏字段”后仍可达，不能把它当作 UI 已收尾。

整改与验收：

- 按交易类型只解析其实际需要的字段；不要依赖“框隐藏了，用户就不会有旧值”。
- 允许清理无关字段作为 UI 辅助，但领域构造仍应按类型限定输入。
- 覆盖买入→入金、买入→换汇、现金→买入等切换；无关旧错误不阻断新类型，必需错误仍被阻断。
- 在真实页面完成上述切换后记账，仅使用隔离账本。
- 检查初次加载时字段可见性是否已经应用；不能只在用户第二次切换类型后才正确。

### R6-04（P2）：选择 5/10 年查询，结果表与解释仍标成 1 年

定位：

- [FxCarryView.xaml.cs:196](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:196)：读取年份，按 years×365 请求历史。
- [FxCarryView.xaml:108](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:108)：FxRangeHeader 固定“1 年高低”，没有对应更新引用。
- [FxCarryView.xaml:94](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:94)：分位说明固定“过去一年 / 一年低位 / 一年高位”；上方长说明也固定一年。

选择长区间时，顶部状态说 5/10 年，数值按长区间请求，表头/解释却声称一年；会让用户误解同一组数字的时间口径。

整改与验收：

- 请求区间、结果表头、分位 tooltip、读屏说明共享同一个有效区间状态。
- 没有取满所选历史长度时明确实际可用区间/样本限制，不把“请求 10 年”写成已有完整 10 年数据。
- 覆盖 1→5→10→1 的切换及请求失败；不能让旧数值配上新年份。
- 新增页面/视图模型断言，确认标题、说明、数值来自同一响应区间。

### R6-05（P2）：三项负例中两项测试的是复制逻辑，不能保护真实脚本

定位：

- [ui_smoke_negative.ps1:47](D:/Agent/Project/OMNIX-Caishenfolio/scripts/ui_smoke_negative.ps1:47)：重新定义 Remove-OwnedSmokeDirectory，而非调用生产脚本的函数。
- [ui_smoke_negative.ps1:65](D:/Agent/Project/OMNIX-Caishenfolio/scripts/ui_smoke_negative.ps1:65)：重新写一段 skipped 判定，只验证副本。
- 同文件约 88 行，只有 Get-FailureText 从生产脚本加载。

即便生产脚本的路径守卫或 skipped 失败处理被删坏，前两项副本测试仍可通过。因此本轮重跑 3/3 的事实不能证明对应生产防护得到回归保护。

整改与验收：

- 把安全清理/结果判定抽为无顶层启动副作用的可加载模块，生产与测试调用同一个实现；或用可验证的受控脚本入口测试实际执行结果。
- 路径拒绝、重解析点拒绝、关键步骤 skipped→整体失败，应验证真实函数/真实报告与退出码。
- 给出失效保护证据：受控替身/变异中故意移除对应防护，相关测试必须失败；不要在用户生产状态做破坏性试验。
- 清理负例全部使用自己创建的临时树；绝不拿真实用户目录测试删除。

## 4. 有进展但尚不能整体关闭的验收项

### 历史 UI / 读屏

认可用户提供的旧版本人工观察以及现存报告，不将其说成“从未做过”。
但以下证据不能互相替代：

- 6 个费用框的 5 次 Tab 顺序，不等于全应用表单/弹窗/结果区完整键盘验收。
- 输入框与展开说明被讲述人读出，不等于结果卡逐卡读屏已通过。
- probe JSON 记录了边界、点击和 Tab，但没有完整机器 pass/fail 判定；不能只凭文件存在认定整个矩阵通过。
- DPI 文档应统一逻辑尺寸与物理像素口径，记录实际 DPI/工作区；不要混用尺寸推导裁剪结论。
- 200% 并非原任务三档的必过要求，可标“扩展场景未测”；100/125/150% 和窄屏仍需在最终界面版本上完成要求的组合。

全局 ComboBox 模板是本轮后续修改，应回归键盘选择、鼠标弹出、可访问名称与结果，不能仅检查深色底是否好看。

### 文档与发布

目前 current/handoff/feature-status 仍有“DPI/鼠标/Tab 未测”，delivery-report 部分章节又说已完成，甚至写“下一步只剩真人与结果卡读屏”。应统一为按版本/时间区分的证据表，不能遗漏当前代码整改、重新跑矩阵和打包。

此外：

- 完整构建本次有 2 条 xUnit1030 警告；修复测试里的 ConfigureAwait 用法后再复跑，不要仅靠增量构建不重触发分析器来宣布 0 警告。
- Release MSI 未构建不等于必须先获“发布授权”：本地构建与对外发布是不同动作。按已授权开发范围做候选包验证，推送/安装覆盖/发布另行确认。
- Git 整理清单可保留“待确认提交”；未提交、未发布不能称交付基线已固化。
- 正式账本最后写入时间未变是一项辅助信息，不足以单独证明隔离策略对所有错误配置都安全。

## 5. 下一位 Agent 的最小整改顺序

1. **先修 R6-01**，补正式根/非法配置 fail-closed 测试；保持所有测试写入远离用户正式状态。
2. **修 R6-02 与 R6-05**，使验收脚本可运行且负例调用真实实现。
3. **修 R6-03 / R6-04**，给交易类型切换和历史区间文案增加回归。
4. 冻结候选代码，记录 HEAD + 未提交关键文件哈希；清理两条测试分析器警告。
5. 重跑完整构建、C#、managed Python -W error、5 项进程门控、真实负例、两次完整 UI。
6. 在同一候选上补 DPI/窄屏、键盘与结果卡读屏；真人十一项未完成时明确等待，不伪造通过。
7. 构建 Release MSI，校准交付材料，按授权处理本地提交；未经授权不推送、安装覆盖或发布。

完成后提供新的交付报告与证据索引，再请求复审。不要再修改 UI 后沿用修改前的完整验收结论。
