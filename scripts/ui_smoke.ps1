<#
.SYNOPSIS
    Launches the desktop app and verifies it actually comes up.

.DESCRIPTION
    `dotnet build` and `dotnet test` cannot catch a XAML failure: BAML is parsed at runtime, so a
    bad style or binding builds clean and then throws on startup. This script closes that gap —
    run it after any change to XAML, resource dictionaries, or view wiring.

    It starts the app with the fixture market provider (no network), waits for a real window
    title, checks stderr for an unhandled exception, writes a JSON report, and shuts down.

.EXAMPLE
    scripts\ui_smoke.ps1

.EXAMPLE
    scripts\ui_smoke.ps1 -TimeoutSeconds 90 -KeepOpen
#>
[CmdletBinding()]
param(
    [string]$DesktopProject = "src\Caishenfolio.Desktop\Caishenfolio.Desktop.csproj",
    [string]$ReportPath = ".local\ui-smoke\ui-smoke-report.json",
    [int]$TimeoutSeconds = 90,
    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$steps = @()
$failure = $null
$hostProcess = $null
$appProcess = $null

function Resolve-InWorkspace([string]$path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($path)) {
        [System.IO.Path]::GetFullPath($path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $workspace $path))
    }
    # Reports and logs must never escape the repo.
    if (-not $resolved.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于仓库内：$resolved"
    }
    return $resolved
}

function Add-Step([string]$name, [string]$status, [string]$detail = "") {
    $script:steps += [pscustomobject]@{
        name   = $name
        status = $status
        detail = $detail
    }
    $colour = switch ($status) { "passed" { "Green" } "failed" { "Red" } default { "Yellow" } }
    Write-Host ("  [{0,-7}] {1} {2}" -f $status, $name, $detail) -ForegroundColor $colour
}

$reportFull = Resolve-InWorkspace $ReportPath
$reportDir = Split-Path -Parent $reportFull
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
$stdoutLog = Join-Path $reportDir "desktop-stdout.log"
$stderrLog = Join-Path $reportDir "desktop-stderr.log"

Write-Host "UI 冒烟：启动桌面应用并确认窗口出现" -ForegroundColor Cyan

try {
    Add-Step "resolve project" "passed" (Resolve-InWorkspace $DesktopProject)

    # Fixture provider keeps the smoke test offline and deterministic.
    $env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
    $env:CAISHENFOLIO_SKIP_SYMBOL_INDEX_NETWORK = "1"

    foreach ($stale in @($stdoutLog, $stderrLog)) {
        if (Test-Path $stale) { Remove-Item $stale -Force }
    }

    $hostProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$DesktopProject`"" `
        -WorkingDirectory $workspace `
        -PassThru -WindowStyle Minimized `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog
    Add-Step "launch" "passed" "host pid=$($hostProcess.Id)"

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $windowTitle = $null
    while ((Get-Date) -lt $deadline) {
        # `dotnet run` is the host; the WPF window belongs to the child process.
        $candidate = Get-Process -Name "Caishenfolio.Desktop" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) {
            $candidate.Refresh()
            if (-not [string]::IsNullOrWhiteSpace($candidate.MainWindowTitle)) {
                $appProcess = $candidate
                $windowTitle = $candidate.MainWindowTitle
                break
            }
        }

        if ($hostProcess.HasExited -and -not $candidate) {
            break
        }

        Start-Sleep -Milliseconds 500
    }

    $stderrText = if (Test-Path $stderrLog) { (Get-Content $stderrLog -Raw) } else { "" }
    if ($null -eq $stderrText) { $stderrText = "" }

    if ($stderrText -match "Unhandled exception|XamlParseException") {
        $firstLine = ($stderrText -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
        throw "应用启动时抛出未处理异常：$firstLine （完整日志：$stderrLog）"
    }

    if (-not $windowTitle) {
        throw "在 $TimeoutSeconds 秒内没有出现窗口。日志：$stderrLog"
    }
    Add-Step "window appeared" "passed" $windowTitle

    if ($windowTitle -notmatch "OMNIX-Caishenfolio") {
        throw "窗口标题不符合预期：$windowTitle"
    }
    Add-Step "window title" "passed" $windowTitle

    $version = (Get-Content (Join-Path $workspace "VERSION") -Raw).Trim()
    if ($windowTitle -notmatch [regex]::Escape($version)) {
        throw "窗口标题里的版本与 VERSION ($version) 不一致：$windowTitle"
    }
    Add-Step "version in title" "passed" "v$version"

    # Give the ledger refresh and core bootstrap a moment, then re-check for a late crash.
    Start-Sleep -Seconds 5
    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "窗口出现后应用退出。日志：$stderrLog"
    }
    Add-Step "still running" "passed" "pid=$($appProcess.Id)"
}
catch {
    $failure = $_.Exception.Message
    Add-Step "smoke" "failed" $failure
}
finally {
    if (-not $KeepOpen) {
        foreach ($p in @($appProcess, $hostProcess)) {
            if ($p) {
                try { $p.Refresh(); if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
            }
        }
        # `dotnet run` can leave the child behind when the host is killed first.
        Get-Process -Name "Caishenfolio.Desktop" -ErrorAction SilentlyContinue |
            ForEach-Object { try { Stop-Process -Id $_.Id -Force } catch { } }
    }

    $report = [pscustomobject]@{
        tool       = "ui_smoke"
        timestamp  = (Get-Date).ToUniversalTime().ToString("o")
        status     = if ($failure) { "failed" } else { "passed" }
        failure    = $failure
        steps      = $steps
        stdout_log = $stdoutLog
        stderr_log = $stderrLog
    }
    $report | ConvertTo-Json -Depth 5 | Out-File -FilePath $reportFull -Encoding utf8
    Write-Host ""
    Write-Host "报告：$reportFull"
}

if ($failure) {
    Write-Host "UI 冒烟失败。" -ForegroundColor Red
    exit 1
}

Write-Host "UI 冒烟通过。" -ForegroundColor Green
