# Quality Gates

Use this file before risky changes, handoff, delivery, or release. Apply only relevant categories and mark the rest as N/A.

Statuses:

- Pass: verified with evidence.
- Warn: partial coverage or residual risk.
- Fail: issue must be fixed or explicitly deferred.
- N/A: not relevant to this project or change.

## Pre-Change Gate

- Objective, dependencies, risks, impact scope, and acceptance criteria are clear.
- Existing worktree state has been inspected.
- Affected files or modules are identified.
- Verification approach is known before implementation starts.
- Security, privacy, data, API, frontend, or release-sensitive areas are flagged when relevant.

## Pre-Delivery Gate

### Security and Privacy

- Inputs are validated and outputs are encoded where applicable.
- Secrets, tokens, passwords, and PII are not logged or committed.
- Authentication and authorization changes are reviewed for least privilege.
- File paths, uploads, system commands, and outbound requests are checked for injection or traversal risk.

### Data, API, and Consistency

- Schema changes use migrations where applicable.
- API shape, versioning, validation, pagination, and idempotency are reviewed when applicable.
- Money uses integer minor units or decimal types, never binary floating point.
- Time storage and transfer use UTC when the project handles time-sensitive data.

### Code Quality and Maintainability

- The change follows existing project patterns.
- Duplication, dead code, broad types, empty catches, and unhandled TODO/FIXME/HACK comments are reviewed.
- Error handling is explicit and does not silently swallow failures.
- Configuration is separated from code and required config fails fast.

### Testing and Verification

- Unit, integration, E2E, or manual verification is selected according to risk.
- Boundary cases and regression scenarios are covered where relevant.
- Verification commands and results are recorded in `current.md` or `handoff.md`.
- Known unverified areas are stated explicitly.

### Frontend, Accessibility, and UX

- Loading, empty, error, and boundary states are handled when UI is affected.
- Keyboard access, labels, alt text, focus visibility, and color contrast are checked when UI is affected.
- Mobile layout and user-visible text are checked when frontend behavior changes.

### Operations, Dependencies, and Release

- Dependency changes are reviewed for lockfiles, unused packages, vulnerabilities, and licenses.
- Logs, metrics, health checks, retries, timeouts, graceful shutdown, and rate limits are considered when relevant.
- README, API docs, ADRs, changelog, release notes, or migration notes are updated when needed.
- Rollback or recovery path is known for risky delivery.

## Gate Result Template

### YYYY-MM-DD - Gate name

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | N/A |  |  |
| Data, API, and consistency | N/A |  |  |
| Code quality and maintainability | N/A |  |  |
| Testing and verification | N/A |  |  |
| Frontend, accessibility, and UX | N/A |  |  |
| Operations, dependencies, and release | N/A |  |  |

Open issues:

- None.

### 2026-07-19 - P2 delivery

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | State root under LocalAppData; loopback client only; no live credentials | Path roots registered on Desktop startup |
| Data, API, and consistency | Pass | Fixture fail-closed research; EXCHANGE:SYMBOL; disclaimer on research | Core in-memory + Host SQLite mirror |
| Code quality and maintainability | Pass | `ITaskStore` abstraction; typed client DTOs | stdlib HTTP retained |
| Testing and verification | Pass | `scripts/verify_p2.ps1`; C# 39; Python 17 | Manual Desktop click-through not run |
| Frontend, accessibility, and UX | Warn | Search/bars/research panel works; no a11y pass | Dark theme basic controls |
| Operations, dependencies, and release | Pass | Microsoft.Data.Sqlite 8.0.11; verify script | No packaging yet |

Open issues:

- Manual Desktop smoke against system Python still pending.

### 2026-07-31 - R0~R5 理财重构交付

| Category | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Security and privacy | Pass | 账本 SQLite 落在 State root；CSV 导入只读文件不执行；导出走 Artifact root | 未引入任何下单/凭证通道 |
| Data, API, and consistency | Pass | 金额全 decimal + 货币标签，跨币种运算抛错；SQLite decimal 存 TEXT；`PRAGMA user_version` 迁移 v1→v2 | 交易日按 DateOnly 存，避免时区漂移 |
| Code quality and maintainability | Pass | 语义单一来源（交易所注册表 / 阶段号常量）；替换两处重复的 CN 代码启发式 | MainWindow 旧研究页仍为 code-behind，见下方开放问题 |
| Testing and verification | Pass | C# 42→183；Python 47(6红)→82；`dotnet build` 0 错 0 警 | 真实行情联网路径仍未覆盖（需 network） |
| Frontend, accessibility, and UX | Warn | 启动冒烟通过（窗口标题 v0.10.0，默认落总览页）；未定价持仓在表格与导出中均显式标记 | 未做键盘导航与对比度专项检查 |
| Operations, dependencies, and release | Pass | 无新增第三方依赖；README/AGENTS/docs 已更新 | 安装包仍未做 |

Open issues:

- 旧研究页（行情/计划/网格/回测/对比）仍是 1225 行 code-behind，未 MVVM 化；新增的理财页已是 ViewModel + 绑定。
- akshare 的 `fx_spot_quote` 与债券接口未在联网环境验证，仅保证缺接口时 fail-closed 并给出可读提示。
