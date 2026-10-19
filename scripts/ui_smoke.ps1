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
    [switch]$KeepOpen,
    [switch]$StartupOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$steps = @()
$failure = $null
$hostProcess = $null
$appProcess = $null
$focusNote = ""
$smokeStateRoot = $null
$smokeRootParent = $null

function Resolve-InWorkspace([string]$path) {
    $resolved = if ([System.IO.Path]::IsPathRooted($path)) {
        [System.IO.Path]::GetFullPath($path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $workspace $path))
    }
    # Reports and logs must never escape the repo.
    if (-not $resolved.StartsWith($workspace.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于仓库内：$resolved"
    }
    return $resolved
}

function Test-CriticalSmokeSkipped($steps) {
    return @($steps | Where-Object {
        $_.name -in @(
            "navigate pages", "fx cost input layout", "fx cost input focus",
            "fx form to net e2e", "fx range switch e2e", "ledger type switch e2e",
            "core health reachable", "seed isolated ledger", "onboarding guide"
        ) -and $_.status -eq "skipped"
    })
}

function Remove-OwnedSmokeDirectory([string]$target, [string]$parent) {
    if ([string]::IsNullOrWhiteSpace($target) -or [string]::IsNullOrWhiteSpace($parent)) { return }
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    $fullParent = [System.IO.Path]::GetFullPath($parent)
    $prefix = $fullParent.TrimEnd('\') + '\'
    if (-not $fullTarget.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理不属于本次冒烟目录的路径：$fullTarget"
    }
    if (-not (Test-Path -LiteralPath $fullTarget)) { return }
    $item = Get-Item -LiteralPath $fullTarget -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "拒绝清理重解析点：$fullTarget"
    }
    # Drop junctions first. Otherwise a venv link inside the smoke root would be followed.
    Get-ChildItem -LiteralPath $fullTarget -Force -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
        ForEach-Object { $_.Delete() }
    Remove-Item -LiteralPath $fullTarget -Recurse -Force
}

function Get-FailureText($element) {
    # Assign first, then read the name. Interpolating AutomationElement.Current.Name
    # directly emits the property path instead of the error body (R5-03).
    if ($null -eq $element) { return "(未找到失败文本)" }
    $text = $element.Current.Name
    if ([string]::IsNullOrWhiteSpace($text)) { return "(失败文本为空)" }
    return [string]$text
}

function New-SmokeAttempt([string]$RequestedReport) {
    $full = Resolve-InWorkspace $RequestedReport
    $parent = Split-Path -Parent $full
    # Never follow a report directory junction out of the workspace.
    $cursor = $parent
    while ($cursor -and $cursor -ne $workspace) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "拒绝写入重解析点证据目录：$cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
    [void][IO.Directory]::CreateDirectory($parent)
    # A previous report is archived under its own timestamp, never overwritten: retrying until
    # green must not be able to erase a failure. Refusing outright also preserved evidence, but
    # it meant the documented command `scripts\ui_smoke.ps1` stopped working after its first
    # run, so the mandatory gate quietly became unusable without a -ReportPath workaround.
    if ([IO.File]::Exists($full)) {
        $stamp = (Get-Item -LiteralPath $full -Force).LastWriteTimeUtc.ToString('yyyyMMddTHHmmssfffZ')
        $archived = Join-Path $parent ([IO.Path]::GetFileNameWithoutExtension($full) +
            '-' + $stamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) +
            [IO.Path]::GetExtension($full))
        [IO.File]::Move($full, $archived)
    }
    # Atomic reservation: two runs cannot claim the same report path at once.
    $reservation = [IO.File]::Open($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $reservation.Dispose()
    $id = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N')
    $directory = Join-Path $parent ('attempt-' + $id)
    [void][IO.Directory]::CreateDirectory($directory)
    return [pscustomobject]@{ Id = $id; Directory = $directory; Report = $full }
}

function Wait-OwnedOnboarding {
    param(
        [int]$ProcessId,
        [scriptblock]$Observe,
        [scriptblock]$Dismiss,
        [int]$TimeoutMilliseconds = 15000,
        [int]$PollMilliseconds = 100
    )
    # Both production UIA and negative tests pass snapshots through THIS ownership gate.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $guide = $null
    do {
        $matching = @(& $Observe | Where-Object {
            $_.Current.ProcessId -eq $ProcessId -and $_.Current.Name -eq '欢迎使用 Caishenfolio'
        })
        if ($matching.Count -gt 1) { throw "本次进程出现多个首次引导窗口，拒绝猜测。" }
        if ($matching.Count -eq 1) { $guide = $matching[0]; break }
        if ($PollMilliseconds -gt 0) { Start-Sleep -Milliseconds $PollMilliseconds }
    } while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    if ($null -eq $guide) { throw "首次隔离启动未在期限内出现本次进程的引导窗口（pid=$ProcessId）。" }

    & $Dismiss $guide
    $timer.Restart()
    do {
        $remaining = @(& $Observe | Where-Object {
            $_.Current.ProcessId -eq $ProcessId -and $_.Current.Name -eq '欢迎使用 Caishenfolio'
        })
        if ($remaining.Count -eq 0) { return }
        if ($PollMilliseconds -gt 0) { Start-Sleep -Milliseconds $PollMilliseconds }
    } while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    throw "跳过后本次进程的首次引导窗口仍未关闭（pid=$ProcessId）。"
}

function Add-Step([string]$name, [string]$status, [string]$detail = "") {
    $script:steps += [pscustomobject]@{
        name   = $name
        status = $status
        detail = $detail
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
    }
    $colour = switch ($status) { "passed" { "Green" } "failed" { "Red" } default { "Yellow" } }
    Write-Host ("  [{0,-7}] {1} {2}" -f $status, $name, $detail) -ForegroundColor $colour
}

function Invoke-NavigationWalk {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string[]]$Buttons
    )

    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "navigate pages" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        Add-Step "navigate pages" "skipped" "未找到主窗口自动化节点"
        return
    }

    # Nav buttons wrap an icon with their label, so the automation name is not the bare text.
    $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $all = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond))

    $visited = @()
    foreach ($name in $Buttons) {
        $button = $null
        foreach ($candidate in $all) {
            if ($candidate.Current.Name -like "*$name*") { $button = $candidate; break }
        }
        if (-not $button) {
            $seen = ($all | ForEach-Object { $_.Current.Name }) -join " | "
            throw "左栏找不到「$name」按钮。现有按钮：$seen"
        }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700
        $visited += $name
    }

    Add-Step "navigate pages" "passed" ($visited -join " / ")
}

function Invoke-DismissOnboarding {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # The first-run guide is a modal window whose BAML only parses when shown. If the app
    # opened with it, click through ("skip") and wait for the real main window — this both
    # verifies the guide's XAML loads and keeps later steps against a normal main window.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "onboarding guide" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $root = $automation::RootElement
    $byTitle = New-Object System.Windows.Automation.PropertyCondition(
        $automation::NameProperty, "欢迎使用 Caishenfolio")
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $ownedTitle = New-Object System.Windows.Automation.AndCondition($byPid, $byTitle)
    # A caption Text node can have the same name as its containing dialog. Match
    # actual windows, otherwise a single guide is misreported as two candidates.
    $byWindow = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $ownedWindow = New-Object System.Windows.Automation.AndCondition($ownedTitle, $byWindow)
    Wait-OwnedOnboarding -ProcessId $ProcessId -Observe {
        @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ownedWindow))
    } -Dismiss {
        param($guide)
        $skipName = New-Object System.Windows.Automation.PropertyCondition(
            $automation::NameProperty, "跳过，直接进入")
        $skip = $guide.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $skipName)
        if (-not $skip) {
            throw "首次引导窗口已打开，但找不到「跳过，直接进入」按钮。"
        }
        $skip.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Add-Step "onboarding guide" "passed" "本次 pid=$ProcessId 的引导已出现、跳过并确认关闭"
}

