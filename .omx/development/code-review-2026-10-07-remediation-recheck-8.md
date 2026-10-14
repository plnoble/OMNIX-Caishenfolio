# 第八轮复审与剩余整改清单

日期：2026-10-07。范围：R7-01、R7-02、R7-03；不扩展新功能或重开历史已关闭问题。

## 1. 结论

**R7-01、R7-03 的功能缺陷关闭；R7-02 部分修复，仍有一个 P2 未关闭。本批完整整改验收暂不通过。**

不是全部重做，也不是上一轮三个问题都未修。本轮已独立重跑构建、完整 C#/Python、真进程、生产负例，以及原样的 R7 定向探针。按 `agent-dev-protocol` 同步了状态、门禁和交接；没有修改产品实现、提交、推送、发布或停止既有核心。

| 项目 | 判定 | 证据 |
| --- | --- | --- |
| R7-01 按交易类型解析 | 功能缺陷关闭 | BuildTransaction 按 needsQuantity/Amount/Fee/Tax/Instrument 选择解析；旧错误金额不再阻断 Buy，必需数量错误仍拒绝。尚需把桌面输入反例迁移为正式长期测试，并补页面切换记账验证。 |
| R7-03 完整绝对路径 | 关闭 | 输入改用 IsPathFullyQualified；新增 3 个 cwd 位于临时目录时的相对路径负例；原生产函数探针从 accepted=true 变为 false，正式根仍被拒绝。 |
| R7-02 失败后的结果区间 | 该子项通过 | 1 年成功后 5 年请求失败，仍保留 1 年标题、1 年数据，并明确说明上次成功区间。 |
| R7-02 年化金额标题 | 请求执行后的标题修复通过 | 为“每 100 万年化利差（按当前利率）”，不再拼接历史年数。XAML 初始标题仍需统一，见下文。 |
| R7-02 响应归属与完整说明 | **仍未关闭（P2）** | 旧的 5 年响应仍覆盖已成功的 10 年响应；展开说明仍硬编码“过去一年”。 |

## 2. 独立门禁与证据边界

HEAD 仍为 `0c3e3b3460893588602a95f01c58b6c50560c678`；版本 0.13.3 / R5。审核对象是该 HEAD 上的未提交工作区，而不是旧提交本身。记录时 61 个已跟踪改动、66 个未跟踪条目、暂存区为空（本报告新增前）。

| 检查 | 本轮实测 |
| --- | --- |
| 完整构建 | 0 警告 / 0 错误 |
| C# | **605 通过**，0 失败/0 跳过；Host 570 + Desktop 35 |
| managed venv，fixture，Python `-W error` | 449 项：**448 通过 / 1 跳过** |
| 显式开启真进程测试 | **5/5 通过**，复用边界见下文 |
| 生产辅助函数负例 | **3/3 通过** |
| R7 原样探针 | 确认两项修复及 R7-02 剩余乱序问题；这是观测探针，exit 0 不表示全部反例消失 |
| `git diff --check` | exit 0 |
| 7 个受审源码/脚本文件指纹 | 验证前后无漂移 |
| 正式账本主文件 | 验证前后 SHA-256、字节数、最后修改时间一致 |
| 两次 UI 冒烟、DPI、窄屏、读屏、真人、MSI | **未执行，不报通过** |

证据目录：[.local/recheck-8](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8)。

