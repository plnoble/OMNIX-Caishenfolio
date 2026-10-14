<#
.SYNOPSIS
    Negative checks for the UI smoke harness. These must fail closed.

.DESCRIPTION
    Does not start the desktop app and does not touch the user's ledger.
    Proves three guards that a green smoke run cannot prove by itself:
    cleanup refuses a path outside the smoke parent, a critical skipped step
    cannot be reported as an overall pass, and a failure string is the real
    error text rather than an AutomationElement property path.
#>
[CmdletBinding()]
param(
    [string]$ReportPath = ".local\acceptance\ui-smoke-negative-report.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$failures = @()

function Add-Failure([string]$name, [string]$detail) {
    $script:failures += [pscustomobject]@{ name = $name; detail = $detail }
    Write-Host "  [failed ] $name $detail" -ForegroundColor Red
}

function Assert-Throws([string]$name, [scriptblock]$action, [string]$expected) {
    $threw = $false
    $message = ""
    try { & $action }
    catch {
        $threw = $true
        $message = $_.Exception.Message
    }
    if (-not $threw) {
        Add-Failure $name "应当失败，但执行成功了。"
        return
    }
    if ($message -notlike "*$expected*") {
        Add-Failure $name "失败了，但原因不含「$expected」。实际：$message"
        return
    }
    Write-Host "  [passed ] $name" -ForegroundColor Green
}

# Load the production functions without executing the script's top-level launch.
$smokeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "ui_smoke.ps1") -Raw
$loaded = foreach ($name in @('Test-CriticalSmokeSkipped', 'Remove-OwnedSmokeDirectory', 'Get-FailureText',
    'Resolve-InWorkspace', 'New-SmokeAttempt', 'Wait-OwnedOnboarding')) {
    $match = [regex]::Match($smokeSource, "(?s)function $name\b.*?`r?`n\}")
    if (-not $match.Success) { throw "ui_smoke.ps1 中找不到生产函数 $name。" }
    $match.Value
}
Invoke-Expression ($loaded -join "`r`n")

$parent = Join-Path $workspace ".local\ui-smoke"
Assert-Throws "cleanup outside parent" {
    Remove-OwnedSmokeDirectory (Join-Path $env:TEMP "not-owned") $parent
} "拒绝清理"

# --- a skipped critical step cannot make the report pass ---
$steps = @(
    [pscustomobject]@{ name = "navigate pages"; status = "skipped"; detail = "UIAutomation 不可用" },
    [pscustomobject]@{ name = "window title"; status = "passed"; detail = "ok" }
)
$failure = $null
$critical = @(Test-CriticalSmokeSkipped $steps)
if ($critical.Count -gt 0) {
    $failure = "关键步骤被跳过，不能计为验收通过：navigate pages"
}
if ($failure) {
    Write-Host "  [passed ] skipped critical step fails the run" -ForegroundColor Green
}
else {
    Add-Failure "skipped critical step" "跳过的关键步骤仍被当成通过。"
}

# --- failure text is the element's name, not the property path ---
# Load only Get-FailureText from the production script. Dot-sourcing the whole
# file would launch the desktop app.
$smokeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "ui_smoke.ps1") -Raw
$helperMatch = [regex]::Match($smokeSource, '(?s)function Get-FailureText\(\$element\) \{.*?\r?\n\}')
if (-not $helperMatch.Success) {
    Add-Failure "failure text" "ui_smoke.ps1 中找不到 Get-FailureText。"
}
else {
    Invoke-Expression $helperMatch.Value
    $knownError = "路径比较失败：Response status code does not indicate success: 422 (Unprocessable Entity)."
    $standIn = [pscustomobject]@{ Current = [pscustomobject]@{ Name = $knownError } }
    $captured = Get-FailureText $standIn
    if ($captured -ne $knownError -or $captured -like "*AutomationElement.Current.Name*") {
        Add-Failure "failure text" "没有保留真实错误正文：$captured"
    }
    else {
        Write-Host "  [passed ] failure text keeps the real body" -ForegroundColor Green
    }
}

