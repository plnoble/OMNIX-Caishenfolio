# 第五轮复审：UI 验收补充（2026-09-08）

## 1. 结论与范围

**本轮 UI 补充验收暂不通过；第四轮代码修复通过的结论不变。**

R3-01 / R3-02 继续关闭。本轮没有发现需要重新打开这两项的证据，也未修改产品代码。
新增焦点/可写检查取得有效机器证据；新增表单端到端检查在本工作区连续两次失败，不能认定“扩展冒烟全绿”或关闭桌面净额验收。
需处理 **R5-01 / R5-02 两项 P2 验收问题**，以及 **R5-03 一项 P3 诊断/口径问题**。

本次针对新增 scripts/ui_smoke.ps1 检查及其真实桌面→Host→Python 链路复核，并重跑基础回归。
不扩大为产品全量审计；DPI、窄窗口、鼠标命中、Tab 遍历、真人体验均不以代码检查替代。

## 2. 独立实测

| 检查 | 本轮实际结果 |
|---|---|
| dotnet build Caishenfolio.slnx --no-restore | 通过，0 警告 / 0 错误 |
| dotnet test Caishenfolio.slnx --no-build --no-restore | 587 通过：Host 552 + Desktop 35 |
| 应用 managed venv，python -W error 全量 | 运行 449 项：448 通过 / 1 跳过 |
| 新增 UI 冒烟，运行 1 | 失败：场景 A 请求失败；焦点、布局、九页导航已通过 |
| 新增 UI 冒烟，运行 2（保留窗口诊断） | 同样失败；页面显示 HTTP 422 |
| 场景 B：固定费 1 USD | 未执行到，不能计通过 |
| 设置窗口（本次完整脚本尾部步骤） | 被前序失败阻断，不能沿用旧报告算本轮通过 |
| git diff --check | 无空白错误；存在 Git 的 LF/CRLF 提示 |
| 门控真进程 5 项、Release MSI | 本轮未另行重跑；第四轮通过证据仍属第四轮 |
| DPI / 窄窗口 / 鼠标点击 / Tab / 真人任务 | 未验收 |

可复核的本地报告：

- [第一次冒烟](D:/Agent/Project/OMNIX-Caishenfolio/.local/ui-smoke/recheck-5-ui-smoke-report.json)
- [第二次诊断冒烟](D:/Agent/Project/OMNIX-Caishenfolio/.local/ui-smoke/recheck-5-ui-diagnostic-report.json)

两份 JSON 均为 status=failed。失败字段因 R5-03 未保留真正的页面错误。
第二次运行直接读取 UIAutomation 文本得到：

> 路径比较失败：Response status code does not indicate success: 422 (Unprocessable Entity).

同一运行中 /health 的关键字段为：

~~~json
{
  "status": "ok",
  "market_provider": "cache+upstream",
  "market_provider_ready": true,
  "market_data_synthetic": false
}
~~~

这段是复审时观察值的摘录，不是冒烟 JSON 已自动记录的字段。
**已证实实际并非 fixture；尚未取得 422 响应体，因此不能断言 422 的具体校验字段或上游错误原因。**
测试后已关闭本轮启动的应用；未为使测试变绿而修改用户凭据或账本。

## 3. 整改项

### R5-01（P2）：fixture 未在真实桌面链路生效，冒烟依赖用户配置

定位：

- scripts/ui_smoke.ps1:577：仅在父进程设置 CAISHENFOLIO_MARKET_PROVIDER=fixture。
- src/Caishenfolio.Desktop/MainWindow.xaml.cs:69、672：加载用户 State 中的 MarketCredentialsStore，并传给 broker。
- src/Caishenfolio.Host/Python/AnalyticsCoreProcessBroker.cs:62：非空凭据分支把 ApplyToEnvironment 的结果写入子进程环境，覆盖继承值；只有不传凭据的分支才保留该环境变量。
- src/Caishenfolio.Host/MarketData/MarketCredentialsStore.cs:54：始终注入保存的 MarketProvider，空值使用 auto。
- python/caishenfolio_core/market/factory.py:23：按子进程最终环境选择 provider。

影响：

在保存了非 fixture 配置、或使用默认 auto 的桌面环境中，脚本不能保证离线确定性。本次实测 cache+upstream / synthetic=false 与此链路一致，且表单请求连续失败。
“预期净额使用相对关系”只消除了对具体报价数值的依赖，**没有消除网络、凭据、缓存、用户本位币和账本状态依赖**。
这首先是验收基础设施缺陷，不据此认定生产 provider 的优先级设计错误。

整改要求：

1. 由 C# Host 显式提供受约束的测试启动配置与独立测试数据根，使用独立状态、缓存、凭据配置和确定的 fixture 数据；不覆写用户正式配置或给正式账本加测试交易。
2. fixture 选择应覆盖实际桌面启动分支，而不只覆盖 credentialsStore=null 的测试分支。不要简单把生产环境改为“任意外部环境变量永远优先”，破坏 Host 权限边界。
3. 表单操作前读取实际 /health 并校验 fixture + synthetic=true；错误数据源应立即明确失败，不能继续等待 40 秒后只报告净额未出现。
4. 记录实际 provider、窗口/应用标识、场景输入和失败步骤；禁止输出凭据及无关个人数据。
5. 在隔离测试配置中分别覆盖“初始 provider=auto”和“已有非 fixture 配置”两种前置状态，验证 smoke 仍使用自己的 fixture；不得依靠开发者手工切换数据源。
6. 若测试仍返回 422，捕获脱敏错误详情并定位具体原因；不得把所有 422 归结为网络或简单跳过。

