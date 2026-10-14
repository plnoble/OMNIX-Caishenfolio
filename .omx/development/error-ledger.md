# Error Ledger

## 2026-10-07 - 隔离冒烟仍启动真实数据源

- Symptom: `/health` 在测试变量显示 fixture 时仍返回 cache+upstream，缓存路径指向用户正式目录。
- Wrong assumption: 父进程环境变量会原样到达最终 Python 进程，仓库内测试目录也可以作为隔离根。
- Root cause: Host 只允许 LocalAppData 或临时目录；仓库路径被拒绝后强制逻辑不生效。进程环境查询结果也与 Python 读到的值不一致。
- Detection: 冒烟打印进程树、`/health.market_provider_env` 和运行时启动参数。
- Fix: 冒烟根改到临时目录；隔离 fixture 同时写入命令行 `--provider`；健康检查核对本次 pid。
- Prevention: 不把“进程查询看到环境变量”当作子进程真实环境；隔离路径先过 Host allowlist。
- Skill candidate: yes。

## 2026-07-19 - SQLite test cleanup file lock

- Symptom: SqliteTaskStore tests asserted OK but failed deleting `tasks.db` (`used by another process`).
- Wrong assumption: disposing store / closing per-call connections was enough to release the file.
- Root cause: Microsoft.Data.Sqlite default connection pooling keeps native handles.
- Detection: `scripts/verify_p2.ps1` / xUnit failures in finally cleanup.
- Fix: `Pooling=false` on connection string; `SqliteConnection.ClearAllPools()` on dispose; best-effort test cleanup.
- Prevention: for local single-file DBs used in tests/Desktop, prefer no pooling or explicit pool clear on shutdown.
- Skill candidate: yes (local SQLite lifecycle).

## 2026-07-19 - init-agent-protocol ran in wrong directory

- Symptom: First init reported SKIP for all files; `D:\Agent\Project\Caishenfolio` stayed empty.
- Wrong assumption: Running the init script without `Set-Location` would target the intended project path.
- Root cause: `init-agent-protocol.ps1` writes relative to `Get-Location`, not an explicit `-TargetPath`.
- Detection method: Listed Caishenfolio dir (empty of protocol files) while legacy project already had `.omx`.
- Fix: `Set-Location D:\Agent\Project\Caishenfolio` then re-run init.
- Prevention rule: Always cd into the target project before protocol init; confirm CREATE paths after run.
- Skill candidate: yes (protocol bootstrap checklist)

## 2026-07-19 - Market namespace collided with Market enum

- Symptom: CS0118 `Market` is a namespace but used as a type in FixtureMarketDataProvider.
- Wrong assumption: folder/namespace `Caishenfolio.Host.Market` is fine alongside enum `Market`.
- Root cause: C# namespace `Market` hides type `Market` in the same compilation unit scope.
- Detection method: `dotnet build`
- Fix: rename namespace/folder to `Caishenfolio.Host.MarketData`; qualify enum as `Data.Market`.
- Prevention rule: avoid namespace names that match domain type names (Market, Task, Data alone).
- Skill candidate: no

## 2026-07-31 - R4：Setter Property="Resources" 导致启动即崩

- 症状：`dotnet build` 零错误零警告、183 个单测全过，但启动时 `XamlParseException`：
  “设置属性 System.Windows.Setter.Property 时引发了异常”，内层 `ArgumentNullException (Parameter 'property')`。
- 错误假设：以为可以在 `Style` 里用 `<Setter Property="Resources">` 给 DataGrid 挂一套表头/单元格样式，
  从而避免在每个视图里重复那段样式。
- 根因：`Setter.Property` 只接受**依赖属性**；`FrameworkElement.Resources` 是普通 CLR 属性，
  解析时 Property 为 null。BAML 在运行时才加载，编译期完全看不出来。
- 定位方式：`Start-Process` 启动应用并把 stderr 重定向到文件；进程秒退，读首 6 行拿到内层异常。
- 修复：把 `DataGridColumnHeader` / `DataGridCell` 改为 App.xaml 里的应用级隐式样式，
  `WealthGrid` 只保留真正的依赖属性设置。
- 预防规则：**XAML 改动必须真正启动一次应用**，构建通过不代表 BAML 能加载；
  写 `Setter Property="X"` 前先确认 X 是 DependencyProperty。
- 是否值得沉淀为 skill：是——「WPF 改动的验证必须包含一次启动冒烟」应进入交付门禁。

## 2026-08-01 - 用 PowerShell Set-Content 改中文文件，把整份文档写成乱码

- 症状：`current.md` 里每个中文字符都变成乱码（`瀹屾垚`、`楠岃瘉`），全文报废。
- 错误假设：以为 `(Get-Content x -Raw) -replace ... | Set-Content x -Encoding utf8` 是安全的原地替换。
- 根因：Windows PowerShell 5.1 的 `Set-Content` 默认按系统 ANSI 代码页（936）写出；
  即使显式给 `-Encoding utf8`，读入那一侧 `Get-Content` 已按 ANSI 解码，往返一次即损坏。
  本项目此前已记录过 BOM 相关的编码坑，这次是同一类问题的另一个入口。
- 定位方式：写完后文件内容直接肉眼可见乱码；`git checkout <上一提交> -- <file>` 可完整恢复。
- 修复：从上一个提交恢复原文件，改用 Edit 工具做精确替换（保留原编码，不整体重写）。
- 预防规则：**不要用 PowerShell 字符串替换重写任何含中文的文本文件**。
  改文件一律走 Edit 工具；确需脚本写入时用 `[System.IO.File]::WriteAllText($p,$s,(New-Object System.Text.UTF8Encoding($false)))`。
