# Git 交付清单（2026-10-07）

HEAD 仍是 `0c3e3b3`。本批结束时暂存区为空，没有 commit，没有 push。

计数：59 个已跟踪文件有未暂存改动，62 个未跟踪路径。比 2026-10-07 盘点开始时多出的，是本批新增的隔离代码、测试、验收文档和负例脚本。

## 建议纳入一次本地提交的来源

这些是功能与其测试，不是生成物。提交前仍要由用户确认，本批没有自动暂存。

- `.omx/development/` 中的计划、复审、状态、验收和本清单。
- `python/caishenfolio_core/` 与 `tests/python/` 的研究、行情和服务器改动。
- `src/Caishenfolio.Desktop/`、`src/Caishenfolio.Host/` 与对应 `tests/`。
- `scripts/ui_smoke.ps1`、`scripts/ui_smoke_negative.ps1`。
- `Caishenfolio.slnx`、`README.md`、`python/pyproject.toml`。

## 不要纳入 Git

- `.local/`：冒烟报告、日志、隔离状态。报告已在交付说明中给出本地路径。
- `%TEMP%\caishenfolio-ui-smoke\`：每次冒烟的隔离根，脚本结束时只删除自己的目录。
- 根目录 `%s`：0 字节，2026-09-08 14:53 创建。未找到引用，未删除，待确认是否为生成残留。
- 用户 LocalAppData 下的账本、凭据、缓存、venv。冒烟通过目录联接复用 venv，不复制凭据。

## 发布候选

未构建新的 Release MSI，也没有签名、上传或安装。原因是本批没有发布授权，且 DPI、键鼠、真人验收仍开放。下一次发布前先用 `scripts/version.ps1` 升版本，再按现有 WiX 流程构建。

## 风险

构建中间清单曾记录另一个旧目录 `D:\Agent\Project\Caishenfolio`。本次冒烟改为启动当前工作区 `src\Caishenfolio.Desktop\bin\Debug\net8.0-windows\Caishenfolio.Desktop.exe`，并用 `/health` 的 pid 证明实际进程。提交前应再执行一次干净构建，确认没有机器级输出路径覆盖。
