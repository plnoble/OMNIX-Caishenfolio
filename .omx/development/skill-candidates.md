# Skill Candidates

## protocol-init-target-root

- Problem: `init-agent-protocol.ps1` uses current directory only; easy to init wrong repo.
- Candidate: skill or script wrapper requiring absolute `-ProjectRoot` and post-check that AGENTS.md exists at that path.

## dual-runtime-p0-gates

- Problem: Host+Python workbenches need both `dotnet test` and `python -m unittest` every milestone.
- Candidate: reusable verify script template with PYTHONPATH + solution path parameters.

## local-sqlite-lifecycle

- Problem: Microsoft.Data.Sqlite connection pooling keeps `tasks.db` locked after store dispose; tests and Desktop shutdown fail to delete/release files.
- Candidate: checklist skill — for local single-file DBs set `Pooling=false`, clear pools on dispose, best-effort cleanup in tests.

## WPF/XAML 改动的启动冒烟

- 触发场景：任何改动 App.xaml / 视图 XAML / 资源字典的任务。
- 规则：`dotnet build` + 单测通过**不足以**声明完成；必须 `Start-Process dotnet run` 并重定向 stderr，
  确认进程存活且 `MainWindowTitle` 非空，再关闭。BAML 在运行时加载，样式/绑定错误编译期不可见。
- 依据：2026-07-31 `Setter Property="Resources"` 事故（见 error-ledger）。

## 度量"缺失占比"时先写下分子分母各含什么

- 触发场景：任何"部分/整体"比例：数据覆盖率、完成度、配置齐全度。
- 规则：若被度量的缺失量根本不出现在分母里（如"未定价市值占比"的分母本身就剔除了未定价项），
  该比例必然退化为 0 或 100%。此时换一个确定可知的口径（如成本、条数），并在文案里
  明示口径。
- 依据：2026-09-04 N0.2 覆盖比例首版恒等于 100%（见 error-ledger）。

## 含中文的 .ps1：编辑后验证 BOM

- 触发场景：用 edit/任意外部工具修改 `.ps1`（尤其含中文注释与字符串的）。
- 规则：编辑后读文件前 3 字节，必须是 `EF BB BF`；丢了就用
  `[System.IO.File]::WriteAllText($p,$text,(New-Object System.Text.UTF8Encoding($true)))` 补回。
  PS 5.1 对无 BOM 的 .ps1 按 ANSI 解码，中文损坏会吞掉引号/花括号，报出一串
  **离病灶很远**的语法错误。
- 依据：2026-09-05 ui_smoke.ps1 edit 后解析错乱（error-ledger）；本仓第三起同类编码事故。

## 交接即基线：接手第一条命令是构建

- 触发场景：接手任何由上一轮 Agent 留下的工作区（尤其有未提交改动）。
- 规则：先 `git status` + `dotnet build` + 测试，确认基线状态再规划新工作；
  离场时同样必须以"构建+测试全绿"收尾。半成品交接会让下一轮的第一小时
  花在修不是自己造成的问题上。
- 依据：2026-09-05 接手时 `dotnet build` 即失败（error-ledger「上一轮 Agent 留下构建失败的半成品」）。

## None 承载多义时，每个消费点必须先消歧

- 触发场景：可选字段用 None 表示（此处 `_fixed_fee_in_target` 返回 None 既可能是
  "没有固定费"也可能是"固定费币种折算不了"）。
- 规则：当 None 有两种以上含义，消费处必须显式区分（如 `if x is not None and y is None`
  才是"不可计算"）；从一处复制分支逻辑到另一处时，逐条件核对守卫——
  "结构像"不等于"语义同"。
- 依据：2026-09-05 N2 path 3 复制 `_apply_costs` 逻辑时丢了消歧守卫（见 error-ledger）。

## 前后台一致性 = 同一条代码路径，不是同一套约定

- 触发场景：任何"后台/无界面运行也要复用前台规则"的需求（提醒、导出、校验）。
- 规则：让后台跑前台的同一条入口（如此处的 `PortfolioWorkspace.RefreshAsync`），
  而不是按理解重新拼装同样的调用——参数一多，重新拼装必然在某个默认值上漂移。