- 是否值得沉淀为 skill：是——「Windows 上改文本文件只用 Edit，不用 shell 重写」应与既有的 BOM 规则合并。

## 2026-09-04 - N0.2 首版覆盖比例：用"已定价市值/总合计"，结果恒等于 100%

- 症状：新测试 `APartiallyPricedPortfolioNeverClaimsAllClearEvenWithNoFindings` 断言
  覆盖比例 < 50%，实际返回 1.0。
- 错误假设：以为"总合计"包含未定价部分——但 `ValuationEngine` 本来就把未定价持仓排除在
  总合计之外（这正是 R2 的正确行为），所以分子分母是同一个数。
- 根因：没有先想清楚"缺失的量是什么"。覆盖比例要度量的是未知部分占比，而未知部分
  的市值正是未知数，用已定价口径永远算不出。账本中唯一确定的是成本。
- 定位方式：写临时探针测试输出中间值（TSE:7203 CostBase=0、TotalValue=30000），
  三分钟定位。
- 修复：改为成本口径 `PricedFractionByCost`，并接受"成本也因缺汇率未知时按 0 计"的
  有意低估。
- 预防规则：度量"缺失占比"时，先写下分母和分子各自包含什么；若缺失量本身在分母里
  不出现，这个比例必然退化（0 或 1）。
- 是否值得沉淀为 skill：否，属一次性思考失误，但已记入此账。

## 2026-09-04 - N0.2 首版 BuildSummary 引入无意义静态占位成员

- 症状：构建时自己发现 `targetDriftMarker`/`targetAssetAllocationForSummary` 两个静态成员
  从未被赋值、永远为 null。
- 错误假设：想避免"给 BuildSummary 加一个参数"的连锁改动，试图用静态变量传递目标配置。
- 根因：省小钱吃大亏——静态字段在多线程/多快照下还会串数据。
- 修复：老老实实加参数 `targets`，删除占位成员。
- 预防规则：数据依赖用参数传递；静态状态只用于真正的进程级常量。
- 是否值得沉淀为 skill：否。

## 2026-09-05 - N1 接手：上一轮 Agent 留下构建失败的半成品

- 症状：开工第一条命令 `dotnet build` 即失败——`PortfolioSettingsTests.cs(296)` CS1513
  应输入 }；`SymbolResolverTests` 一例断言失败（参数序写反）。
- 错误假设（上一轮的）：把新测试方法追加到类的闭合 `}` 之后，视觉上像在类里，
  编译器不认；`Assert.Contains(resolution.Reason, "歧义")` 把期望子串与实际串写反。
- 根因：多轮交接时没有以"构建+测试全绿"作为离场状态；交接物没有自检。
- 定位方式：`dotnet build` 错误行号直接指向缺失括号；测试输出显示实际 Reason 字符串完整。
- 修复：闭合括号与 Dispose 方法移到文件末尾；`Assert.Contains("歧义", resolution.Reason)`。
- 预防规则：**任何 Agent 离场前必须跑一次构建+测试**（协议第 3 条"小步可验证"的离场形态）；
  接手方开工第一条命令永远是构建基线，不是读代码。
- 是否值得沉淀为 skill：是——「交接即基线」值得并入 agent-dev-protocol 的 Startup。

## 2026-09-05 - N1：edit 工具改 .ps1 丢 BOM，PowerShell 5.1 解析错乱

- 症状：`scripts/ui_smoke.ps1` 经 edit 工具改动后，运行报
  `Missing closing '}' in statement block`，错误信息里中文全部显示为乱码（`棣栭〉`）。
- 错误假设：以为 error-ledger 里"不要用 PowerShell 重写中文文件"的教训只针对 shell 管道，
  edit 工具是安全的。
- 根因：edit 工具按 UTF-8 无 BOM 写回；PS 5.1 解析 .ps1 时无 BOM 即按 ANSI(936) 解码，
  中文串损坏后某些字节恰好吞掉引号/花括号，产生一串误导性的语法错误（报错位置
  离真实病灶很远）。文件内容本身没坏，只是缺 3 字节 BOM。
- 定位方式：读文件前 3 字节（`3C 23 0A` = `<#`，无 `EF BB BF`）；乱码仅出现在
  PS 解析错误里而 read 工具显示正常。
- 修复：`[System.IO.File]::WriteAllText($p,$text,(New-Object System.Text.UTF8Encoding($true)))`
  补回 BOM，内容零改动。
- 预防规则：**edit 工具改过任何 .ps1 后，必须检查/恢复 BOM**（前 3 字节应为 EF BB BF）。
  这是本项目第三起同类编码事故（前两起：version.ps1 的 Set-Content BOM、current.md 的
  ANSI 往返），三个入口殊途同归：Windows 上含中文的文件，编码状态是文件的一部分。
- 是否值得沉淀为 skill：是——「含中文的 .ps1，编辑后验证 BOM」应并入既有编码规则条目。

## 2026-09-05 - N1：一次 edit 误吞相邻函数声明行

- 症状：给 ui_smoke.ps1 插入新函数后运行报语法错；`rg 'function Invoke-'` 只剩 2 个声明，
  `Invoke-DialogCheck` 的 `function … {` 行消失，但其函数体还在。
