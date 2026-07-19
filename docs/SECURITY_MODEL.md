# Caishenfolio Security Model (P0)

## Principles

1. Deny by default.
2. Least privilege.
3. Host owns authority; Python is controlled execution.
4. Research/simulation outputs are never investment advice packaging.
5. Fail closed on unknown tools, paths, providers, and binds.

## Capability Classes

| Capability | Default | Notes |
|---|---|---|
| ReadOnly | allowed when registered | Queries, metadata |
| FileRead | deny | Requires import root |
| FileWrite | deny | Requires artifact/run root |
| Network | deny | External HTTP/API |
| Shell | deny | Explicit policy only |
| GeneratedCode | deny | Sandbox + run root required |
| ExternalWidget | deny | Isolated WebView2 later |

## Path Roots

Separate roots:

- `Import` — user documents/import
- `Artifact` — reports/exports
- `Run` — generated code / ephemeral runs
- `State` — local DBs, runtime logs, task mirror

Rules:

- No UNC paths
- No `..` traversal outside root after full resolve
- Validate on both Host and Python sides

## Loopback Policy

Managed Analytics Core must bind only to loopback addresses. Binding to `0.0.0.0`, LAN, or public IPs is rejected by Host policy.

## Redaction

Diagnostics and logs must redact:

- password / secret / token / api_key / authorization style values
- local filesystem paths when exporting support packages (later full export pipeline)

## Audit Direction (post-P0)

Security-relevant events write durable audit records. If audit write fails, surface an audit-gap warning.
