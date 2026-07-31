# 版本与发布

## 版本号只有一个来源

根目录的 `VERSION` 与 `PHASE` 是**唯一**写版本号和阶段的地方，都是单行纯文本，
好让 MSBuild、C#、Python、PowerShell 都能读同一个值。

| 位置 | 怎么拿到版本 |
|---|---|
| `Directory.Build.props` | 直接读 `VERSION` / `PHASE`，注入 `Version` / `AssemblyVersion` / 程序集属性 |
| `ProductInfo.Version` / `.Phase` | 从程序集属性读，**不硬编码** |
| `packaging\windows\Omnix.Installer.wixproj` | `ProductVersion=$(OmnixVersion)` |
| `python\caishenfolio_core\__init__.py` | 由 `scripts\version.ps1` 写入（Python 读不到 MSBuild） |
| `python\pyproject.toml` | 同上 |

`ProductVersionTests` 会断言这四处一致；任何漂移都会让 `dotnet test` 变红。

> 历史教训：重构前版本号散落四处，实际值分别是 `0.2.0` / `0.10.0` / `0.10.0` / `0.4.0`；
> 阶段号也在三处硬编码并漂移过，导致 6 个测试长期红灯。

## 提升版本

```powershell
scripts\version.ps1                      # 查看当前版本与所有写入点
scripts\version.ps1 -Bump patch          # 0.10.0 -> 0.10.1
scripts\version.ps1 -Bump minor          # 0.10.0 -> 0.11.0
scripts\version.ps1 -SetVersion 1.0.0 -SetPhase GA
```

脚本会同步 `VERSION`、`PHASE`、Python 常量与 `pyproject.toml`，并提示下一步命令。

## 本地构建安装包

```powershell
dotnet build packaging\windows\Omnix.Installer.wixproj -c Release
```

产物：`packaging\windows\bin\x64\Release\OMNIX-Caishenfolio.msi`

安装包只装 WPF 桌面二进制。.NET 8 桌面运行时与 Python 分析核心不打进 MSI：
前者由系统提供，后者首次运行时按需准备。

## 发布流程

```powershell
scripts\version.ps1 -Bump minor
dotnet build Caishenfolio.slnx; if ($?) { dotnet test Caishenfolio.slnx }
git commit -am "Release v0.11.0"
git tag v0.11.0
git push origin main --tags
```

推 tag 会触发 `.github/workflows/release.yml`：

1. **先校验 tag 与 `VERSION` 一致**，不一致直接失败（避免发出版本号对不上的包）
2. 跑 C# 与 Python 全部测试
3. 构建 MSI 并计算 SHA256
4. 建一个**草稿** Release 并附上 MSI 与校验和 —— 你确认后再手动发布

日常 push 到 `main` 走 `.github/workflows/ci.yml`：构建 + 双侧测试 + 产出 MSI 制品。
Python 测试在 CI 里强制走 `fixture` 数据源，CI 不依赖真实行情源。

## 桌面端更新

- 系统页 →「检查更新」向 GitHub Releases 查询最新版本并与当前版本比较。
- 比较是**数值比较**（`0.9.0` < `0.10.0`），不是字符串比较。
- 网络失败只提示「检查失败，不影响使用」，不阻塞任何功能。
- 应用**不会**自动下载或安装任何东西；点「打开发布页」在浏览器里手动下载。
- MSI 配了 `MajorUpgrade`，新版本直接覆盖安装即可原地升级。

## 用户数据

账本、缓存、状态都在 `%LocalAppData%\Caishenfolio`，**不在**安装目录。
升级、修复、卸载都不会动它。要清空需要手动删除该目录。