- 错误假设：old_string 选了 `function Invoke-DialogCheck {` 单行做插入锚点，
  new_string 结尾忘了把这行原样带回——替换语义是"删旧插新"，不是"在前面插入"。
- 修复：edit 补回声明行。
- 预防规则：用"函数声明行"做锚点做前插时，new_string 必须**以完整原锚点结尾**；
  插入后立即 `rg 'function '` 核对声明数量。
- 是否值得沉淀为 skill：否，属操作失误，但记录在案。

## 2026-09-05 - N2：`PathCost` 无固定费被误判为"币种无法折算"

- 症状：`test_via_path_pays_the_spread_twice` 里绕行路径 net_amount 为 None——
  只给了比例成本、没给固定费，理应有净额。
- 错误假设：path 3 内联成本逻辑里 `fee_in_target = _fixed_fee_in_target(...)` 返回 None
  时一律按"无法折算"处理；但 None 有两种含义——**没有固定费**（正常）与
  **固定费币种折算不了**（异常），代码只区分了后者。
- 根因：`_apply_costs`（单腿路径）写对了守卫
  （`if cost.fixed_fee is not None and fee_in_target is None`），path 3 复制逻辑时丢了这个条件。
- 定位方式：模块级测试 21 例中 1 例失败，最小化输入两分钟定位。
- 修复：path 3 补上与 `_apply_costs` 相同的守卫。
- 预防规则：**None 承载多义时（此处是"无值"vs"不可计算"），每个消费点必须先消歧**；
  复制粘贴的分支逻辑要逐条件核对，"结构像"不等于"语义同"。
- 是否值得沉淀为 skill：是——「None 的多重语义必须显式消歧」值得并入编码规约。

## 2026-09-05 - N2：首版路由测试把已折算成百分数的字段又乘了 100

- 症状：`assertAlmostEqual(payload["held_advantage_gross_pct"] * 100, 17.6)` 失败，
  实际值 17.600000000000002——payload 已是百分数单位。
- 错误假设：照抄 fx_carry 的 `carry`（分数 0.015）约定，以为 `_pct` 后缀字段也是分数。
- 根因：两个模块的百分数约定不一致且无文档；调用方只能靠猜。
- 修复：确立全仓命名规则（`_pct` 结尾 = 百分数单位），测试断言改为不乘 100
  并加注释；decisions.md 记录该规则。
- 预防规则：跨模块边界的字段单位必须在字段名里自证（`_pct`/`_pct` 之外的分数），
  新模块选约定时先查相邻模块并写进决策记录。
- 是否值得沉淀为 skill：否，规则已并入 decisions 与命名约定。

## 2026-09-05 - N3：GeneratedRegex 落在非 partial 类上，编译期才暴露

- 症状：给 `NotificationDispatcher` 加 `[GeneratedRegex]` 剥备注后缀的辅助方法后，
  Host 构建报 CS0260「类型声明上缺少 partial 修饰符」。
- 错误假设：源生成器特性只作用于方法，宿主类声明无关。
- 根因：`GeneratedRegex` 会为宿主类型生成分部实现，类必须 `partial`。
- 修复：`public sealed class` → `public sealed partial class`。
- 预防规则：往已有类里加 `[GeneratedRegex]` 方法时，同一次编辑就把类声明改成
  partial；这是编译期错误（不是运行期），构建即抓，风险低。
- 是否值得沉淀为 skill：否，属 API 记忆问题。

## 2026-09-05 - N2 差值首版：净额与毛额同池比较，"承诺绝不发生的事"第一版就发生

- 症状：差值测试失败值 -0.223847 与任何手算都对不上；探针输出显示 best 是 split
  （毛额 15.09），而 direct（净额 14.87）被排在它后面——毛额与净额被放进同一个
  max() 里比较。
- 错误假设：compare_value"净额优先、毛额兜底"即可——注释里写着"绝不静默混基"，
  实现却按路径各自选基线，混用恰恰发生了（split 因另一个 bug 只有毛额，
  它的毛额赢了别家的净额，最优路径指向了一条没扣成本的路径）。
- 根因：比较基线是**集合属性**（所有可比路径必须同基），不是**路径属性**；
  按路径各自兜底时，缺净额的路径自动占了"少扣成本"的便宜。
- 定位方式：手算与断言值对不上时停止猜测定律数字，写探针脚本打印全部路径的
  net/base 与 diff——10 秒看到 split 的毛额出现在净额队伍里。
- 修复：先判"是否全部可比路径都有净额"，统一用净额或统一用毛额；`best_path`
  同口径。
- 预防规则：**任何跨实体的比较（排序/最优/差值）先回答"全体在哪个口径上可比"，
  再取数**；"兜底"只能发生在整组层面，不能逐实体各自兜底。
- 是否值得沉淀为 skill：是——「比较口径是集合属性」值得并入 fx_paths 的既有规则。

## 2026-09-05 - N2 split 段重犯 None 多义错（N2 已记过的同一守卫）

- 症状：split 路径在只给比例成本（无固定费）时仍返回 gross_only；
  `fee1 is None or fee2 is None` 把"没有固定费"当成了"固定费币种折算不了"。
- 错误假设：修 path 3 时以为同类分支只有一处。
- 根因：split 段是从早期草稿独立写的，与 `_apply_costs`/path 3 的守卫
  （`fixed_fee is not None and fee_in_target is None` 才是"不可折算"）不同步。
  **error-ledger 里"复制分支逻辑逐条件核对"的预防规则正是为这种情况写的，
  写 split 时没有执行。**
