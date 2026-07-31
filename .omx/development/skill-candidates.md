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
