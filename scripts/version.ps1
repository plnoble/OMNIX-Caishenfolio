<#
.SYNOPSIS
    Shows or bumps the single source of truth for the product version and phase.

.DESCRIPTION
    The root VERSION and PHASE files are the only place these values are written. This script
    keeps every consumer in step: MSBuild reads the files directly, and the Python constants
    (which cannot read MSBuild) are rewritten here. `dotnet test` fails if anything drifts.

.EXAMPLE
    scripts\version.ps1
    Shows the current version, phase, and every place they appear.

.EXAMPLE
    scripts\version.ps1 -Bump minor
    0.10.0 -> 0.11.0

.EXAMPLE
    scripts\version.ps1 -SetVersion 1.0.0 -SetPhase GA
#>
[CmdletBinding(DefaultParameterSetName = "Show")]
param(
    [Parameter(ParameterSetName = "Bump")]
    [ValidateSet("major", "minor", "patch")]
    [string]$Bump,

    [Parameter(ParameterSetName = "Set")]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$SetVersion,

    [Parameter(ParameterSetName = "Bump")]
    [Parameter(ParameterSetName = "Set")]
    [Parameter(ParameterSetName = "Show")]
    [string]$SetPhase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot "VERSION"
$phaseFile = Join-Path $repoRoot "PHASE"
$pythonInit = Join-Path $repoRoot "python\caishenfolio_core\__init__.py"
$pyproject = Join-Path $repoRoot "python\pyproject.toml"

function Read-Trimmed([string]$path) {
    if (-not (Test-Path $path)) { throw "缺少文件：$path" }
    return (Get-Content -Path $path -Raw).Trim()
}

$currentVersion = Read-Trimmed $versionFile
$currentPhase = Read-Trimmed $phaseFile

$newVersion = $currentVersion
if ($PSCmdlet.ParameterSetName -eq "Bump") {
    $parts = $currentVersion.Split(".")
    $major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
    switch ($Bump) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    $newVersion = "$major.$minor.$patch"
}
elseif ($PSCmdlet.ParameterSetName -eq "Set") {
    $newVersion = $SetVersion
}

$newPhase = if ([string]::IsNullOrWhiteSpace($SetPhase)) { $currentPhase } else { $SetPhase.Trim() }

if ($newVersion -eq $currentVersion -and $newPhase -eq $currentPhase) {
    Write-Host "当前版本：$currentVersion   阶段：$currentPhase" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "写入位置（唯一来源）："
    Write-Host "  VERSION                                  $currentVersion"
    Write-Host "  PHASE                                    $currentPhase"
    Write-Host ""
    Write-Host "自动派生（不要手改）："
    Write-Host "  Directory.Build.props                    读取 VERSION / PHASE"
    Write-Host "  ProductInfo.Version / .Phase             读取程序集属性"
    Write-Host "  packaging\windows\*.wixproj              ProductVersion=`$(OmnixVersion)"
    Write-Host ""
    Write-Host "脚本同步（Python 读不到 MSBuild）："
    Write-Host "  python\caishenfolio_core\__init__.py      __version__ / PRODUCT_PHASE"
    Write-Host "  python\pyproject.toml                    version"
    Write-Host ""
    Write-Host "用法： scripts\version.ps1 -Bump patch|minor|major   或   -SetVersion 1.0.0 [-SetPhase GA]"
    return
}

# Windows PowerShell 5.1 writes a BOM with -Encoding utf8, which would leak into VERSION and
# into the .py source. Write these without one.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-NoBom([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

Write-NoBom $versionFile "$newVersion`n"
Write-NoBom $phaseFile "$newPhase`n"

# Python cannot read MSBuild properties, so its constants are rewritten rather than derived.
$initText = Get-Content -Path $pythonInit -Raw -Encoding UTF8
$initText = $initText -replace '__version__ = "[^"]*"', "__version__ = `"$newVersion`""
$initText = $initText -replace 'PRODUCT_PHASE = "[^"]*"', "PRODUCT_PHASE = `"$newPhase`""
Write-NoBom $pythonInit $initText

$projectText = Get-Content -Path $pyproject -Raw -Encoding UTF8
$projectText = $projectText -replace '(?m)^version = "[^"]*"$', "version = `"$newVersion`""
Write-NoBom $pyproject $projectText

Write-Host "版本：$currentVersion -> $newVersion" -ForegroundColor Green
Write-Host "阶段：$currentPhase -> $newPhase" -ForegroundColor Green
Write-Host ""
Write-Host "下一步：" -ForegroundColor Yellow
Write-Host "  dotnet test Caishenfolio.slnx        # 漂移守护测试会验证四处是否一致"
Write-Host "  git commit -am `"Release v$newVersion`""
Write-Host "  git tag v$newVersion; git push origin main --tags   # 触发 Release 工作流"