function Invoke-CostInputLayoutCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # recheck-2 F-01: the percentage and fixed-fee panels used to share one grid row and
    # overlapped. Programmatic proof: find the six cost text boxes on the FX page via
    # UIAutomation and assert no two of their on-screen rectangles intersect.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "fx cost input layout" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "布局检查：未找到主窗口自动化节点。"
    }

    # Navigate to the FX page first (the same walk the nav check uses).
    $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $all = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond))
    $fxButton = $all | Where-Object { $_.Current.Name -like "*汇率*" } | Select-Object -First 1
    if (-not $fxButton) {
        throw "布局检查：找不到「汇率」导航按钮。"
    }
    $fxButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900

    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edits = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond))
    $costNames = @("已持有→目标", "本位币直换", "绕行")
    $costBoxes = @()
    foreach ($edit in $edits) {
        # Cost boxes are the only edits whose neighbours carry the path labels.
        $name = $edit.Current.Name
        $automationId = $edit.Current.AutomationId
        if ($automationId -match "^Fx(Held|New|Detour)(CostPct|Fee)Box$") {
            $costBoxes += $edit
        }
    }

    if ($costBoxes.Count -lt 6) {
        throw "布局检查：预期 6 个成本输入框，找到 $($costBoxes.Count) 个。"
    }

    $rects = @()
    foreach ($box in $costBoxes) {
        $r = $box.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) {
            throw "布局检查：输入框 $($box.Current.AutomationId) 不可见（宽或高为 0）。"
        }
        $rects += ,@($r.X, $r.Y, $r.Right, $r.Bottom, $box.Current.AutomationId)
    }

    for ($i = 0; $i -lt $rects.Count; $i++) {
        for ($j = $i + 1; $j -lt $rects.Count; $j++) {
            $a = $rects[$i]; $b = $rects[$j]
            $overlapX = ($a[0] -lt $b[2]) -and ($b[0] -lt $a[2])
            $overlapY = ($a[1] -lt $b[3]) -and ($b[1] -lt $a[3])
            if ($overlapX -and $overlapY) {
                throw "布局检查：$($a[4]) 与 $($b[4]) 边界重叠。"
            }
        }
    }

    # Navigate back to the overview so later checks see the normal state.
    $overview = $all | Where-Object { $_.Current.Name -like "*总览*" } | Select-Object -First 1
    if ($overview) {
        $overview.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }

    Add-Step "fx cost input layout" "passed" "6 个成本输入框可见且互不重叠"
}

function Invoke-CostInputFocusCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # recheck-4 UI acceptance #1 (programmatic half): each cost box accepts keyboard focus,
    # is not offscreen, and TAB from it moves focus somewhere else (no focus trap).
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "fx cost input focus" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "焦点检查：未找到主窗口自动化节点。"
    }
    $main.SetFocus()
    Start-Sleep -Milliseconds 300

    $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $all = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond))
    $fxButton = $all | Where-Object { $_.Current.Name -like "*汇率*" } | Select-Object -First 1
    if (-not $fxButton) {
        throw "焦点检查：找不到「汇率」导航按钮。"
    }
    $fxButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    $main.SetFocus()
    Start-Sleep -Milliseconds 300

    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edits = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond))
    $costBoxes = @($edits | Where-Object { $_.Current.AutomationId -match "^Fx(Held|New|Detour)(CostPct|Fee)Box$" })
    if ($costBoxes.Count -lt 6) {
        throw "焦点检查：预期 6 个成本输入框，找到 $($costBoxes.Count) 个。"
    }

    foreach ($box in $costBoxes) {
        $id = $box.Current.AutomationId
        if ($box.Current.IsOffscreen) {
            throw "焦点检查：$id 位于可视区外（IsOffscreen）。"
        }

        try {
            $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("0")
        }
        catch {
            # A read-only or non-value edit fails the acceptance outright.
            throw "焦点检查：$id 无法通过 ValuePattern 写入（不可输入）。"
        }

        # Writable is proven by ValuePattern above. SetFocus is best-effort: UIA can
        # reject it on a writable box, and that is not a product failure. Mouse hit
        # testing and Tab order remain separate checks (R5-03).
        try { $box.SetFocus() } catch { $script:focusNote += "$id SetFocus 未成功; " }

        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("")
    }

    # Back to overview for the subsequent checks.
    $overview = $all | Where-Object { $_.Current.Name -like "*总览*" } | Select-Object -First 1
    if ($overview) {
        $overview.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }

    $focusDetail = "6 个成本输入框 UIA 可写、非屏外（鼠标命中与 Tab 遍历未测）"
    if ($script:focusNote) { $focusDetail += "；SetFocus 备注：" + $script:focusNote }
    Add-Step "fx cost input focus" "passed" $focusDetail
}

