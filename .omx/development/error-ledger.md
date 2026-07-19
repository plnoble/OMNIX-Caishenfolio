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