验收：连续两次在测试自有状态下运行，实际 provider 均为 fixture、完整场景通过；用户原状态不受影响；异常 provider 的负例明确失败。

### R5-02（P2）：未设置持有币种，首条文本匹配无法证明持仓路径贯通

定位：

- scripts/ui_smoke.ps1:365、391：只选择固定费币种并填写持有金额/成本，没有选择 FxHeldCombo，也没有调用账本预填。
- src/Caishenfolio.Desktop/Wealth/FxCarryView.xaml:139：FxHeldCombo 初始无项目。
- FxCarryView.xaml.cs:239、251：只有“按账本填入”流程才填充并选中持有币种。
- FxCarryView.xaml.cs:275、285、358：未选币种时，已持有金额/成本不会被解析成请求持仓，held_currency 被传为 null。
- python/caishenfolio_core/research/fx_paths.py:352、387、420：持仓/绕行路径依赖 held；新本币直换路径可以独立存在。
- scripts/ui_smoke.ps1:419、471：两次均用 Regex.Match 取全窗口首条毛额/净额文本；状态说明也只按全窗口存在检查。

影响：

即便修好环境后测试变绿，它仍可能只验证“新本币直接换 USD”，没有证明填入的 2100 持有金额、100 成本或持仓费用进入页面结果。
没有路径身份的首条匹配，还可能把一张卡的数值与另一张卡的状态拼成通过条件。

证据边界：这是静态调用链确认的覆盖缺口；本轮实际运行在场景 A 请求失败，**没有把一个成功的 base-only 运行冒称为已实测**。

整改要求：

1. 在 R5-01 的隔离状态中准备可追溯的持仓样例，明确本位币 CNY、目标 USD、持有 JPY，再通过真实 UI 预填/选择并核对选择值和金额。不要向用户账本插入样例。
2. 至少分别定位“已持有 JPY 直接换 USD”和“新 CNY 直接换 USD”两张卡，逐卡断言输入、毛额、净额、成本状态属于同一路径；可增设稳定 AutomationId。
3. 场景 A：两张卡分别验证零费净=毛。场景 B：两张卡分别验证固定费 1 USD，净=毛−1，币种为 USD。
4. 不得通过只改新资金费框就误认为持仓费框也已贯通；增加可区分的费用输入或断言以排除串线。
5. 费用留空应有明确的毛额/未知成本页面证据（沿用第四轮 §5#2）。当前 B 把绕行固定费留空，却不检查该路径，不可将绕行费用分支也计为已覆盖。
6. 等待本次提交完成后再取对应结果，避免把旧结果或任意一张卡当成当前场景的成功证据。

验收：报告写清验证了哪些路径；删除持有币种选择或故意接错持仓费输入时，测试必须失败。未覆盖的路径显式保留未测。

### R5-03（P3）：失败详情插值错误；焦点检查被扩大为点击/Tab 证据

定位：

- scripts/ui_smoke.ps1:426：.Current.Name 写在 PowerShell 子表达式外，实际输出 AutomationElement.Current.Name 字面描述，而非错误正文。
- scripts/ui_smoke.ps1:243：注释声称检查 Tab 移出焦点，但函数实际仅 SetFocus。
- .omx/development/quality-gates.md:470：把本检查描述成“可点击”，没有鼠标命中证据。

整改与验收：

1. 先把失败节点赋给变量，再读取 .Current.Name；用已知错误文本测试 failure 字段确实包含正文。
2. 焦点检查准确标注为“UIA 可写、可聚焦、非屏外”；SetFocus/ValuePattern 不等于鼠标点击或 Tab 遍历。
3. 修改脚本注释及记录口径；点击、Tab 若未另测，继续保留开放。
4. 不要把 UIAutomation 不可用导致的 skipped 计作验收通过。

## 4. 给下一位 Agent 的执行顺序

1. **先修 R5-01**：隔离启动状态，断言实际 provider，恢复可重复的页面请求。
2. **再修 R5-02**：显式准备/选择币种，按路径逐卡断言；不要只追求任意净额文本出现。
3. **同步修 R5-03**：保留有效诊断，纠正焦点/点击/Tab 的声明边界。
4. 重跑构建、C#、managed Python；若调整 Host 启动配置，补启动分支回归并重跑受影响的真进程测试；真实启动扩展冒烟连续两次。
5. 提交代码差异、两次完整 smoke JSON、实际数据源和场景断言摘要，再请求复审。所有计数保留 pass / skip / fail 区分。
6. 上述通过后推进人工 DPI 三档、窄窗口/低分辨率、鼠标/Tab、真人新手任务；每项记录尺寸/DPI、操作、实际结果及截图或录屏。

不要重做已关闭的 R3-01/R3-02；显式 CashTransfer、N6/N7 按原产品计划另行推进，不和本次测试整改混成一个工作包。