- [构建日志](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/build.log)、[C# 日志](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/csharp.log)、[Python 日志](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/python.log)。
- [真进程日志](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/real-process.log)、[负例报告](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/negative-report.json)。
- [原样探针的本轮输出](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/r7-probe-output.log)；源码复用 [R7 Probe](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-7/Probe/Program.cs)，未覆盖其旧输出。
- [源码指纹](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/source-manifest.json)、[账本运行前](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/ledger-before.json)、[账本运行后](D:/Agent/Project/OMNIX-Caishenfolio/.local/recheck-8/ledger-after.json)。

### 本轮环境限制

开始时 8765 已由 PID 43356 的核心监听，健康信息为 `market_provider=cache+upstream`、`market_data_synthetic=false`，不是本轮启动的 fixture 核心。本轮未停止或改配置该进程。

真进程测试里的 `StartsOrReusesACoreValuesTheLedgerAndStopsWhatItStarted` 按设计复用了它，对临时空账本运行；其余链路测试走各自端口。**5/5 是实际测试结果，但不能写成“五项均证明本轮自启 fixture 隔离成功”。** 两次 UI 冒烟要求本次拥有的核心，不能复用这个进程。未执行 UI 冒烟，不伪造 failed/passed 报告；请在用户确认已有核心可关闭后处理，或另行使测试使用独立端口，不能直接杀进程获取绿灯。

账本主文件本轮前后均为 110592 字节，最后写入时间为北京时间 2026-10-07 10:51:12，SHA-256 一致。这只证明主文件的前后指纹一致，不把它扩张为整个状态目录或 WAL 的完整审计。

## 3. 唯一未关闭项：R7-02（P2）

### 3.1 旧响应仍覆盖新选择

定位：[FxCarryView.xaml.cs:173](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:173)、[FxCarryView.xaml.cs:209](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:209)、[FxCarryView.xaml.cs:229](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml.cs:229)。

实际生产加载方法，在内存 HTTP handler 控制回包顺序时：

1. 发出 5 年请求 A；
2. 下拉切到 10 年，发出请求 B；
3. B 先成功，显示 10 年结果；
4. A 后成功，覆盖了 B。

最终实测：

```text
Selection = 10
Header = 5 年高低
Range = 5 ~ 6                 # A 的测试数据标记，不是真实行情
Status = 本位币 CNY，区间 5 年，共 1 个货币对……
```

与上轮相比，标题和行现在能一起更新，这是有效修复；但它们仍会一起倒退为用户已经离开的旧请求。当前只禁用刷新按钮，下拉依旧能并发发起查询；没有请求序号/归属校验。旧请求失败同样能进入 catch 覆盖新请求状态，旧 finally 也能提前恢复刷新按钮。

**修复要求：**

- 为每次加载分配请求序号，并在成功、失败和 finally 中均确认仍属于当前请求，才更新行、标题、说明、状态与 busy 状态。或者明确串行化并在加载期间禁用区间选择；两种方案择一，不要求大规模重构。
- 如使用取消，仍需处理取消后返回的旧结果；不能只 Cancel 而没有归属判断。
- 正常失败保留上次成功结果的现有行为可以保留，但应独立记录“是否已有成功结果”和其有效区间，不从默认表头推断。
- 当前第一次请求就失败时，也会写“仍显示上次成功的 1 年高低结果”，尽管从未成功。这个失败提示分支一并按上述成功状态修正为“尚无成功结果”。此项为同一失败状态修复的验收要求，不另开新的大工作包。

### 3.2 说明和初始标题未统一

定位：[FxCarryView.xaml:45](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:45)、[FxCarryView.xaml:87](D:/Agent/Project/OMNIX-Caishenfolio/src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:87)。

- 「换汇盈亏说明」仍为“历史分位……在过去一年里算高还是低”。5/10 年结果下仍错误，读屏也会读到旧口径。
- XAML 初始年化列还是“每 100 万所选区间年化利差”；仅请求入口改了 Header。统一初始值，避免初始/核心未就绪时留下旧标题。
- 范围说明应指向当前显示结果的有效区间；或者使用明确的“当前结果所用历史区间”通用说明。保留数据覆盖不足提示，不将请求长度等同完整取得的历史样本。

### 3.3 必须补的长期回归

这次 C# 从 602 增加到 605，对应 3 个路径测试；Desktop 仍为 35 项，未找到本轮新增的交易输入或异步区间回归。一次性探针不能替代正式测试，尤其不能把探针能打印出错误状态当作验收通过。

请将下面用例放进正常测试入口，并对正确结果做断言：

| 用例 | 必须断言 |
| --- | --- |
| 顺序 1→5→10→1 | 标题/行/tooltip/展开说明指向相同有效区间 |
| 1 年成功，5 年失败 | 保留 1 年数据及 1 年有效区间，明确当前选择请求失败 |
| 首次请求失败 | 空结果且不宣称存在上次成功数据 |
| 5 年慢、10 年快 | 5 年后来成功或失败都不能覆盖 10 年结果/状态 |
| 新请求未完成，旧请求已完成 | 旧 finally 不提前解除新请求的 busy 状态 |
| 初始/未就绪/1/5/10 年 | 年化列含义恒定；展开说明无固定一年造成的冲突 |
| R7-01 的无关字段错误 | 入金→买入、买入→入金/换汇不受无关旧文本阻断；所需数量/价格/金额错误仍拒绝 |

异步测试用可控完成顺序（如 TaskCompletionSource），不依赖公网行情、Thread.Sleep 或“机器刚好够慢”。如果需要，把加载核心改为可 await 的 Task 方法，由事件处理器薄封装即可；不要为这项修复重写整个软件。

## 4. 给开发 Agent 的下一步

1. **只补 R7-02 剩余子项和长期回归。** R7-01、R7-03 不再重复修改；保留相关反例保护。
2. **更新交接再冻结候选。** current/handoff 顶部和 Current Objective 一致；记录未提交源码指纹。不要只写“已修”，应逐项对应上表断言及实际执行结果。
3. **重跑基础门禁。** 构建、完整 C#、managed Python、门控真进程、生产负例。检查每步退出码，不继承本轮结果到修改后的候选。
4. **满足隔离前置条件后连续两次完整 UI 冒烟。** 保存新候选独立报告目录；检查本次核心进程、fixture/synthetic、独立账本、九页导航、逐路径净额、防串线、空费用不伪装净额及关键步骤无 skipped。补真实页面类型切换记账及区间操作，不能仅测控件方法。
5. **最终 UI/人验与发布验证。** 在最终候选上补 100/125/150%、1280×720、点击/Tab、结果卡逐卡读屏、真人十一项；Release MSI 构建与安装升级分别记。提交、推送、发布仍需用户授权。

本轮结论应准确写为：**“两项功能缺陷关闭，一项 P2 仍需完成；基础自动门禁通过，完整 UI/真人/发布验收未完成。”** 不增加投资功能或新的界面改版来代替收尾。