- 定位方式：基线混用探针的副产品——split 本应有净额却没有，顺藤摸瓜。
- 修复：split 段补上与 path 3 相同的消歧守卫，`(fee or 0)` 计入。
- 预防规则：修一个 None-多义 bug 时，**全模块 rg 同一函数的所有调用点**，
  逐处核对守卫是否一致——"结构像"不等于"语义同"，第二处永远是复制粘贴的。
- 是否值得沉淀为 skill：是——升级既有条目为「修 None 多义 bug 必须全模块扫调用点」。

## 2026-09-05 - N4：ui_smoke.ps1 第三次丢 BOM（同坑第三踩）

- 症状：edit 工具改冒烟脚本（导航走查加"证据卡"页）后运行报
  `Missing closing '}'`，中文乱码——与 N1 批次完全相同的症状。
- 错误假设：error-ledger 已有两条记录 + skill-candidates 已有条目，以为"知道
  就等于会防"。
- 根因：edit 工具按 UTF-8 无 BOM 写回 .ps1，PS 5.1 无 BOM 按 ANSI 解码——
  知识存在不改变工具行为，只有流程改变行为。
- 定位方式：读前 3 字节（`3C 23 0A` = `<#`，无 BOM）；本次从看到乱码到恢复
  用了 1 分钟（规则已内化），但第 3 次发生本身说明该入口需要工具级修复。
- 修复：`UTF8Encoding($true)` 补回 BOM，零内容改动。
- 预防规则（升级）：**任何 .ps1 的 edit 之后、运行之前，检查前 3 字节**——
  不是"记得这条规则"，而是把它写进每次改 .ps1 的肌肉流程；根治方案是把
  冒烟脚本迁到 pwsh 7 或改用无中文注释版本（列入 handoff Next）。
- 是否值得沉淀为 skill：条目已存在，本次升级触发条件为"无条件检查"而非
  "怀疑时检查"。

## 2026-09-05 - SQLite 读回的 decimal 带残留比例尺，内容哈希不相等

- 症状：导出→再导入的手工流水未被判定为重复（Importable=2，应为 0）；探针显示种子字符串
  仅小数位不同（`0.0` vs `0`、`1000.0` vs `1000`），全部数值字段 `==` 相等。
- 错误假设：以为 decimal `==` 相等则 `ToString(InvariantCulture)` 也相等——忽略了
  `decimal` 的 scale 是值的一部分（1000.0m ≠ 1000m 在 ToString 上）。
- 根因：SQLite 列声明 TEXT，但参数写入侧把 decimal 序列化成带小数位的文本（如 "1000.0"），
  `GetDecimal` 忠实还原了该 scale；而 CSV 重建行经 `ParseDecimal` 得到 scale=0 的值。
  内容相同的两行，一路经 DB、一路经解析器，哈希输入就分叉了。
- 定位方式：临时探针逐字段比较全部相等后，复刻 `DeterministicId` 的种子字符串逐字符 diff，
  3 分钟定位。
- 修复：`DeterministicId` 的 decimal 字段统一走 `Norm()`（`"0.####…"` 28 位格式，
  去尾零、scale 无关）。
- 预防规则：**凡对"从 DB 重读的行"与"从文本/网络重建的行"做内容比较（哈希、去重、
  Diff），decimal 必须先做 scale 归一化**；`==` 相等不代表规范形式相等。
- 是否值得沉淀为 skill：是——「decimal 比较前先归一化 scale」是跨存储往返的通用陷阱，
  已加入 skill-candidates。

## 2026-09-05 - 证据卡回撤符号约定：-60.0 与 0.6 的比较永假

- 症状：max_loss 门禁测试不生效（回撤 60% vs 上限 20% 仍输出 positive）。
- 错误假设：以为 `max_drawdown_pct` 是非负幅度；实际存储为带符号负数（-60.0 = 跌 60%）。
- 根因：`behaviour_block.get("max_drawdown_pct")` 直接与正阈值比较，-0.6 >= 0.2 恒假。
- 定位方式：探针打印 behaviour 块看到 -60.0。
- 修复：门禁两侧都取 `abs()` 后比较幅度。
- 预防规则：跨模块消费"回撤/跌幅"类数值前，先确认符号约定（幅度 or 带符号变化），
  消费处显式 abs() 或在契约里写死"非负幅度"。

## 2026-09-05 - 测试类里 `const string Account` 遮蔽了 `Account.Create`

- 症状：`Account.Create("银行", …)` 编译错误指向 `string.Create`（参数 1 要 IFormatProvider）。
- 错误假设：以为是文件编码或括号问题，反复查了不可见字符与插值残留。
- 根因：测试类有 `private const string Account = "acct_main"`，标识符查找命中字段，
  `Account.Create` 解析为 `string` 的静态方法。
- 定位方式：编译器错误里的参数类型（IFormatProvider/Span<char>）是 string 方法签名的指纹。
- 修复：全限定 `Host.Portfolio.Account.Create`。
- 预防规则：测试夹具常量别用领域类型名（Account/Instrument/Money）；编译错误的参数类型
  指纹比行号更快定位"解析到错误类型"类问题。
- 是否沉淀 skill：否，但"看错误参数类型而非行号"值得记入 reflections。

## 2026-09-05 - 移动加权成本的除法残差（再次验证先比例后乘法）