# FA1-R02: controlled snapshots drive the production wait / ownership / close logic.
# These are pure harness tests: no UIA, no desktop process, no real ledger.
function Assert-Check([string]$name, [scriptblock]$action) {
    try { & $action; Write-Host "  [passed ] $name" -ForegroundColor Green }
    catch { Add-Failure $name $_.Exception.Message }
}
$ownGuide = [pscustomobject]@{ Current=[pscustomobject]@{ ProcessId=123; Name='欢迎使用 Caishenfolio' } }
$otherGuide = [pscustomobject]@{ Current=[pscustomobject]@{ ProcessId=456; Name='欢迎使用 Caishenfolio' } }
Assert-Check 'delayed owned guide closes; foreign guide untouched' {
    $state = @{ polls=0; dismissed=$false; chosen=0 }
    Wait-OwnedOnboarding -ProcessId 123 -TimeoutMilliseconds 1000 -PollMilliseconds 0 -Observe {
        $state.polls++
        $otherGuide
        if ($state.polls -ge 4 -and -not $state.dismissed) { $ownGuide }
    } -Dismiss { param($guide) $state.chosen=$guide.Current.ProcessId; $state.dismissed=$true }
    if ($state.polls -lt 5 -or $state.chosen -ne 123) { throw 'Did not wait for / dismiss only the owned guide.' }
}
Assert-Throws 'foreign-only guide cannot pass onboarding' {
    Wait-OwnedOnboarding -ProcessId 123 -TimeoutMilliseconds 10 -PollMilliseconds 0 -Observe { $otherGuide } -Dismiss { throw 'Must not touch foreign guide' }
} '未在期限内'
Assert-Throws 'missing guide fails instead of claiming non-first-run' {
    Wait-OwnedOnboarding -ProcessId 123 -TimeoutMilliseconds 10 -PollMilliseconds 0 -Observe { @() } -Dismiss { throw 'Must not dismiss' }
} '未在期限内'
Assert-Throws 'guide still open after skip fails' {
    Wait-OwnedOnboarding -ProcessId 123 -TimeoutMilliseconds 10 -PollMilliseconds 0 -Observe { $ownGuide } -Dismiss { }
} '仍未关闭'
Assert-Throws 'ambiguous owned guides fail closed' {
    Wait-OwnedOnboarding -ProcessId 123 -Observe { $ownGuide; $ownGuide } -Dismiss { throw 'Must not guess' }
} '多个首次引导'
Assert-Check 'skipped onboarding is a critical failure' {
    $skipped = @(Test-CriticalSmokeSkipped @([pscustomobject]@{ name='onboarding guide'; status='skipped' }))
    if ($skipped.Count -ne 1) { throw 'Onboarding skip must fail the run.' }
}

# FA1-R03: unique attempts and immutable requested report names; preserve this evidence.
$guardRoot = Join-Path $workspace ('.local/smoke-guard-tests/' + [guid]::NewGuid().ToString('N'))
Assert-Check 'attempts have distinct report and log directories' {
    $first = New-SmokeAttempt (Join-Path $guardRoot 'one.json')
    $second = New-SmokeAttempt (Join-Path $guardRoot 'two.json')
    if ($first.Id -eq $second.Id -or $first.Directory -eq $second.Directory) { throw 'Attempts share evidence paths.' }
    # This is test output created by this test, not a user file.
    'original failure evidence' | Out-File -LiteralPath $first.Report -Encoding utf8
}
Assert-Throws 'existing report cannot be overwritten' {
    New-SmokeAttempt (Join-Path $guardRoot 'one.json')
} 'one.json'
Assert-Check 'failed retry preserved the previous report' {
    if ((Get-Content -LiteralPath (Join-Path $guardRoot 'one.json') -Raw).Trim() -ne 'original failure evidence') {
        throw 'Previous evidence was changed.'
    }
}
Assert-Throws 'similarly named sibling workspace is rejected' {
    New-SmokeAttempt ($workspace + '-sibling/report.json')
} '路径必须位于仓库内'

$reportFull = if ([System.IO.Path]::IsPathRooted($ReportPath)) { $ReportPath } else { Join-Path $workspace $ReportPath }
$reportDir = Split-Path -Parent $reportFull
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
[pscustomobject]@{
    tool = "ui_smoke_negative"
    timestamp = (Get-Date).ToUniversalTime().ToString("o")
    status = if ($failures.Count -gt 0) { "failed" } else { "passed" }
    checks = 13
    failures = $failures
} | ConvertTo-Json -Depth 4 | Out-File -FilePath $reportFull -Encoding utf8
Write-Host "报告：$reportFull"
if ($failures.Count -gt 0) { exit 1 }