function Invoke-FxFormToNetEndToEnd {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # recheck-5 R5-02: per-path-card evidence. The held pot (JPY, from the isolated ledger
    # via the REAL prefill button) and the fresh-money path are asserted SEPARATELY — each
    # card's gross/net/status must belong to that same path, never a mix of two cards.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "fx form to net e2e" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "表单端到端：未找到主窗口自动化节点。"
    }

    function Get-Buttons {
        @($main.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
    }
    function Get-Edits {
        @($main.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))))
    }
    function Get-Edit([string]$automationId) {
        $found = Get-Edits | Where-Object { $_.Current.AutomationId -eq $automationId } | Select-Object -First 1
        if (-not $found) { throw "表单端到端：找不到输入框 $automationId。" }
        return $found
    }
    function Set-EditValue([string]$automationId, [string]$value) {
        $edit = Get-Edit $automationId
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    }

    # Navigate to the FX page.
    $fxButton = Get-Buttons | Where-Object { $_.Current.Name -like "*汇率*" } | Select-Object -First 1
    if (-not $fxButton) { throw "表单端到端：找不到「汇率」导航按钮。" }
    $fxButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900

    # Fee currency = USD via Expand + SelectItem (WPF combo needs the expand first).
    $comboCond = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $feeCombo = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)) |
        Where-Object { $_.Current.AutomationId -eq "FxFeeCurrencyCombo" } | Select-Object -First 1
    if (-not $feeCombo) { throw "表单端到端：找不到固定费币种下拉。" }
    $expand = $feeCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 300
    $usdItem = $feeCombo.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, "USD")))
    if ($usdItem) {
        $usdItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 200
    }
    $expand.Collapse()
    Start-Sleep -Milliseconds 200

    # REAL prefill: the held pot comes from the isolated ledger through the UI button, so
    # the JPY currency selection itself is exercised (recheck-5 R5-02 #1).
    $prefill = Get-Buttons | Where-Object { $_.Current.Name -like "*按账本填入*" } | Select-Object -First 1
    if (-not $prefill) { throw "表单端到端：找不到「按账本填入」按钮。" }
    $prefill.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800

    # Verify the prefill landed the isolated ledger's facts: 2100 JPY held, 100 CNY cost.
    $heldAmount = (Get-Edit "FxHeldAmountBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $heldCost = (Get-Edit "FxHeldCostBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ("$heldAmount" -ne "2100" -or "$heldCost" -ne "100") {
        throw "表单端到端：按账本填入结果不符（金额=$heldAmount 成本=$heldCost，预期 2100/100）。隔离账本预置或预填失败。"
    }

    # The held-currency combo must now carry JPY (populated by the prefill path).
    $heldCombo = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)) |
        Where-Object { $_.Current.AutomationId -eq "FxHeldCombo" } | Select-Object -First 1
    if (-not $heldCombo) { throw "表单端到端：找不到持有币种下拉。" }
    $heldSel = $heldCombo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $heldCcy = if ($heldSel.Count -gt 0) { $heldSel[0].Current.Name } else { "" }
    if ($heldCcy -ne "JPY") {
        throw "表单端到端：持有币种选择不是 JPY（实际 $heldCcy）。"
    }

    function Invoke-Compare {
        $btn = Get-Buttons | Where-Object { $_.Current.Name -like "*比较兑换路径*" } | Select-Object -First 1
        if (-not $btn) { throw "表单端到端：找不到「比较兑换路径」按钮。" }
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    # Collect the CURRENT render's text only after the new response lands (R5-02 #6: never
    # match a previous run's numbers). The status line resets to 已比较 N 条路径 on success;
    # we wait for that marker to flip, then harvest card texts.
    function Wait-ForCompareResult {
        param([string]$ScenarioLabel, [string]$PreviousNet = "")
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 900
            $texts = @($main.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))
            $joined = ($texts | ForEach-Object { $_.Current.Name }) -join "`n"
            if ($joined -match "路径比较失败") {
                $failEl = $texts | Where-Object { $_.Current.Name -like "*失败*" } | Select-Object -First 1
                $failText = Get-FailureText $failEl
                throw "表单端到端（$ScenarioLabel）：请求失败——$failText"
            }
            if ($joined -match "已比较 \d+ 条路径") {
                $currentNet = ""
                $netMatch = [regex]::Match($joined, "已持有 JPY 直接换[\s\S]{0,240}?净到账 ([0-9,\.]+) USD")
                if ($netMatch.Success) { $currentNet = $netMatch.Groups[1].Value }
                if ($currentNet -ne $PreviousNet) { return $joined }
            }
        }
        throw "表单端到端（$ScenarioLabel）：40 秒内未见「已比较 N 条路径」。"
    }

    function Get-CardText([string]$marker) {
        # A card's label and amounts are SEPARATE TextBlocks (separate UIA elements); match
        # the label element, walk UP to the card border, then join every text inside it —
        # per-card evidence, never a mix of two cards (recheck-5 R5-02).
        $texts = @($main.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))
        $labelEl = $texts | Where-Object { $_.Current.Name -like "*$marker*" } | Select-Object -First 1
        if (-not $labelEl) { return "" }

        # Walk up until an element containing BOTH the label and a sibling amounts text.
        $walker = $labelEl
        for ($i = 0; $i -lt 6 -and $walker; $i++) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($walker)
            if (-not $walker) { break }
            $cardTexts = @($walker.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))
            $joinedCard = ($cardTexts | ForEach-Object { $_.Current.Name }) -join "`n"
            if ($joinedCard -match "毛到账" -and $joinedCard -match "净到账") {
                return $joinedCard
            }
        }
        return $labelEl.Current.Name
    }

    # ---------- Scenario A: 0 pct + 0 fee on ALL paths → per-card net == gross ----------
    foreach ($id in @("FxHeldCostPctBox","FxNewCostPctBox","FxDetourCostPctBox",
                      "FxHeldFeeBox","FxNewFeeBox","FxDetourFeeBox")) {
        Set-EditValue $id "0"
    }
    Invoke-Compare
    $joinedA = Wait-ForCompareResult "0+0"
    $netA = ([regex]::Match($joinedA, "已持有 JPY 直接换[\s\S]{0,240}?净到账 ([0-9,\.]+) USD")).Groups[1].Value

    # Held-→USD card: contains the receipt facts (2 100 JPY, paid 100 CNY).
    $heldLineA = Get-CardText "已持有 JPY 直接换"
    if (-not $heldLineA) { throw "表单端到端（0+0）：未找到「已持有 JPY 直接换」卡片。" }
    $mA = [regex]::Match($heldLineA, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mA.Success) { throw "表单端到端（0+0）：持仓卡无毛/净到账文本（$heldLineA）。" }
    if ($mA.Groups[1].Value -ne $mA.Groups[2].Value) {
        throw "表单端到端（0+0）：持仓卡净额($($mA.Groups[2].Value))≠毛额($($mA.Groups[1].Value))。"
    }

    # Fresh CNY→USD card.
    $freshLineA = Get-CardText "新 CNY 直接换"
    if (-not $freshLineA) { throw "表单端到端（0+0）：未找到「新 CNY 直接换」卡片。" }
    $mFA = [regex]::Match($freshLineA, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mFA.Success) { throw "表单端到端（0+0）：新资金卡无毛/净到账文本（$freshLineA）。" }
    if ($mFA.Groups[1].Value -ne $mFA.Groups[2].Value) {
        throw "表单端到端（0+0）：新资金卡净额≠毛额。"
    }
    if ($joinedA -notmatch "净额已计入成本") {
        throw "表单端到端（0+0）：未见「净额已计入成本」状态。"
    }

    # ---------- Scenario B: 1 USD fixed fee on the two single paths; detour fees BLANK ----
    # (R5-02 #5: the detour's blank-cost branch must show the unknown-cost state, asserted.)
    Set-EditValue "FxHeldFeeBox" "1"
    Set-EditValue "FxNewFeeBox" "1"
    Set-EditValue "FxDetourFeeBox" ""
    Set-EditValue "FxDetourCostPctBox" ""
    Invoke-Compare
    $joinedB = Wait-ForCompareResult "0+1USD" $netA
    $netB = ([regex]::Match($joinedB, "已持有 JPY 直接换[\s\S]{0,240}?净到账 ([0-9,\.]+) USD")).Groups[1].Value

    $heldLineB = Get-CardText "已持有 JPY 直接换"
    $mB = [regex]::Match($heldLineB, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mB.Success) { throw "表单端到端（0+1USD）：持仓卡无毛/净到账文本（$heldLineB）。" }
    $gB = [double]::Parse($mB.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $nB = [double]::Parse($mB.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    if ([math]::Abs(($gB - $nB) - 1.0) -gt 0.01) {
        throw "表单端到端（0+1USD）：持仓卡净额≠毛额−1 USD（毛=$gB 净=$nB）。"
    }

    $freshLineB = Get-CardText "新 CNY 直接换"
    $mFB = [regex]::Match($freshLineB, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mFB.Success) { throw "表单端到端（0+1USD）：新资金卡无毛/净到账文本（$freshLineB）。" }
    $gFB = [double]::Parse($mFB.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $nFB = [double]::Parse($mFB.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    if ([math]::Abs(($gFB - $nFB) - 1.0) -gt 0.01) {
        throw "表单端到端（0+1USD）：新资金卡净额≠毛额−1 USD（毛=$gFB 净=$nFB）。"
    }

    # The detour card, with BOTH cost fields blank (unknown), must show the gross-only state.
    $detourLineB = Get-CardText "先换 JPY 再换"
    if (-not $detourLineB) { throw "表单端到端（0+1USD）：未找到绕行卡片。" }
    if ($detourLineB -notmatch "成本输入缺失") {
        throw "表单端到端（0+1USD）：绕行卡费用留空却未见「成本输入缺失」（$detourLineB）。"
    }

    # ---------- Scenario C: different fixed fees prove the boxes are not swapped ----------
    # Both cards charging 1 USD cannot show a field swap. 1 vs 2 can.
    # Expected gap is the fee itself (decimal identity), not a formula copied from fx_paths.
    # UI money is shown to 2 decimal places, so the tolerance is 0.01 display rounding.
    Set-EditValue "FxHeldFeeBox" "3"
    Set-EditValue "FxNewFeeBox" "2"
    Set-EditValue "FxDetourFeeBox" "0"
    Set-EditValue "FxDetourCostPctBox" "0"
    Invoke-Compare
    $joinedC = Wait-ForCompareResult "防串线 1/2 USD" $netB

    $heldLineC = Get-CardText "已持有 JPY 直接换"
    $mHC = [regex]::Match($heldLineC, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mHC.Success) { throw "表单端到端（防串线）：持仓卡无毛/净到账文本（$heldLineC）。" }
    $gHC = [decimal]::Parse($mHC.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $nHC = [decimal]::Parse($mHC.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $heldGap = ($gHC - $nHC) - 3.0
    if (($heldGap -gt 0.01) -or ($heldGap -lt -0.01)) {
        throw "表单端到端（防串线）：持仓卡差额不是 3 USD（毛=$gHC 净=$nHC）。固定费可能串到新资金卡。"
    }

    $freshLineC = Get-CardText "新 CNY 直接换"
    $mFC = [regex]::Match($freshLineC, "毛到账 ([0-9,\.]+) USD　→　净到账 ([0-9,\.]+) USD")
    if (-not $mFC.Success) { throw "表单端到端（防串线）：新资金卡无毛/净到账文本（$freshLineC）。" }
    $gFC = [decimal]::Parse($mFC.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $nFC = [decimal]::Parse($mFC.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    $freshGap = ($gFC - $nFC) - 2.0
    if (($freshGap -gt 0.01) -or ($freshGap -lt -0.01)) {
        throw "表单端到端（防串线）：新资金卡差额不是 2 USD（毛=$gFC 净=$nFC）。固定费可能串到持仓卡。"
    }

    # ---------- Scenario D: blank held cost must not look like a free net amount ----------
    Set-EditValue "FxHeldCostBox" ""
    Set-EditValue "FxHeldCostPctBox" ""
    Set-EditValue "FxHeldFeeBox" ""
    Invoke-Compare
    $blankJoined = Wait-ForCompareResult "持仓成本留空" "present"
    $heldBlank = Get-CardText "已持有 JPY 直接换"
    if ($heldBlank) {
        if ($heldBlank -match "净到账 (?!未知)") {
            throw "表单端到端（持仓成本留空）：持仓卡把未知成本显示成了净到账金额。"
        }
        if ($heldBlank -notmatch "成本输入缺失") {
            throw "表单端到端（持仓成本留空）：持仓卡未见成本输入缺失。"
        }
    }
    elseif ($blankJoined -notmatch "已比较 1 条路径") {
        throw "表单端到端（持仓成本留空）：持仓卡消失，但结果不是只剩 1 条其他路径。"
    }

    # Cleanup: blank the form for the next run.
    foreach ($id in @("FxHeldAmountBox","FxHeldCostBox","FxHeldCostPctBox","FxNewCostPctBox",
                      "FxDetourCostPctBox","FxHeldFeeBox","FxNewFeeBox","FxDetourFeeBox")) {
        Set-EditValue $id ""
    }

    $overview = Get-Buttons | Where-Object { $_.Current.Name -like "*总览*" } | Select-Object -First 1
    if ($overview) {
        $overview.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }

    Add-Step "fx form to net e2e" "passed" "逐卡：0+0 净=毛；0+1USD 净=毛−1；防串线差额 3 与 2；绕行留空与持仓成本留空已覆盖"
}

function Invoke-FxRangeSwitchCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # FA1: the carry page's real range path — switching 1/5/10 years issues a load per range,
    # the busy window disables refresh + range while it runs (observed when timing allows),
    # and EVERY range restores both controls afterwards (never a permanent disable).
    # The request-ownership semantics itself is pinned by unit tests; this step proves the
    # page-level wiring: switch → result lands → controls usable again.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "fx range switch e2e" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "区间检查：未找到主窗口自动化节点。"
    }

    function Find-ById([string]$automationId) {
        $main.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::AutomationIdProperty, $automationId)))
    }

    $buttons = @($main.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
    $fxButton = $buttons | Where-Object { $_.Current.Name -like "*汇率*" } | Select-Object -First 1
    if (-not $fxButton) { throw "区间检查：找不到「汇率」导航按钮。" }
    $fxButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900

    $combo = Find-ById "FxRangeCombo"
    if (-not $combo) { throw "区间检查：找不到区间下拉 FxRangeCombo。" }
    $loadButton = Find-ById "LoadButton"
    if (-not $loadButton) { throw "区间检查：找不到刷新按钮 LoadButton。" }

    $busyObservations = @()
    foreach ($rangeName in @("5 年", "10 年", "1 年")) {
        # The previous range's load may still be finishing: wait for the restore first,
        # otherwise Expand fires on a disabled combo and the popup never opens.
        $restoreDeadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $restoreDeadline -and
               (-not $loadButton.Current.IsEnabled -or -not $combo.Current.IsEnabled)) {
            Start-Sleep -Milliseconds 200
        }
        if (-not $loadButton.Current.IsEnabled -or -not $combo.Current.IsEnabled) {
            throw "区间检查（$rangeName）：前一次请求后 20 秒内刷新/区间仍未恢复可用。"
        }

        $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        $item = $null
        $itemDeadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $itemDeadline -and -not $item) {
            $item = $combo.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $rangeName)))
            if (-not $item) { Start-Sleep -Milliseconds 200 }
        }
        if (-not $item) {
            try { $expand.Collapse() } catch { }
            throw "区间检查：下拉里没有「$rangeName」选项。"
        }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 100
        try { $expand.Collapse() } catch { }

        # Busy window: selection fires the load, which must disable refresh + range up front.
        $pollUntil = (Get-Date).AddSeconds(2)
        $busySeen = $false
        while ((Get-Date) -lt $pollUntil) {
            if (-not $loadButton.Current.IsEnabled -or -not $combo.Current.IsEnabled) {
                $busySeen = $true
                break
            }
        }
        if ($busySeen) {
            if ($loadButton.Current.IsEnabled -or $combo.Current.IsEnabled) {
                throw "区间检查（$rangeName）：禁用状态不一致（刷新或区间其一仍可点）。"
            }
        }

        # The result must land for THIS range (fixture core keeps the request short).
        $years = ($rangeName -replace " 年", "")
        $deadline = (Get-Date).AddSeconds(45)
        $statusNow = ""
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $statusEl = Find-ById "StatusText"
            if ($statusEl) { $statusNow = [string]$statusEl.Current.Name }
            if ($statusNow -match "区间 $years 年") { break }
        }
        if ($statusNow -notmatch "区间 $years 年") {
            throw "区间检查（$rangeName）：45 秒内状态未出现「区间 $years 年」（实际：$statusNow）。"
        }

        # Restore: after the current request finishes both controls must be usable again.
        if (-not $loadButton.Current.IsEnabled) {
            throw "区间检查（$rangeName）：请求结束后刷新按钮仍被禁用。"
        }
        if (-not $combo.Current.IsEnabled) {
            throw "区间检查（$rangeName）：请求结束后区间下拉仍被禁用。"
        }
        if ($busySeen) { $busyObservations += "$rangeName=observed" } else { $busyObservations += "$rangeName=too-fast" }
    }

    $busyText = $busyObservations -join "；"
    Add-Step "fx range switch e2e" "passed" "5/10/1 年切换逐一得到「区间 N 年」结果并恢复刷新与区间（busy 窗口：$busyText；归属语义由正式单测钉死）"
}