- 依据：2026-09-05 N3 层 2 复用 Workspace 后，阈值/交叉核对/容差自动与前台一致
  （decisions.md「N3 层 2 复用完整 Workspace」）。

## 事件去重的两种事实分开记账

- 触发场景：通知/提醒/告警的冷却与重试语义。
- 规则：「世界的事实」（条件当前是否成立）每次观察都记录；「动作的事实」
  （消息是否真的送达）只在送达后记录。冷却从动作起算，重入检测从世界状态起算——
  混在一起会让"投递失败"被自己的冷却吞掉。
- 依据：2026-09-05 N3 AlertGatekeeper 设计（decisions.md「送达与观察分离」）。

## 比较口径是集合属性，不是路径属性

- 触发场景：跨实体排序/取最优/算差值（路径比较、收益排名、费率对比）。
- 规则：先回答"全体在哪个口径上可比"（如全体净额或全体毛额），再取数；
  逐实体各自"缺 A 就用 B 兜底"会让缺数据的一方在比较中白赚便宜。
  需求里写"绝不混用"的约束要直接写成断言，写在注释里等于没写。
- 依据：2026-09-05 N2 差值首版净毛混用（error-ledger「差值首版基线混用」）。

## 修 None 多义 bug 必须全模块扫调用点

- 触发场景：修一个"None 有两种含义"的 bug。
- 规则：rg 该函数/模式在全模块的所有调用点，逐处核对消歧守卫是否一致；
  修一处就走，第二处（通常是复制粘贴来的）必然还埋着同一个错。
- 依据：2026-09-05 N2 path 3 修了守卫、split 段同日重犯（error-ledger）。

## decimal 内容比较前先归一化 scale

- 触发场景：对 DB 重读行与文本/网络重建行做哈希、去重、Diff；任何 decimal 参与的
  "内容是否相同"判断。
- 规则：`decimal` 的 scale 是值的一部分——1000.0m 与 1000m `==` 相等但默认 ToString 不同。
  比较前用固定格式（如 `"0.####…"` 去尾零）归一化，或改比规范化后的 `==`。
- 依据：2026-09-05 CSV 幂等修复（error-ledger：SQLite GetDecimal 带写入侧 scale）。

## 反例测试必须断言具体数值，方向性断言不算反例

- 触发场景：为审核反例写测试时。
- 规则：反例的价值在于"只有正确实现能通过"；只断言 `小于/大于/非空` 的测试，
  错误值（缩小 200 倍、抵消为零）同样通过——等于给缺陷背书。必须把反例文档里的
  期望数值写进断言（Equal/AlmostEqual），量纲变了就跟着改断言并核对公式。
- 依据：2026-09-06 复审 R04（0.065 通过"小于毛额"断言）与 R02（客户端测试传 0.15
  绕过 UI 单位），两处均为上一轮自写测试的盲区（error-ledger 2026-09-06）。

## 参数贯通改动必须有真进程链路测试

- 触发场景：给"桌面表单 → C# 客户端 → Python 路由 → 内核函数"任一段新增/改名参数。
- 规则：直调内核的测试只证明内核；序列化/路由/接线的断点只有真进程集成测试能抓住
  （broker 起服务、HTTP 走请求、断言响应）。参数链改动的 DoD = 至少一个链路测试 +
  序列化断言。注意两个坑：响应通常有包装层（先取 card 再取字段）；broker 无凭据时
  默认 provider=auto，离线测试需在宿主环境设 fixture 供子进程继承。
- 依据：2026-09-06 remediation-recheck 四项 P1 同型缺陷（error-ledger 同日）。

## XAML 布局改动的证据是程序化边界断言，不是导航成功

- 触发场景：在 Grid 中新增/移动面板、改 Row/Column、加输入控件。
- 规则：WPF 同格多元素不报错——后声明者覆盖前者绘制与命中，构建与导航冒烟全绿。
  布局类改动的完成证据 = UIAutomation 按 AutomationId 定位相关控件，断言每个宽高>0
  且两两边界不相交（`scripts/ui_smoke.ps1` 的 `Invoke-CostInputLayoutCheck` 是模板）。
- 依据：2026-09-07 recheck-2 F-01（error-ledger 同日）。

## 第三轮复审：金额独立核算与最终输出校验

