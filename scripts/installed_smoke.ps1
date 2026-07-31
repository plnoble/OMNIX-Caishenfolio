<#
.SYNOPSIS
    Runs the app from an installed-style layout outside the repository.

.DESCRIPTION
    `scripts\ui_smoke.ps1` launches the app from the repo, where the python\ tree is always a
    few directories up. An installed copy has no repository above it — and that difference once
    shipped an MSI that threw DirectoryNotFoundException from the window constructor before
    anything appeared on screen.

    This copies exactly what the installer ships (the published desktop output plus the staged
    Python core) into a temporary folder outside the repo and checks the app still starts.

.EXAMPLE
    scripts\installed_smoke.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$ReportPath = ".local\installed-smoke\installed-smoke-report.json",
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$steps = @()
$failure = $null
$appProcess = $null
$stageRoot = $null

function Add-Step([string]$name, [string]$status, [string]$detail = "") {
    $script:steps += [pscustomobject]@{ name = $name; status = $status; detail = $detail }
    $colour = switch ($status) { "passed" { "Green" } "failed" { "Red" } default { "Yellow" } }
    Write-Host ("  [{0,-7}] {1} {2}" -f $status, $name, $detail) -ForegroundColor $colour
}

$reportFull = Join-Path $workspace $ReportPath
$reportDir = Split-Path -Parent $reportFull
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
$stderrLog = Join-Path $reportDir "installed-stderr.log"

Write-Host "安装布局冒烟：在仓库之外以安装后的目录结构运行" -ForegroundColor Cyan

try {
    # Building the installer project also publishes the desktop and stages the Python core,
    # so the payload here is exactly what the MSI would carry.
    & dotnet build (Join-Path $workspace "packaging\windows\Omnix.Installer.wixproj") `
        -c $Configuration -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "安装包构建失败" }
    Add-Step "build installer payload" "passed"

    $publish = Join-Path $workspace "packaging\windows\obj\$Configuration\DesktopPublishOutput"
    $pythonStage = Join-Path $workspace "packaging\windows\obj\$Configuration\PythonStage"
    foreach ($required in @($publish, $pythonStage)) {
        if (-not (Test-Path $required)) { throw "缺少构建产物：$required" }
    }

    $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("omnix_installed_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Copy-Item (Join-Path $publish "*") $stageRoot -Recurse -Force
    Copy-Item $pythonStage (Join-Path $stageRoot "python") -Recurse -Force
    Add-Step "stage outside repo" "passed" $stageRoot

    $core = Join-Path $stageRoot "python\caishenfolio_core\__init__.py"
    if (-not (Test-Path $core)) {
        throw "安装布局里没有 python\caishenfolio_core（模块被压平或未打包）。"
    }
    Add-Step "analytics core present" "passed"

    $env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
    $env:CAISHENFOLIO_SKIP_SYMBOL_INDEX_NETWORK = "1"
    if (Test-Path $stderrLog) { Remove-Item $stderrLog -Force }

    $exe = Join-Path $stageRoot "Caishenfolio.Desktop.exe"
    $appProcess = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized `
        -RedirectStandardError $stderrLog `
        -RedirectStandardOutput (Join-Path $reportDir "installed-stdout.log")

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $title = $null
    while ((Get-Date) -lt $deadline) {
        if ($appProcess.HasExited) { break }
        $appProcess.Refresh()
        if (-not [string]::IsNullOrWhiteSpace($appProcess.MainWindowTitle)) {
            $title = $appProcess.MainWindowTitle
            break
        }
        Start-Sleep -Milliseconds 500
    }

    $stderrText = if (Test-Path $stderrLog) { (Get-Content $stderrLog -Raw) } else { "" }
    if ($null -eq $stderrText) { $stderrText = "" }
    if ($stderrText -match "Unhandled exception") {
        $first = ($stderrText -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
        throw "安装布局下启动抛出未处理异常：$first"
    }
    if (-not $title) { throw "安装布局下没有出现窗口（日志：$stderrLog）" }
    Add-Step "window appeared" "passed" $title
}
catch {
    $failure = $_.Exception.Message
    Add-Step "installed smoke" "failed" $failure
}
finally {
    if ($appProcess) {
        try { $appProcess.Refresh(); if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force } } catch { }
    }
    if ($stageRoot -and (Test-Path $stageRoot)) {
        Start-Sleep -Milliseconds 500
        try { Remove-Item $stageRoot -Recurse -Force } catch { }
    }

    ([pscustomobject]@{
        tool      = "installed_smoke"
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        status    = if ($failure) { "failed" } else { "passed" }
        failure   = $failure
        steps     = $steps
    }) | ConvertTo-Json -Depth 5 | Out-File -FilePath $reportFull -Encoding utf8
    Write-Host ""
    Write-Host "报告：$reportFull"
}

if ($failure) {
    Write-Host "安装布局冒烟失败。" -ForegroundColor Red
    exit 1
}

Write-Host "安装布局冒烟通过。" -ForegroundColor Green