function Invoke-LedgerTypeSwitchCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # FA1: the ledger entry form's real type path on the ISOLATED ledger — a deposit lands,
    # switching to Buy keeps the (now disabled) stale amount text, and the Buy must still
    # record: the R7-01 boundary as an actual page transaction, not only a form unit test.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "ledger type switch e2e" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "类型切换：未找到主窗口自动化节点。"
    }

    function Find-ById2([string]$automationId) {
        $main.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::AutomationIdProperty, $automationId)))
    }
    function Set-EditById([string]$automationId, [string]$value) {
        $edit = Find-ById2 $automationId
        if (-not $edit) { throw "类型切换：找不到输入框 $automationId。" }
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    }
    function Get-AllTexts {
        @($main.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))
    }
    function Wait-ForGridText([string]$marker) {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $joined = (Get-AllTexts | ForEach-Object { $_.Current.Name }) -join "`n"
            if ($joined -match [regex]::Escape($marker)) { return }
        }
        throw "类型切换：20 秒内流水表未出现「$marker」。"
    }
    function Select-Kind([string]$kindName) {
        $combo = Find-ById2 "EntryKind"
        if (-not $combo) { throw "类型切换：找不到类型下拉 EntryKind。" }
        $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        Start-Sleep -Milliseconds 300
        $item = $combo.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $kindName)))
        if (-not $item) {
            $expand.Collapse()
            throw "类型切换：类型下拉里没有「$kindName」。"
        }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 100
        try { $expand.Collapse() } catch { }
        Start-Sleep -Milliseconds 200
    }
    function Invoke-Record {
        $buttons = @($main.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
        $record = $buttons | Where-Object { $_.Current.Name -eq "记账" } | Select-Object -First 1
        if (-not $record) { throw "类型切换：找不到「记账」按钮。" }
        $record.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    $navButtons = @($main.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
    $ledgerButton = $navButtons | Where-Object { $_.Current.Name -like "*账本*" } | Select-Object -First 1
    if (-not $ledgerButton) { throw "类型切换：找不到「账本」导航按钮。" }
    $ledgerButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900

    # 1) Deposit on the isolated ledger.
    Select-Kind "入金"
    Set-EditById "EntryAmount" "500"
    Set-EditById "EntryNote" "冒烟入金500"
    Invoke-Record
    $statusEl = Find-ById2 "ImportStatusText"
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and $statusEl -and ($statusEl.Current.Name -notmatch "已记账")) {
        Start-Sleep -Milliseconds 300
        $statusEl = Find-ById2 "ImportStatusText"
    }
    if (-not $statusEl -or ($statusEl.Current.Name -notmatch "已记账")) {
        throw "类型切换：入金未得到「已记账」（实际：$(if ($statusEl) { $statusEl.Current.Name })）。"
    }
    Wait-ForGridText "冒烟入金500"

    # 2) Type an amount but DON'T record, then switch to Buy: the production handler
    #    disables the amount box and keeps its text — the exact stale-input shape of R7-01.
    Set-EditById "EntryAmount" "500"
    Select-Kind "买入"
    $amountBox = Find-ById2 "EntryAmount"
    if (-not $amountBox) { throw "类型切换：找不到金额输入框。" }
    if ($amountBox.Current.IsEnabled) {
        throw "类型切换：切到买入后金额框仍可编辑（应与生产处理器一致被禁用）。"
    }
    $amountValue = $amountBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ("$amountValue" -ne "500") {
        throw "类型切换：买入类型下金额框残留文本意外变化（实际：$amountValue）。"
    }

    # 3) A valid Buy must record despite the stale disabled amount text (R7-01 page level).
    Set-EditById "EntrySymbol" "SSE:600000"
    Set-EditById "EntryQuantity" "1"
    Set-EditById "EntryPrice" "10"
    Set-EditById "EntryNote" "冒烟买入"
    Invoke-Record
    $statusEl = Find-ById2 "ImportStatusText"
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and $statusEl -and ($statusEl.Current.Name -notmatch "已记账")) {
        Start-Sleep -Milliseconds 300
        $statusEl = Find-ById2 "ImportStatusText"
    }
    if (-not $statusEl -or ($statusEl.Current.Name -notmatch "已记账")) {
        throw "类型切换：买入未得到「已记账」（实际：$(if ($statusEl) { $statusEl.Current.Name })）——无关旧金额可能仍在阻断。"
    }
    Wait-ForGridText "SSE:600000"
    Wait-ForGridText "冒烟买入"

    # Back to the overview so the subsequent dialog check sees the usual nav state.
    $overviewButton = $navButtons | Where-Object { $_.Current.Name -like "*总览*" } | Select-Object -First 1
    if ($overviewButton) {
        $overviewButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }

    Add-Step "ledger type switch e2e" "passed" "入金 500 入账；买入在残留禁用金额文本（500）下仍入账，流水表可见 SSE:600000"
}