- 症状：部分换出后成本 100.00000000000000000000000010 ≠ 100。
- 根因：先算单位成本 200/4500（循环小数截断 28 位）再乘 2250，残差进入金额；
  先算比例 2250/4500=0.5（精确）再乘总额 200 则精确。
- 修复：释放按 `pricedCost × (pricedShare/pricedAmountBefore)` 记账 + 微量钳制归零。
- 预防规则：与既有 FX 残差教训同源——**凡 decimal 比例运算，先除出精确比例再乘，
  禁止先算单价再乘数量**。已并入 skill-candidates 既有条目。

## 2026-09-06 - 自查：上一轮修复引入的四个新缺陷（R02/R04 最为典型）

- 症状：复审 2026-09-06 报告 10 个 P1，其中 R04（分批排名值缩小 200 倍）与 R01
  （跨币种暴露抵消）是**上一轮修复代码自身引入的**。
- 错误假设：R04——以为"归一化"就是把每腿除以自己的成本基再乘份额；实际上
  from_held/from_base 已按 frac 拆分，再乘一次 frac 并除以完整成本是双重缩放。
  R02——以为单位在两端各自"清楚"，没有写下一个端到端契约；UI 收 20、服务端当 0.2。
- 根因（共性）：修复时只让"自己的断言"通过——R04 测试只断言"小于毛额"（真值 13.09
  和错值 0.065 都满足），R02 客户端测试直接传 0.15 绕过 UI。**断言弱于反例时，
  测试就是在给错误背书。**
- 定位方式：复审 Agent 用最小反例数值（13.091525、60%×20%）逐个复现。
- 修复：见 worklog 2026-09-06。测试侧：凡"量纲"类断言必须写具体数值（等于/近似），
  禁止只写不等式方向；凡"单位"类契约必须有跨边界的端到端测试（UI 输入→API 载荷）。
- 是否沉淀为 skill：是——「反例断言必须钉死数值，方向性断言不算反例」。

## 2026-09-06 - 修复 WP2 时被不变量测试逼出的第二个费用缺陷

- 症状：`test_unknown_costs_never_produce_status_ok` 失败——(spread=0.5, fee=None) 输出 ok。
- 错误假设：三态改造时只盯"比例成本未知"，认为"固定费缺失但点差已知"可以从净额里跳过
  （fee_in_target None 分支返回毛额-0）。实际上这正是"未知当零"的另一种形态。
- 根因：`_apply_costs` 里 `if fee_in_target is None: net = after_pct`——None 既表示"没填
  固定费"又表示"币种不可折算"两个语义，前者应 fail-closed，后者也 fail-closed，但两者
  都没被拦。
- 定位：性质测试（参数化三态组合）比样例测试先抓住它——样例只测了计划点名的形态。
- 修复：固定费 None → 明确拒绝；`PathCost.zero()` 补全两字段。
- 预防规则：改造某字段语义为三态时，用参数化不变量测试枚举 (None, 0, 正值) 的全组合，
  而不是只改计划点名的那个形态。

## 2026-09-06 - "内核测试全绿"掩盖"链路断开"（复审 remediation-recheck 四项 P1 的共性）

- 症状：四项 P1 全是同一形态——Python/C# 内核正确，直调内核的测试通过；桌面→客户端→
  路由的某一段丢了参数或没接上（funding_currency 不在请求字典、固定费输入框不存在、
  预填调用废弃 API、卖出现金用请求额）。
- 错误假设：以为"内核函数有测试 + 客户端构造字典时也写了字段" = 链路通。实际上每一段
  各自正确、拼接处断开。
- 根因：没有跨边界的集成测试；测试直接构造内核入参（甚至手工注入内核才有的字段），
  把链路序列化这一跳完全绕过。
- 定位方式：复审沿真实请求链逐段比对字段。
- 修复：每项补真进程集成测试（broker 起真 Python 服务、HTTP 客户端发请求、断言卡片/
  路径结果）；另发现两个测试自身坑——响应有 `{ok, task, card}` 包装层、broker 默认
  provider=auto 需宿主环境显式 fixture。
- 预防规则：**凡"桌面→客户端→路由→内核"的参数贯通改动，验证必须包含至少一个真进程
  链路测试**（fixture 行情），直调内核的测试只能证明内核，不能证明接线。
- 是否沉淀为 skill：是——「链路贯通 = 真进程集成测试，不是内核测试」。

## 2026-09-07 - 加 XAML 面板复制了 Grid.Row，两输入区同格叠放

- 症状：汇率页比例成本与固定费两组输入在真实窗口里互相覆盖，固定费不可点。
- 错误假设：以为"新面板放进同一个 Grid、构建通过、页面能导航"= 布局正确。
- 根因：复制比例面板时连 `Grid.Row="2"` 一起带走；WPF 允许同格多元素，后声明者覆盖
  前者的绘制与命中。编译器与导航级冒烟对"同格叠放"完全沉默。
- 定位方式：复审静态读 XAML 行号（父 Grid 4 行 vs 两个 Row="2" + 结果 Row="3"）。
- 修复：拆独立行；并在冒烟脚本加程序化布局断言（UIAutomation 找输入框、宽高>0、
  两两边界不相交）——该类缺陷从此有机器级守门。
- 预防规则：**新增面板先看父容器的 Row/Column 定义再放；布局类改动必须过布局断言**
  （构建通过+能导航不是证据）。
