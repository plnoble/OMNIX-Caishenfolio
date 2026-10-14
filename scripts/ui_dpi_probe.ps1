# One-shot display probe. Launches the isolated desktop, records DPI, window size,
# cost-box bounds, mouse hit-testing, and one Tab step. Cleans up only its own process.
[CmdletBinding()]
param([string]$ReportPath = ".local\acceptance\dpi-100-probe.json")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$exe = Join-Path $workspace "src\Caishenfolio.Desktop\bin\Debug\net8.0-windows\Caishenfolio.Desktop.exe"
$root = Join-Path $env:TEMP ("caishenfolio-dpi-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path (Join-Path $root "state") -Force | Out-Null
$managed = Join-Path $env:LOCALAPPDATA "Caishenfolio\state\.venv"
New-Item -ItemType Junction -Path (Join-Path $root "state\.venv") -Target $managed | Out-Null
$env:CAISHENFOLIO_TEST_STATE_ROOT = $root
$env:CAISHENFOLIO_TEST_FORCE_PROVIDER = "fixture"
$env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
$proc = Start-Process -FilePath $exe -WorkingDirectory $workspace -PassThru -WindowStyle Normal
$notes = @()
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PointerProbe {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
  }
  public static void Tab() { keybd_event(0x09, 0, 0, UIntPtr.Zero); keybd_event(0x09, 0, 2, UIntPtr.Zero); }
}
"@
    $automation = [System.Windows.Automation.AutomationElement]
    $deadline = (Get-Date).AddSeconds(40)
    $main = $null
    while ((Get-Date) -lt $deadline -and -not $main) {
        Start-Sleep -Milliseconds 500
        $main = $automation::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $proc.Id)))
    }
    if (-not $main) { throw "窗口未出现" }
    $skip = $null
    $skipDeadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $skipDeadline -and -not $skip) {
        Start-Sleep -Milliseconds 300
        $allButtons = @($automation::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
        $skip = $allButtons | Where-Object { $_.Current.ProcessId -eq $proc.Id -and $_.Current.Name -like "*跳过*" } | Select-Object -First 1
    }
    if ($skip) {
        $skip.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700
        $notes += "onboarding skipped"
    }
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
    $dpi = [PointerProbe]::GetDpiForWindow($hwnd)
    if ($dpi -lt 96) { $dpi = 96 }
    $scale = $dpi / 96.0
    [void][PointerProbe]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, [int](1020 * $scale), [int](680 * $scale), 0x0014)
    Start-Sleep -Milliseconds 500
    $notes += "dpi=$dpi scale=$scale"
    $main.SetFocus()
    Start-Sleep -Milliseconds 500
    $buttons = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
    $fx = $buttons | Where-Object { $_.Current.Name -like "*汇率*" } | Select-Object -First 1
    if (-not $fx) { throw "找不到汇率页" }
    $fx.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    $edits = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))))
    $boxes = @($edits | Where-Object { $_.Current.AutomationId -match "^Fx(Held|New|Detour)(CostPct|Fee)Box$" })
    $boxReport = @()
    foreach ($box in $boxes) {
        $r = $box.Current.BoundingRectangle
        $boxReport += [pscustomobject]@{ id = $box.Current.AutomationId; x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height; offscreen = [bool]$box.Current.IsOffscreen }
    }
    $hits = @()
    foreach ($box in $boxes) {
        # UIA BoundingRectangle and SetCursorPos both speak PHYSICAL pixels for a
        # DPI-aware caller — no scale division (it was wrong at 200%, where every
        # click landed half-way up the page on the grid instead of the box).
        $rect = $box.Current.BoundingRectangle
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [PointerProbe]::Click($x, $y)
    Start-Sleep -Milliseconds 400
    $focused = [string]$automation::FocusedElement.Current.AutomationId
    $clicked = $automation::FromPoint((New-Object System.Windows.Point($x, $y)))
    $hitId = if ($clicked) { [string]$clicked.Current.AutomationId } else { "" }
    $hitName = if ($clicked) { [string]$clicked.Current.Name } else { "" }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $parent = if ($clicked) { $walker.GetParent($clicked) } else { $null }
    $parentId = if ($parent) { [string]$parent.Current.AutomationId } else { "" }
    $notes += "hit_parent=$parentId"
    [PointerProbe]::Tab()
    Start-Sleep -Milliseconds 300
    $afterTab = [string]$automation::FocusedElement.Current.AutomationId
    $hits += [pscustomobject]@{ target = $box.Current.AutomationId; x = $x; y = $y; focused = $focused; hit_id = $hitId }
    }
    $before = [string]$automation::FocusedElement.Current.AutomationId
    [PointerProbe]::Tab()
    Start-Sleep -Milliseconds 180
    $tabOrder = @($before, [string]$automation::FocusedElement.Current.AutomationId)
    foreach ($step in 1..5) {
        [PointerProbe]::Tab()
        Start-Sleep -Milliseconds 180
        $tabOrder += [string]$automation::FocusedElement.Current.AutomationId
    }
    $window = $main.Current.BoundingRectangle
    $report = [pscustomobject]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        window = [pscustomobject]@{ x = [int]$window.X; y = [int]$window.Y; w = [int]$window.Width; h = [int]$window.Height }
        boxes = $boxReport
        clicks = $hits
        tab_order = $tabOrder
        notes = $notes
    }
    $full = Join-Path $workspace $ReportPath
    New-Item -ItemType Directory -Path (Split-Path $full) -Force | Out-Null
    $report | ConvertTo-Json -Depth 5 | Out-File -FilePath $full -Encoding utf8
    Write-Host "报告：$full"
}
finally {
    if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch {} }
    $children = @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" | Where-Object { $_.ParentProcessId -eq $proc.Id })
    foreach ($child in $children) { try { Stop-Process -Id $child.ProcessId -Force } catch {} }
    $link = Join-Path $root "state\.venv"
    if (Test-Path $link) { (Get-Item $link).Delete() }
    if ($root.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