function Invoke-RecordDialogCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    # WP-01 目标 4 at page level: 「新增记录」 is the ordinary path for account cash flow, and a
    # dialog's BAML is only parsed when it is shown. Type a real deposit into the isolated ledger,
    # save it, and require the row to reach the transaction grid — the unit tests drive
    # BuildTransaction, this drives the button.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "record dialog e2e" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $automation::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        throw "新增记录：未找到主窗口自动化节点。"
    }

    function Find-ById([System.Windows.Automation.AutomationElement]$root, [string]$automationId) {
        $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::AutomationIdProperty, $automationId)))
    }
    function Find-Button([System.Windows.Automation.AutomationElement]$root, [string]$name) {
        @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) |
            Where-Object { $_.Current.Name -like "*$name*" } | Select-Object -First 1
    }
    function Set-Edit([System.Windows.Automation.AutomationElement]$root, [string]$id, [string]$value) {
        $edit = Find-ById $root $id
        if (-not $edit) { throw "新增记录：找不到输入框 $id。" }
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    }
    function Wait-ForText([System.Windows.Automation.AutomationElement]$root, [string]$marker) {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $texts = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))
            $joined = ($texts | ForEach-Object { $_.Current.Name }) -join "`n"
            if ($joined -match [regex]::Escape($marker)) { return }
        }
        throw "新增记录：20 秒内未出现「$marker」。"
    }
    function Find-RecordWindow {
        $windows = @($automation::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $byPid))
        $windows | Where-Object { $_.Current.Name -eq "新增记录" } | Select-Object -First 1
    }

    $ledgerButton = Find-Button $main "账本"
    if (-not $ledgerButton) { throw "新增记录：找不到「账本」导航按钮。" }
    $ledgerButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900

    $main = $automation::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    $openButton = Find-Button $main "新增记录"
    if (-not $openButton) { throw "新增记录：找不到「新增记录」按钮。" }
    $openButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $dialog = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $dialog) {
        Start-Sleep -Milliseconds 300
        $dialog = Find-RecordWindow
    }
    if (-not $dialog) { throw "新增记录：窗口 15 秒内没有出现（BAML 可能在运行时才坏）。" }

    # The four operations must all be on offer (disabled ones still enumerate while expanded).
    $operations = Find-ById $dialog "OperationCombo"
    if (-not $operations) { throw "新增记录：找不到操作下拉。" }
    $expand = $operations.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 400
    $items = @($operations.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            $automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    $names = @($items | ForEach-Object { $_.Current.Name })
    foreach ($expected in @("投入", "转出", "转到我的另一个账户", "当天资产金额")) {
        if (-not ($names | Where-Object { $_ -like "*$expected*" })) {
            throw "新增记录：操作下拉里没有「$expected」（实际：$($names -join ' / ')）。"
        }
    }
    try { $expand.Collapse() } catch { }
    Start-Sleep -Milliseconds 200

    # 投入 is the default operation: date + amount + note is the whole form.
    Set-Edit $dialog "DateBox" "2025-06-30"
    Set-Edit $dialog "AmountBox" "500"
    Set-Edit $dialog "NoteBox" "冒烟新增记录500"
    $save = Find-Button $dialog "保存"
    if (-not $save) { throw "新增记录：找不到「保存」按钮。" }
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $closed = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $closed) {
        Start-Sleep -Milliseconds 300
        $closed = -not (Find-RecordWindow)
    }
    if (-not $closed) { throw "新增记录：保存后窗口未关闭（错误可能留在对话框上）。" }

    $main = $automation::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    Wait-ForText $main "冒烟新增记录500"

    # The four per-account numbers live in their own grid on the same page (WP-01 目标 3).
    if (-not (Find-ById $main "AccountSummaryGrid")) {
        throw "新增记录：账本页没有出现「账户汇总」表。"
    }

    Add-Step "record dialog e2e" "passed" "新增记录：四条操作在列；投入 500 保存后进入流水表，账户汇总表在位"

    $overviewButton = Find-Button $main "总览"
    if ($overviewButton) {
        $overviewButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }
}