- 是否沉淀为 skill：是——「XAML 布局改动的证据 = 程序化边界断言，不是导航成功」。

## 2026-09-07 - 卖出校验用了钳制后的量当基准（F-02A 的思维错误）

- 症状：持仓 20%、US 切片 10%、卖 5% → 输出 us=5%（剩余持仓 15% 装在 5% 地区里）。
- 错误假设：以为"本次卖出能从切片里拿得出来"就够了。
- 根因：自洽性的正确基准是**完整持仓**——输入声称该标的占 20%，它的地区就必须至少
  20%；与本次卖多少无关。用钳制后的 candidate_weight 比较，等于把"输入是否自洽"
  偷换成了"这次操作是否付得起"。
- 修复：比较对象改为 current_weight_of_symbol；超卖反例没抓住它恰因 90% 钳到 20%
  后两者数值相等——反例覆盖了边界却错过中间态（部分卖出）。
- 预防规则：**校验输入自洽性时写清基准量是什么**（存量 vs 本次增量）；反例要覆盖
  中间档（部分执行），不能只有边界档（全卖/超卖）。

## 2026-09-07 - 第三轮复审：外部入金分母混用与日期回退

- 症状：旧组合 10 万、旧持仓 2 万、外部买入 1 万，返回 29.09% 并误报 28% 上限；应为 3/11=27.27%。
- 错误假设：旧权重可以直接加上以新总额为分母的增量权重；现有测试复用了该错误公式。
- 根因/检测：旧持仓未稀释；以生产 `assemble_card` 做金额核算反例并检查风险结论复现。
- 待修：R3-01 使用同一新分母，纠正测试；预防为独立金额核算及跨维度一致性测试。
- 另一个症状：所有市场日期无效时，日期 helper 拒绝的值又经 fallback 成为 market_as_of；R3-02 待修为最终输出未知并保留来源警告。
- 本轮工具异常：默认 shell、内置补丁与 computer-use Node 受 sandbox helper 初始化故障影响；shell 和 apply_patch 命令经授权完成检查与文档写入，图形检查重置后仍失败，未测边界已在报告注明。

## 2026-09-07 - 外部资金的"旧占比+新增/新总额"公式（R3-01，且旧测试固化了错误）

- 症状：20% 持仓 + 10 000 外部买入 → 显示 29.09%（正确 27.27%），误触发 28% 上限；
  同一张卡里 US 地区 after 27.27% < 单仓 after 29.09%。
- 错误假设：以为"权重加权重"在分母变化时仍成立。旧占比是旧分母下的数字，分母变了
  必须先换算（旧占比 × 旧总额 ÷ 新总额）再叠加——分布切片守恒修复时改了地区/币种的
  换算（缩放旧桶），唯独漏了单一持仓本身。
- 根因：修复 WP1 时对"守恒"的验证只查了分布合计=1，没查"单仓与分布跨维度一致"；
  且当时的测试把错误值当期望写死（0.3 + 10000/110000），测试反而在保护 bug。
- 定位方式：复审用独立金额核算（30000/110000）对照输出。
- 修复：单仓公式改金额口径（旧市值+新金额）/新总额；测试期望全部改为独立核算值；
  新增跨维度一致性断言（标的 ≤ 地区/货币 after；地区只含此标的时两者相等）。
- 预防规则：**权重算式的期望值必须用金额独立核算得出，不得从实现公式反推**；分布
  合计=1 只是必要条件，还要查跨维度一致性（部分 ≤ 所属切片）。

## 2026-09-07 - UIA 自动化的四个坑（recheck-4 UI 验收程序化过程）

- 症状：焦点断言报"找不到属性 IsKeyboardFocusedProperty"；链式调用报"String 不含
  GetCurrentPattern"；ComboBox 选 USD 后固定费仍按 CNY 折算（毛−净差=毛×1/6.7）。
- 错误假设：以为 UIA 属性名与 WPF 依赖属性同名、PowerShell 方法链与 C# 一样、
  ComboBox 子项 Select 等价于用户选择。
- 根因：①正确属性是 HasKeyboardFocusProperty；②PowerShell 需先 $x = Get-Edit… 再
  $x.GetCurrentPattern(…)；③折叠 ComboBox 的 SelectionItem.Select 不总是提交选择，
  必须 ExpandCollapsePattern.Expand() 后再 Select。
- 定位方式：逐步加诊断落盘（表单文本 dump）+ 本地最小正则/差值核算——"差值恰为毛×
  1/6.7"直接指向费币种未切到 USD 而非后端算错。
- 预防规则：UIA 断言的期望值 provider 无关（不写死行情数字，用分组捕获+数值比较）；
  ComboBox 自动化先 Expand 再 Select；PowerShell 方法链先赋值再调用。
- 是否沉淀为 skill：是——「WPF ComboBox 的 UIA 选择要先 Expand」与「断言数字必须
  与数据源解耦」两条已并入 skill-candidates 的 UIA 模板条目。

## 2026-09-08 - UI E2E 的 fixture 假设与路径覆盖失效

