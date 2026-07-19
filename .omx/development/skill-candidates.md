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