function Invoke-DialogCheck {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$ButtonName,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle
    )

    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction Stop
    }
    catch {
        Add-Step "open dialog: $ExpectedTitle" "skipped" "UIAutomation 不可用"
        return
    }

    $automation = [System.Windows.Automation.AutomationElement]
    $root = $automation::RootElement
    $byPid = New-Object System.Windows.Automation.PropertyCondition(
        $automation::ProcessIdProperty, $ProcessId)
    $main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byPid)
    if (-not $main) {
        Add-Step "open dialog: $ExpectedTitle" "skipped" "未找到主窗口自动化节点"
        return
    }

    $byName = New-Object System.Windows.Automation.PropertyCondition(
        $automation::NameProperty, $ButtonName)
    $button = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
    if (-not $button) {
        throw "界面上找不到「$ButtonName」按钮。"
    }

    $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    $deadline = (Get-Date).AddSeconds(20)
    $dialog = $null
    while ((Get-Date) -lt $deadline) {
        $byTitle = New-Object System.Windows.Automation.PropertyCondition(
            $automation::NameProperty, $ExpectedTitle)
        $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byTitle)
        if ($dialog) { break }
        Start-Sleep -Milliseconds 400
    }

    if (-not $dialog) {
        throw "点击「$ButtonName」后没有出现「$ExpectedTitle」窗口（XAML 可能加载失败）。"
    }
    Add-Step "open dialog: $ExpectedTitle" "passed" "已加载并关闭"

    # Close it again so the rest of the run sees a normal main window.
    $closeCondition = New-Object System.Windows.Automation.PropertyCondition(
        $automation::NameProperty, "取消")
    $cancel = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCondition)
    if ($cancel) {
        $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Start-Sleep -Milliseconds 600
}

$attempt = New-SmokeAttempt $ReportPath
$reportFull = $attempt.Report
$reportDir = $attempt.Directory
$startedAt = (Get-Date).ToUniversalTime().ToString('o')
$sourceFingerprint = @()
$transcriptStarted = $false
$stdoutLog = Join-Path $reportDir "desktop-stdout.log"
$stderrLog = Join-Path $reportDir "desktop-stderr.log"

Write-Host "UI 冒烟：启动桌面应用并确认窗口出现" -ForegroundColor Cyan

try {
    Start-Transcript -LiteralPath (Join-Path $reportDir 'runner.log') -NoClobber | Out-Null
    $transcriptStarted = $true
    [pscustomobject]@{ tool='ui_smoke'; attempt_id=$attempt.Id; status='running'; started_at=$startedAt; attempt_dir=$reportDir } |
        ConvertTo-Json | Out-File -LiteralPath $reportFull -Encoding utf8
    $sourceFingerprint = @(git -C $workspace ls-files --cached --others --exclude-standard |
        Where-Object { $_ -match '^(src/|tests/|python/|scripts/|packaging/|VERSION$|PHASE$|Caishenfolio.slnx$|Directory.Build\.)' -and $_ -notmatch '/(bin|obj|__pycache__)/' } |
        ForEach-Object { if (Test-Path -LiteralPath (Join-Path $workspace $_) -PathType Leaf) {
            [pscustomobject]@{ path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $workspace $_) -Algorithm SHA256).Hash }
        } })
    $sourceFingerprint | ConvertTo-Json -Depth 3 | Out-File -LiteralPath (Join-Path $reportDir 'source-manifest.json') -Encoding utf8
    Add-Step "resolve project" "passed" (Resolve-InWorkspace $DesktopProject)

    # Fixture provider keeps the smoke test offline and deterministic.
    $env:CAISHENFOLIO_MARKET_PROVIDER = "fixture"
    $env:CAISHENFOLIO_SKIP_SYMBOL_INDEX_NETWORK = "1"
    # Test isolation (recheck-5 R5-01): the smoke owns a DEDICATED state root so the user's
    # credentials/ledger/cache are never read or written, and the broker force-overrides the
    # provider AFTER credentials injection — a saved "auto"/real data source can no longer
    # leak real network calls into what should be an offline fixture run.
    # Unique directory each run under the user temp root. A repo path is outside the
    # Host allowlist (LocalAppData or temp), so it cannot carry an isolated ledger.
    $smokeRootParent = Join-Path $env:TEMP "caishenfolio-ui-smoke"
    $smokeStateRoot = Join-Path $smokeRootParent ("state-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $smokeStateRoot -Force | Out-Null
    $env:CAISHENFOLIO_TEST_STATE_ROOT = $smokeStateRoot
    $env:CAISHENFOLIO_TEST_FORCE_PROVIDER = "fixture"
    # Reuse the already-provisioned interpreter through a junction. A fresh root would
    # otherwise build its own venv and start a python that did not inherit this environment.
    $managedVenv = Join-Path $env:LOCALAPPDATA "Caishenfolio\state\.venv"
    $managedPython = Join-Path $managedVenv "Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $managedPython)) {
        throw "找不到受管 venv：$managedPython"
    }
    $venvLink = Join-Path $smokeStateRoot "state\.venv"
    New-Item -ItemType Directory -Path (Split-Path -Parent $venvLink) -Force | Out-Null
    New-Item -ItemType Junction -Path $venvLink -Target $managedVenv | Out-Null

    # Seed the ISOLATED ledger (recheck-5 R5-02 #1): one traceable CNY→JPY receipt so the
    # FX page's real prefill button has facts to bring in. The user's ledger is untouched —
    # this file lives only inside the smoke's own state root.
    #
    # WP-01: the DDL below stays at schema v9 on purpose — no `accounts.mode` column — so every
    # run also exercises the real v9→v10 migration (the app must ALTER its own file and keep this
    # seeded receipt). Do not "fix" it to v10; that would delete the only end-to-end migration
    # check the app itself performs.
    $seedDb = Join-Path $smokeStateRoot "state\portfolio.db"
    New-Item -ItemType Directory -Path (Split-Path -Parent $seedDb) -Force | Out-Null
    $seedPy = Join-Path $smokeStateRoot "seed.py"
    $seedBody = @'