- 症状：新增冒烟两次场景 A 请求 HTTP 422，失败字段只输出 AutomationElement.Current.Name。
- 错误假设：父进程 fixture 必然控制桌面子进程；填金额就代表已选择持有币种；任意净额文本就能证明对应路径贯通。
- 已确认根因：桌面传入真实 MarketCredentialsStore，broker 用其覆盖 provider；持有下拉仅预填时填充；正则只匹配首条文本；失败属性位于插值子表达式外。
- 限定：实际 provider 非 fixture 已确认，HTTP 422 的具体响应体/校验原因尚未确认。
- 检测：两次 JSON + 保留窗口读取真实状态文本和 /health + 沿 UI→Host→Python 静态追踪。
- 修复状态：未修改实现，登记 R5-01/02/03 给开发 Agent；需隔离状态、实际 provider 守卫、路径级断言和有效诊断。
- 预防：相对数值断言不等于数据源隔离；检验选中值和被断言路径身份；UIA 焦点不等于鼠标/Tab。
- 沉淀为 skill-candidates：是，见同日条目。

## 2026-09-08 - 隔离账本预置的三个坑 + UIA 卡片文本结构（recheck-5 整改过程）

- 症状（连环）：①seed 脚本 `-f` 格式化报 "not all arguments converted"（SQL 里的 % 被当
  格式符）；②修好后 venv Python 报语法错误（文件带 BOM）；③再修后应用 XamlParseException
  "no such table: settings"；④逐卡断言报"持仓卡无毛/净到账文本"。
- 错误假设：以为预置一张 transactions 表就够（应用会建其余表）；以为卡片标签和金额在
  同一个文本元素里。
- 根因：①PowerShell 里用 Python %-formatting 拼 SQL 天然与 `%` 冲突；②`Set-Content
  -Encoding UTF8` 在 PS5.1 写 BOM，venv Python 拒收；③应用打开 user_version=9 的库**跳过
  迁移**（设计如此），缺表即崩——且崩在 MainWindow 构造期，以 XamlParseException 包装，
  真因在 inner exception；④XAML 模板里 Label 与 AmountsText 是两个 TextBlock → 两个
  UIA 元素，按行 join 全窗口文本再匹配必然落空。
- 定位方式：读 stderr 完整日志的 inner exception；手动跑 seed 脚本看报错；读 XAML 模板。
- 修复：全表预置（六张表 + user_version=9）；占位符 `.Replace()` + `WriteAllText` 无 BOM；
  `Get-CardText` 从 Label 元素经 TreeWalker 上溯至含"毛到账/净到账"的容器再收集。
- 预防规则：**预置已有版本的数据库必须复刻全 schema**（迁移只在版本低时跑）；生成给
  Python 的脚本用无 BOM UTF-8；UIA 多元素文本断言先读 XAML 模板确认元素结构，别假设
  "看起来在一行就在一个元素"。
- 是否沉淀为 skill：四条均已并入 skill-candidates 的 UIA/seed 模板条目。

## 2026-10-07 - 第六轮复审：隔离并非目录前缀成立即可

- 症状：TestIsolation 接受正式产品根；无效测试根被桌面当成未启用测试，继续打开正式状态。
- 错误假设：LocalAppData 内的路径就是隔离路径；helper 拒绝了输入就等于启动流程 fail-closed。
- 根因：允许根过宽，且 TryResolveStateRoot 的“未设置/非法”共用 false，调用者忽略 reason。
- 检测：对当前编译的生产函数做无写入探针，并追踪 MainWindow 根选择分支；未用错误配置启动正式账本。
- 修复状态：待 R6-01；要求显式模式三态、独立测试根、正式根拒绝和启动路径级负例。
- 同轮经验：隐藏字段仍可能被统一解析；复制安全函数的负例不能防止生产函数回归；更名/区间切换要一起更新验证与说明。
- 沉淀：见 skill-candidates 的第六轮复审条目。

## 2026-10-07 - 第七轮复审：修一个方向不等于修复输入边界

- 症状：买入→入金已清空数量，反向切换仍被旧金额阻断；标题更新为 5/10 年但失败/乱序时数字属于旧区间；盘符相对路径仍被接受。
- 错误假设：一次清空足够覆盖所有类型；请求发出时改标题等同响应一致；IsPathRooted 等同完整绝对路径。
- 根因：无条件解析全部字段；未把标题与数据作为同一响应状态，且无请求归属防护；选错路径判定 API。
- 检测：未显示控件调用实际生产方法、内存 HTTP 控制响应顺序、允许目录作为 cwd 的路径反例；未操作正式账本。
- 修复状态：交开发 Agent 按 R7-01～03 修复并加长期测试，本审核不改业务实现。
- 预防：按交易类型选择解析字段；只采纳当前有效响应并原子更新；GetFullPath 前验证 IsPathFullyQualified。已沉淀 skill-candidates。

## 2026-10-07 - 第八轮复审：响应内部一致不等于响应属于当前选择

- 症状：10 年结果先成功，旧 5 年响应后到，标题/行/状态一起倒退为 5 年；下拉仍选 10。
- 错误假设：将标题移动到 await 后就完成了异步区间一致性修复。
- 根因：下拉仍可并发请求；成功/catch/finally 都没有请求身份校验；展开说明也未纳入区间状态。
- 检测：原样 R7 内存 HTTP 探针控制响应完成顺序，不依赖行情服务速度；静态核对展开文本。
- 修复状态：R7-02 保持 P2，交开发 Agent 处理请求归属/失败状态/说明并补正式测试；R7-01/R7-03 功能缺陷关闭。
- 预防与沉淀：同一异步加载的成功、失败、清理状态都受同一个请求身份约束；测试覆盖旧成功和旧失败晚到，见 skill-candidates。

## 2026-10-07 - FA1：UIA 鼠标命中坐标在 200% 缩放下全部落错元素

