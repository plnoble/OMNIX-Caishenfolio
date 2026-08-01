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

**用户能看到新版本的完整链路**（缺任何一步都收不到提示）：

```
scripts\version.ps1 -Bump minor
git tag v0.11.0 → push
  → release.yml 构建 MSI，建【草稿】Release
  → 【你在 GitHub 上手动点发布】        ← 少了这步，客户端永远查不到
  → 桌面端下次启动时提示新版本
```

最后那步是必须的：GitHub 的 `/releases/latest` 接口**按设计排除草稿与预发布**。
草稿躺在那里对客户端等于不存在。

- **启动时静默检查**：应用起来后在后台查一次（排在账本与核心之后，GitHub 慢或不通都不会拖慢启动）。
- 只有**确实更新**的已发布版本才会在顶部出提示条，带「去下载 / 忽略此版本 / 关闭」。
  已是最新、本地版本更新、检查失败——一律无声，不弹窗。
- 「忽略此版本」记在 `%LocalAppData%\Caishenfolio\update.json`，**只忽略那一个版本**，
  更新的版本仍会提示。系统页手动点「检查更新」会**无视忽略**照常显示。
- 比较是**数值比较**（`0.9.0` < `0.10.0`），不是字符串比较。
- 提示条上的「立即更新」会下载 MSI、校验、询问后安装，然后关闭应用
  （MSI 无法替换正在运行的进程占用的文件）。「发布页」仍保留为手动路径。
- MSI 配了 `MajorUpgrade`，新版本直接覆盖安装即可原地升级。

## 发布签名

应用内更新在执行安装包之前会做两层验证：

| 层 | 防什么 |
|---|---|
| SHA-256 校验和 | 下载损坏、传输被改 |
| ECDSA P-256 签名 | **发布本身被替换** |

校验和单独用是弱的——它和 MSI 一起发布，能换掉其一的人也能换掉另一个。
签名不行：私钥只存在于 GitHub Actions secret，公钥编译进程序
（运行时去取公钥没有意义，能换安装包的人也能换公钥）。

任一层不通过就**删除已下载的文件**，不会把未经验证的安装包留在临时目录里。

### 生成密钥（一次性）

```powershell
dotnet run scripts\new_release_key.cs
```

私钥写入 `release-private-key.txt`（已 gitignore），**不打印到屏幕**；只有公钥会显示。
脚本会先用发布流程完全相同的 API 做一次签名/验签自检，不通过就不写文件。

```powershell
gh secret set OMNIX_RELEASE_PRIVATE_KEY < release-private-key.txt
Remove-Item release-private-key.txt        # secret 里那份才是长期存放处
```

再把公钥填进 `ReleaseSignature.PublicKeyBase64` 并发一版。

配置之前：`release.yml` 的签名步骤会**警告并跳过**，应用只做校验和验证
（`SignatureStatus.NotConfigured`）。
配置之后：没有签名或签名不符的安装包会被**直接拒绝**，不是降级成警告。

> 换密钥会让所有旧签名失效。密钥丢失只能重新生成并重发一版。

推送到 `main` **不会**产生任何客户端可见的更新——CI 只产出制品，不建 Release。

## 用户数据

账本、缓存、状态都在 `%LocalAppData%\Caishenfolio`，**不在**安装目录。
升级、修复、卸载都不会动它。要清空需要手动删除该目录。