import sqlite3
conn = sqlite3.connect(r"__SEED_DB__")
conn.executescript("""
CREATE TABLE IF NOT EXISTS accounts (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, main_currency TEXT NOT NULL,
    broker TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '',
    archived INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS instruments (
    symbol TEXT PRIMARY KEY, name TEXT NOT NULL, asset_class TEXT NOT NULL,
    region TEXT NOT NULL, currency TEXT NOT NULL, lot_size TEXT NOT NULL DEFAULT '0',
    face_value TEXT NOT NULL DEFAULT '0', note TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS transactions (
    id TEXT PRIMARY KEY, account_id TEXT NOT NULL, kind TEXT NOT NULL, trade_date TEXT NOT NULL,
    symbol TEXT NOT NULL DEFAULT '', quantity TEXT NOT NULL DEFAULT '0',
    price TEXT NOT NULL DEFAULT '0', currency TEXT NOT NULL, fee TEXT NOT NULL DEFAULT '0',
    tax TEXT NOT NULL DEFAULT '0', cash_amount TEXT NOT NULL DEFAULT '0',
    counter_currency TEXT NOT NULL DEFAULT '', counter_amount TEXT NOT NULL DEFAULT '0',
    fx_rate TEXT NOT NULL DEFAULT '0', note TEXT NOT NULL DEFAULT '',
    import_batch_id TEXT NOT NULL DEFAULT '', recorded_at TEXT NOT NULL,
    reverses_transaction_id TEXT NOT NULL DEFAULT '', counter_account_id TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS fx_rates (
    base_currency TEXT NOT NULL, quote_currency TEXT NOT NULL, as_of TEXT NOT NULL,
    rate TEXT NOT NULL, provider TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (base_currency, quote_currency, as_of));
CREATE TABLE IF NOT EXISTS valuation_snapshots (
    as_of TEXT NOT NULL, base_currency TEXT NOT NULL, total_value TEXT NOT NULL,
    holdings_value TEXT NOT NULL DEFAULT '0', cash_value TEXT NOT NULL DEFAULT '0',
    cost_basis TEXT NOT NULL DEFAULT '0', complete INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (as_of, base_currency));
CREATE TABLE IF NOT EXISTS valuation_position_snapshots (
    as_of TEXT NOT NULL, base_currency TEXT NOT NULL, symbol TEXT NOT NULL,
    account_id TEXT NOT NULL, currency TEXT NOT NULL, quantity TEXT NOT NULL DEFAULT '0',
    market_value_local TEXT, market_value_base TEXT,
    cost_basis_base TEXT NOT NULL DEFAULT '0', priced INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (as_of, base_currency, symbol, account_id));
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ipo_subscriptions (
    id TEXT PRIMARY KEY, account_id TEXT NOT NULL, symbol TEXT NOT NULL,
    name TEXT NOT NULL DEFAULT '', subscribe_date TEXT NOT NULL, status TEXT NOT NULL,
    currency TEXT NOT NULL, subscribed_quantity TEXT NOT NULL DEFAULT '0',
    allotted_quantity TEXT NOT NULL DEFAULT '0', issue_price TEXT NOT NULL DEFAULT '0',
    payment_date TEXT, listing_date TEXT, sold_date TEXT, sold_price TEXT NOT NULL DEFAULT '0',
    fee TEXT NOT NULL DEFAULT '0', note TEXT NOT NULL DEFAULT '');
INSERT INTO accounts VALUES ('acct_smoke', 'smoke-test-account', 'Bank', 'CNY', '', '', 0, '2026-01-01T00:00:00Z');
INSERT INTO transactions VALUES (
    'txn_smoke_fx', 'acct_smoke', 'FxExchange', '2026-01-05', '', '0', '0', 'CNY',
    '0', '0', '100', 'JPY', '2100', '21', 'smoke seed: 100 CNY -> 2100 JPY', '', '2026-01-05T00:00:00Z', '', '');
PRAGMA user_version = 9;
""")
conn.commit()
conn.close()
'@
    # Plain string replace (no -f): Python % formatting would choke on the SQL's percent-free
    # but multi-arg semantics; the placeholder is unique so Replace is exact.
    $seedBody = $seedBody.Replace("__SEED_DB__", $seedDb)
    # UTF8 WITHOUT BOM: the app's venv Python parses a BOM'd file as a syntax error.
    [System.IO.File]::WriteAllText($seedPy, $seedBody, (New-Object System.Text.UTF8Encoding($false)))
    $venvPy = Join-Path $env:LOCALAPPDATA "Caishenfolio\state\.venv\Scripts\python.exe"
    & $venvPy $seedPy
    if ($LASTEXITCODE -ne 0) {
        throw "账本预置失败（exit $LASTEXITCODE）。"
    }
    Add-Step "seed isolated ledger" "passed" "100 CNY → 2100 JPY @ 冒烟测试账户"

    # Run the exe this checkout just built. `dotnet run` previously reused output whose
    # file list still named another checkout, so the isolation argument never launched.
    $desktopExe = Join-Path $workspace "src\Caishenfolio.Desktop\bin\Debug\net8.0-windows\Caishenfolio.Desktop.exe"
    if (-not (Test-Path -LiteralPath $desktopExe)) {
        throw "找不到桌面程序：$desktopExe。先 dotnet build。"
    }
    @(Get-FileHash -LiteralPath $desktopExe, (Join-Path (Split-Path $desktopExe) 'Caishenfolio.Desktop.dll'), (Join-Path (Split-Path $desktopExe) 'Caishenfolio.Host.dll')) |
        Select-Object Path, Hash | ConvertTo-Json | Out-File -LiteralPath (Join-Path $reportDir 'binary-manifest.json') -Encoding utf8
    $hostProcess = Start-Process -FilePath $desktopExe `
        -WorkingDirectory $workspace `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog
    $appProcess = $hostProcess
    Add-Step "launch" "passed" "desktop pid=$($hostProcess.Id)"

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $windowTitle = $null
    while ((Get-Date) -lt $deadline) {
        # The launched exe is the window process. Match its pid so another open copy is ignored.
        $candidate = Get-Process -Id $hostProcess.Id -ErrorAction SilentlyContinue
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

    # First run opens the novice guide on top; skip it and wait for the main window title.
    # Later runs detect no guide and move on.
    Invoke-DismissOnboarding -ProcessId $appProcess.Id
    $appProcess.Refresh()
    $windowTitle = $appProcess.MainWindowTitle

    if ($windowTitle -notmatch "^Caishenfolio") {
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
    $procEnv = [System.Diagnostics.Process]::GetProcessById($appProcess.Id).StartInfo.EnvironmentVariables
    $seenRoot = [string]$procEnv["CAISHENFOLIO_TEST_STATE_ROOT"]
    $seenForce = [string]$procEnv["CAISHENFOLIO_TEST_FORCE_PROVIDER"]
    $rootState = if ($seenRoot -eq $smokeStateRoot) { "matches" } elseif ($seenRoot) { "differs" } else { "missing" }
    Add-Step "desktop test env" "passed" ("root=" + $rootState + " force=" + $(if ($seenForce) { $seenForce } else { "missing" }))

    # Provider gate (recheck-5 R5-01): assert the RUNNING core actually serves fixture
    # synthetic data before any form scenario runs. A wrong provider must fail HERE with the
    # provider name in the message — not 40 seconds later as "净额未出现".
    $healthDeadline = (Get-Date).AddSeconds(60)
    $healthJson = $null
    while ((Get-Date) -lt $healthDeadline) {
        try {
            $healthJson = Invoke-RestMethod -Uri "http://127.0.0.1:8765/health" -TimeoutSec 3
            break
        }
        catch {
            Start-Sleep -Milliseconds 800
        }
    }
    if (-not $healthJson) {
        throw "Provider gate：60 秒内无法访问分析核心 /health（127.0.0.1:8765）。"
    }
    # `dotnet run` does not forward the core's stdout into the redirected host log.
    # Identity is the python process this desktop actually spawned, matched to /health.
    $pythonCandidates = @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*caishenfolio_core.server*" })
    $diag = ($pythonCandidates | ForEach-Object {
        $providerValue = "(unread)"
        try {
            $proc = [System.Diagnostics.Process]::GetProcessById([int]$_.ProcessId)
            $providerValue = [string]$proc.StartInfo.EnvironmentVariables["CAISHENFOLIO_MARKET_PROVIDER"]
            if ([string]::IsNullOrWhiteSpace($providerValue)) { $providerValue = "(empty)" }
        }
        catch { $providerValue = "(gone)" }
        "pid=$($_.ProcessId) parent=$($_.ParentProcessId) provider=$providerValue"
    }) -join "; "
    Add-Step "core process candidates" "passed" ($(if ($diag) { $diag } else { "none" }))
    # Launcher python (child of the desktop) starts the interpreter that binds the port.
    # Own that whole tree. A health pid outside it is someone else's 8765 and is rejected.
    $ownedIds = New-Object System.Collections.Generic.HashSet[int]
    [void]$ownedIds.Add([int]$appProcess.Id)
    $growing = $true
    while ($growing) {
        $growing = $false
        foreach ($candidate in $pythonCandidates) {
            $pidValue = [int]$candidate.ProcessId
            $parentValue = [int]$candidate.ParentProcessId
            if ($ownedIds.Contains($parentValue) -and -not $ownedIds.Contains($pidValue)) {
                [void]$ownedIds.Add($pidValue)
                $growing = $true
            }
        }
    }
    $healthPid = [int]$healthJson.process_id
    if (-not $ownedIds.Contains($healthPid)) {
        throw "Provider gate：/health process_id=$healthPid 不在本次桌面 $($appProcess.Id) 的进程树中（$diag）。"
    }
    $corePid = $healthPid
    Add-Step "core health reachable" "passed" "provider=$($healthJson.market_provider) synthetic=$($healthJson.market_data_synthetic) env=$($healthJson.market_provider_env) pid=$corePid"
    if ($healthJson.market_provider -ne "fixture" -or -not $healthJson.market_data_synthetic) {
        throw "Provider gate：核心数据源不是 fixture（实际 provider=$($healthJson.market_provider)，synthetic=$($healthJson.market_data_synthetic)）。隔离启动未生效。"
    }

    if (-not $StartupOnly) {
    # Switching pages runs each one's reload path, which loading the window alone never touches.
    Invoke-NavigationWalk -ProcessId $appProcess.Id -Buttons @("首页", "证据卡", "持仓", "账本", "估值", "打新", "汇率", "风险", "总览")

    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "切换页面后应用退出。日志：$stderrLog"
    }

    # recheck-2 F-01: prove the FX cost inputs are individually visible and non-overlapping —
    # "the page loads" never proved the two fee panels did not sit on top of each other.
    Invoke-CostInputLayoutCheck -ProcessId $appProcess.Id

    # recheck-4 §5 items #1/#2 (programmatic halves): keyboard reachability of the cost
    # boxes, then a REAL form-fill → compare → net-figure rendering pass.
    Invoke-CostInputFocusCheck -ProcessId $appProcess.Id
    Invoke-FxFormToNetEndToEnd -ProcessId $appProcess.Id

    # FA1: this stage's real page verifications — carry range switching with busy
    # disable/restore on the FX page, then type switching + recording on the ledger page.
    Invoke-FxRangeSwitchCheck -ProcessId $appProcess.Id
    Invoke-LedgerTypeSwitchCheck -ProcessId $appProcess.Id
    Invoke-RecordDialogCheck -ProcessId $appProcess.Id

    # A dialog's BAML is only parsed when it is shown, so opening it is the only way to catch a
    # XAML break in a window the main view never loads.
    Invoke-DialogCheck -ProcessId $appProcess.Id -ButtonName "设置" -ExpectedTitle "理财偏好设置"

    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "打开设置窗口后应用退出。日志：$stderrLog"
    }
    }
}
catch {
    $failure = $_.Exception.Message
    Add-Step "smoke" "failed" $failure
}
finally {
    # Save a process snapshot BEFORE teardown, including failed-start attempts.
    @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'Caishenfolio.Desktop.exe'" -ErrorAction SilentlyContinue |
        Select-Object ProcessId, ParentProcessId, Name, CreationDate) |
        ConvertTo-Json -Depth 3 | Out-File -LiteralPath (Join-Path $reportDir 'processes-before-cleanup.json') -Encoding utf8
    if (-not $KeepOpen) {
        # Only the process this run started, plus a core whose parent is that desktop pid.
        # Never stop every Caishenfolio.Desktop by name: a user's own window may be open.
        $ownedCore = @()
        if ($appProcess) {
            $tree = @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -like "*caishenfolio_core.server*" })
            $keep = New-Object System.Collections.Generic.HashSet[int]
            [void]$keep.Add([int]$appProcess.Id)
            $growing = $true
            while ($growing) {
                $growing = $false
                foreach ($candidate in $tree) {
                    $pidValue = [int]$candidate.ProcessId
                    $parentValue = [int]$candidate.ParentProcessId
                    if ($keep.Contains($parentValue) -and -not $keep.Contains($pidValue)) {
                        [void]$keep.Add($pidValue)
                        $growing = $true
                    }
                }
            }
            $ownedCore = @($tree | Where-Object { $keep.Contains([int]$_.ProcessId) })
        }
        foreach ($core in $ownedCore) {
            try { Stop-Process -Id $core.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
        foreach ($p in @($appProcess, $hostProcess)) {
            if ($p) {
                try { $p.Refresh(); if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
            }
        }
        if ($smokeStateRoot) {
            # Keep the core-side runtime log before the isolated root is removed: a core
            # that dies during provisioning is otherwise undiagnosable after cleanup.
            foreach ($candidate in @(
                (Join-Path $smokeStateRoot "python-runtime.log"),
                (Join-Path $smokeStateRoot "state\python-runtime.log"))) {
                if (Test-Path -LiteralPath $candidate) {
                    try {
                        Copy-Item -LiteralPath $candidate -Destination (Join-Path $reportDir "core-runtime.log") -Force
                    } catch { }
                }
            }
            Remove-OwnedSmokeDirectory $smokeStateRoot $smokeRootParent
        }
    }

    $report = [pscustomobject]@{
        tool       = "ui_smoke"
        attempt_id = $attempt.Id
        mode       = if ($StartupOnly) { 'startup_only' } else { 'full' }
        started_at = $startedAt
        ended_at   = (Get-Date).ToUniversalTime().ToString('o')
        exit_code  = if ($failure) { 1 } else { 0 }
        attempt_dir = $reportDir
        command    = "powershell -NoProfile -File scripts/ui_smoke.ps1"
        parameters = [pscustomobject]@{ ReportPath=$ReportPath; DesktopProject=$DesktopProject; TimeoutSeconds=$TimeoutSeconds; StartupOnly=[bool]$StartupOnly; KeepOpen=[bool]$KeepOpen }
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
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}

$critical = @(Test-CriticalSmokeSkipped $steps)
if ($critical.Count -gt 0 -and -not $failure) {
    $failure = "关键步骤被跳过，不能计为验收通过：" + (($critical | ForEach-Object { $_.name }) -join ", ")
    $report.status = "failed"
    $report.failure = $failure
    $report.exit_code = 1
    $report | ConvertTo-Json -Depth 5 | Out-File -FilePath $reportFull -Encoding utf8
}

$report | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $reportDir 'report.json') -Encoding utf8

if ($failure) {
    Write-Host "UI 冒烟失败。" -ForegroundColor Red
    exit 1
}

Write-Host "UI 冒烟通过。" -ForegroundColor Green