- 症状：`ui_dpi_probe.ps1` 在 200%（192 DPI）显示器上对 6 个成本框的点击 `hit_id` 全部为
  `LegGrid`（页面上方的表格），焦点也为空；同一脚本在 100% 下历史结果正常。
- 错误假设：`UIA BoundingRectangle`（物理像素）需要除以缩放系数才能用于 `SetCursorPos`。
- 根因：对 DPI 感知的调用方，`UIA BoundingRectangle` 与 `SetCursorPos` 都是物理像素坐标，
  除以 scale 后光标落在页面一半高度处。100% 下除以 1 掩盖了该错误。
- 检测：探针 JSON 里 `hit_id != target`，与"焦点应落在被点击框上"的断言矛盾。
- 修复：去掉 `/scale` 除法（`scripts/ui_dpi_probe.ps1`）；修正后 200% 下 6/6 hit=focus=目标框。
- 预防规则：测试工具涉及屏幕坐标时，先在目标缩放下验证"命中=目标"这一自证断言，不要假设
  坐标空间一致；100% 单一环境通过不能证明缩放相关逻辑正确。
- 是否成 skill candidate：否（一次性坐标语义，规则已入探针注释）。

## 2026-10-07 - FA1：用错环境变量启动分析核心，孤儿进程阻塞冒烟门禁

- 症状：冒烟 Provider 门禁拒绝 `/health`（process_id 不在桌面进程树）；根因是执行者此前探测
  fx/carry 时用 `CAISHENFOLIO_SERVER_PORT` / `CAISHENFOLIO_BIND_PORT` 环境变量试图改端口——
  核心端口实际只由 CLI `--port` 决定（`http_server.main`），错误变量被忽略，服务按默认 8765 启动，
  且以 shell `&` 后台方式脱离了生命周期管理，成为占用 8765 的孤儿链。
- 根因：假设"端口能用环境变量配置"未查证；`&` 后台进程随 shell 退出失控。
- 修复：清理 8 个本会话孤儿 fixture 核心（验证归属 + 审计记录 orphan-core-cleanup.json）；
  探测改用 `--port/--provider` CLI 参数与 Bash 工具的后台任务（可 TaskStop）。
- 预防规则：核心端口/数据源用命令行参数（进程内已有 `--provider` 的先例）；长驻测试进程用可
  管理的后台任务而非裸 `&`；冒烟门禁拒绝非己进程树是正确行为，先查自己的遗留再怀疑门禁。
- 是否成 skill candidate：并入 WpfTestHost 条目的"测试进程生命周期"注意点。

## 2026-10-07 - FA1：探针/诊断脚本只清理直接子进程，孙进程核心成孤儿

- 症状：最终收尾检查发现 8765 仍被核心链占用（venv launcher + 解释器），而其父桌面进程早已停止。
- 根因：`ui_dpi_probe.ps1` 的 finally 与临时诊断都只 `Stop-Process` 桌面本身或直接 python 子进程；
  核心真实进程是桌面的孙进程（launcher→interpreter 链），跨过一层就漏掉。
- 修复：验证归属后停止该链并复验正式账本指纹未变（orphan-core-cleanup-2.json）。
- 预防规则：清理多级进程树必须按"启动时记录的根 pid 做整树遍历"（ui_smoke.ps1 的 ownedIds
  聚集循环是正确样板），单层 ParentProcessId 过滤在 launcher→interpreter 结构下必然漏杀。

## 2026-10-07 - FA1 终审：WPF 不调用 Run 仍执行排队启动

- 症状：普通 C# 测试 615 全绿但留下正式路径 cache+upstream 核心；原 WpfTestHost 的“未显示控件”隔离声明不实。
- 根因：Application 构造函数排队启动，InitializeComponent 设置 MainWindow StartupUri，Dispatcher.Run 仍执行它。
- 修复：统筹改资源专用普通 Application，编译时嵌入真实 App.xaml 并只解析 Resources，补 ApplicationIdle 后无主窗口/StartupUri/窗口集合的断言；616 全过，结束无核心。
- 本轮两次失败尝试：派生 App 加载原 BAML 类型不兼容；StartupUri=null 被 setter 拒绝。两条路径已放弃，日志保留，不计通过。
- 预防：资源宿主不得调用生产应用入口；不仅断言值，还要检查窗口/子进程与正式路径副作用。禁止从“无 stderr”或“偶尔成功”推断不是代码问题。
- 原始重试现场存在覆盖，不能补造；每次尝试单独目录、进程归属与启动阶段证据是后续验收要求。已纠正 skill-candidates 错误模板。

## 2026-10-08 - 引导查找需按窗口类型，不能只有名称

- 症状：新增有界等待第一次真实运行报“多个首次引导窗口”；`cold-1.json` 原始失败保留。
- 根因：同一 PID 的对话框和内部标题文本可能同名，Descendants + Name 同时选中两者。
- 修复：增加 ControlType.Window，与 PID/标题联合匹配；后续固定五次冷启动与两次完整 UI 均过。
- 预防：窗口定位至少绑定进程身份与控件类型；临时“没看到”不能当作“非首次启动”；出现/关闭均有界等待，跳过关键观察必须失败。
- 证据规则：独立 attempt 目录、原子拒覆盖、manifest/二进制指纹/起止/退出码；修代码后的新批次保留旧失败版本，不靠同版重试筛绿灯。已补入 skill-candidates。