- 金融占比测试先根据交易前后金额独立计算预期，禁止直接照搬实现公式；外部资金进入后，旧持仓和新增持仓必须使用同一个新总额。
- 分布合计为 1 后，还应检查单仓不超过所属地区/货币切片；仅验证合计不足以保证组合自洽。
- 日期解析测试应覆盖所有候选无效时的最终标题，防止 fallback 把非法输入重新引入。
- 布局边界不相交仅是基础证据；仍需父容器裁剪、焦点与结果展示检查，不能据此宣称多 DPI 和桌面端到端全部通过。
- 依据：`code-review-2026-09-07-remediation-recheck-3.md`。

## UIA 自动化模板（WPF 桌面断言）

- 触发场景：用 UIAutomation 给 WPF 控件写程序化验收（焦点/输入/下拉选择/文本断言）。
- 规则：
  1. 焦点属性是 `HasKeyboardFocusProperty`（没有 IsKeyboardFocusedProperty）；
  2. PowerShell 中 `"str".Method()` 会绑定到字符串——先 `$x = Find…` 再 `$x.Method()`；
  3. ComboBox 选择必须先 `ExpandCollapsePattern.Expand()` 再子项
     `SelectionItemPattern.Select()`，否则选择可能不提交；
  4. `AutomationElement` 没有 `Refresh()`——`FindAll` 本身就是实时查询；
  5. 断言期望值 provider 无关：文本捕获分组 + 数值比较（净==毛、净=毛−费），
     不写死行情数字。
- 依据：2026-09-07 recheck-4 UI 验收程序化（error-ledger 同日四坑）。

## 预置 SQLite 状态库与生成脚本的三条规则

- 触发场景：给测试预置已有应用的数据库（账本/设置等）或生成临时 Python 脚本。
- 规则：
  1. 预置库必须**复刻全 schema 并写对 user_version**——应用打开版本相符的库会跳过迁移，
     缺表即崩（且常以 XamlParseException 包装，真因在 inner exception）；
  2. PowerShell 里生成含 `%` 或 `$` 的 Python/SQL 文本：用唯一占位符 + `.Replace()`，
     别用 `-f`/`-format`（% 是格式符）；用 `[IO.File]::WriteAllText` + `UTF8Encoding($false)`
     写无 BOM 文件（PS5.1 的 `Set-Content -Encoding UTF8` 带 BOM，venv Python 拒收）；
  3. UIA 断言前先读 XAML 模板确认元素结构——相邻显示的文本可能分属不同 TextBlock/不同
     UIA 元素，按容器收集（TreeWalker 上溯至含目标字段的父级）而非按行 join 全窗口。
- 依据：2026-09-08 recheck-5 整改连环坑（error-ledger 同日）。

## 2026-09-08 - 真实桌面 E2E 环境与路径身份守卫

- 相对数值断言只减少对固定行情数值的依赖，不隔离用户 provider、凭据、缓存或账本。
- fixture 必须在实际子进程 /health 中验证；使用 Host 约束的测试状态，禁止为冒烟污染用户状态。
- 多路径结果要先核对持有币种与目标币种，再在指定路径卡内断言数字及成本状态，不能全窗口任取首条。
- 错误节点先赋变量，再读取属性并写入失败详情；不要让插值错误遮蔽实际失败。
- UIA SetFocus / SetValue 仅证明对应自动化能力，鼠标命中和 Tab 遍历需各自证据。
- 依据：`code-review-2026-09-08-ui-acceptance-recheck-5.md`。

## 2026-10-07 - 第六轮：失败关闭、动态表单与证据绑定

- 测试配置要区分未启用/有效/非法；非法模式必须在访问正式状态前中止，不能用 fallback 恢复生产默认值。
- 测试允许目录必须与正式目录互斥；仅检查 LocalAppData 文本前缀不是隔离保证。
- 按类型隐藏控件时，提交路径也按类型解析；对隐藏旧错误值增加回归，不能只检查可见性。
- 安全负例调用生产实现；拷贝判定代码会让生产防护删坏后仍“全绿”。
- UI 更名、区间与模板改动后，断言和说明同步更新；验收报告记录最终候选哈希，不能只记录长期不变的 HEAD。
- 依据：`code-review-2026-10-07-delivery-recheck-6.md`。

