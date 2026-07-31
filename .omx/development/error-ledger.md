# Error Ledger

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