## 2026-10-07 - 第七轮：双向切换、异步状态与 Windows 路径

- 动态表单回归至少双向切换；清空旧值不是构造层忽略无关字段的替代方案。
- 异步页面测试控制完成顺序：先旧后新、先新后旧、失败；数据与解释/标题共同绑定已采纳响应，不能只测文案变化。
- 历史回看区间与年化收益量纲分离；同步年份不能误改另一种指标的口径。
- Windows 路径验证增加 `C:child` 与 `\child`；允许目录作为 cwd 时仍须拒绝非完整绝对路径。
- 依据：`code-review-2026-10-07-remediation-recheck-7.md`。

## 2026-10-07 - 第八轮：异步请求归属要覆盖完整生命周期

- 原子提交标题和行，只解决单个响应内部一致；还要确认它是当前可采纳的响应。
- 请求序号/归属同时约束成功、失败、finally，避免旧结果覆盖、新状态被旧错误覆盖、旧完成提前释放 busy。
- 独立记录是否有成功结果和有效区间；不从默认控件标题猜测曾有成功数据。
- 观察型诊断脚本 exit 0 不等于反例被修复；将正确状态的断言迁入正常测试入口。
- 依据：`code-review-2026-10-07-remediation-recheck-8.md`。

## 2026-10-07 - WPF 控件级测试宿主（WpfTestHost 模式）

- 模式（FA1 终审纠正）：单 STA 线程只创建普通 `Application`，从编译时嵌入的真实 App.xaml 提取
  `Application.Resources`；**不得创建生产 App，不得加载其 StartupUri**。原“不调用 Run 就不启动”
  的说法已被真实残留核心反证：构造函数会排队启动，Dispatcher.Run 仍会执行。测试用例通过
  `InvokeAsync(async body)` 编组到该线程，body 内 `ConfigureAwait(true)` 的续延天然回到
  Dispatcher——异步状态机测试确定性强，无需 sleep/泵帧。
- 已验证（统筹补丁后）：10 个业务回归加 1 个宿主隔离回归；等待 ApplicationIdle 后断言无
  MainWindow/StartupUri/窗口，正常入口全部通过且无核心残留。旧 10 项通过本身未证明隔离。
- 注意点：Application 每 AppDomain 只能一个（必须集中创建）；UIA/控件断言与点击坐标都是物理
  像素（见 error-ledger 同日条目）；长驻测试进程要与可管理的后台任务配合，避免孤儿。
- 适用范围：本仓库任何需要触碰真实 WPF 控件（BAML、StaticResource、DependencyProperty）的回归，
  替代一次性反射探针（recheck-6/7/8 的 Probe 模式）。

## 2026-10-08 - 桌面冒烟的有界等待与不可覆盖证据

- 新隔离根的首次引导按 PID + 标题 + Window 类型定位；等待出现、执行一次操作、等待消失。不得把暂时找不到写成通过，也不得点击同名的其他实例。
- 等待/归属逻辑抽为真实生产函数，测试注入观察序列与操作回调，覆盖延迟、外部 PID、缺失、无法关闭及歧义；实际 UI 验证控件类型与运行链路。
- 每次启动独立 attempt 目录；显式报告路径以 CreateNew 拒覆盖；报告绑定起止、退出码、参数、源码/二进制指纹，失败先保留再诊断。
- 同版失败不重试筛绿灯；代码修正后明确新批次，保留旧批次。StartupOnly 与完整 UI 模式分开报告，不能混算通过范围。

## 2026-10-19 - 金融领域迁移审查的语义核验

- 候选规则：先比输入资金、时间、计量币、单位、费用三态和可追溯事实，再按功能名称判断是否可复用；“盈亏平衡/分批/持仓”同名可能指完全不同的经济问题。
- 候选规则：未知成本不等于未知资产数量或未知兑换价值；结果类型应分离估值可用性与收益可用性，避免整条路径消失。
- 候选规则：性质测试必须覆盖每条路径实际接收的参数组合，尤其多段路径的部分费用；否则全绿仍漏掉复制分支的守卫。
- 候选规则：跨语言迁移保留事实、十进制协议和验收向量，不把旧错误结果冻结为必须兼容的规范。当前仅记录候选，不新增技能或扩大实现范围。
