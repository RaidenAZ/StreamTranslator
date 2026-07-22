param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot "..\artifacts\StreamTranslator")
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PackageRoot "StreamTranslator.exe"
if (-not (Test-Path $exe)) {
    throw "StreamTranslator.exe was not found at $exe"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ClipboardSmokeNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags,
        uint x,
        uint y,
        uint data,
        UIntPtr extraInfo);
}
'@

$subtitleDirectory = Join-Path $PackageRoot "data\subtitles"
$historyPath = Join-Path $subtitleDirectory "$(Get-Date -Format 'yyyy-MM-dd').jsonl"
$historyExisted = Test-Path $historyPath
$historyBackup = if ($historyExisted) {
    [IO.File]::ReadAllBytes($historyPath)
}
else {
    $null
}

New-Item -ItemType Directory -Force -Path $subtitleDirectory | Out-Null
$fixture = [ordered]@{
    type = "subtitle"
    sequence = 1
    utteranceGroupId = "clipboard-smoke:1"
    revision = 1
    replacesSequences = @()
    start = "00:00:00"
    end = "00:00:01"
    generatedAt = [DateTimeOffset]::Now.ToString("O")
    sourceText = "Clipboard smoke fixture"
    translatedText = $null
    status = "Final"
} | ConvertTo-Json -Compress
[IO.File]::WriteAllText(
    $historyPath,
    $fixture + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$process = $null
$clipboardLocked = $false

try {
    $process = Start-Process -FilePath $exe -WorkingDirectory $PackageRoot -PassThru
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "StreamTranslator")
    $windowType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $mainCondition = New-Object System.Windows.Automation.AndCondition(
        $pidCondition,
        $nameCondition,
        $windowType)

    $mainElement = $null
    $startupDeadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) {
            throw "StreamTranslator exited during startup."
        }

        $mainElement = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $mainCondition)
    } while ($null -eq $mainElement -and (Get-Date) -lt $startupDeadline)

    if ($null -eq $mainElement) {
        throw "Main window was not reachable through UI Automation."
    }

    Start-Sleep -Milliseconds 800
    $mainHandle = [IntPtr]::new($mainElement.Current.NativeWindowHandle)
    [ClipboardSmokeNative]::ShowWindow($mainHandle, 9) | Out-Null
    [ClipboardSmokeNative]::SetForegroundWindow($mainHandle) | Out-Null
    Start-Sleep -Milliseconds 300

    function Find-ByAutomationId([string]$automationId) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $automationId)
        return $mainElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    }

    function Find-ByName([string]$name) {
        $elementNameCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $name)
        $condition = New-Object System.Windows.Automation.AndCondition(
            $pidCondition,
            $elementNameCondition)
        return $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    }

    function Invoke-Element($element, [string]$description) {
        if ($null -eq $element) {
            throw "$description was not found."
        }

        try {
            $pattern = $element.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            return
        }
        catch [System.InvalidOperationException] {
        }

        try {
            $pattern = $element.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            return
        }
        catch [System.InvalidOperationException] {
        }

        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) {
            throw "$description cannot be invoked, selected, or clicked."
        }

        [ClipboardSmokeNative]::SetCursorPos(
            [int][Math]::Round($rect.Left + ($rect.Width / 2)),
            [int][Math]::Round($rect.Top + ($rect.Height / 2))) | Out-Null
        [ClipboardSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [ClipboardSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    }

    $historyNavigation = Find-ByAutomationId "NavSubtitleHistoryPage"
    $copyButton = $null
    $copyButtonDeadline = (Get-Date).AddSeconds(5)
    do {
        [ClipboardSmokeNative]::ShowWindow($mainHandle, 9) | Out-Null
        [ClipboardSmokeNative]::SetForegroundWindow($mainHandle) | Out-Null
        Invoke-Element $historyNavigation "Subtitle history navigation item"
        Start-Sleep -Milliseconds 300
        $copyButton = Find-ByAutomationId "CopyAllSubtitlesButton"
    } while ($null -eq $copyButton -and (Get-Date) -lt $copyButtonDeadline)

    if ($null -eq $copyButton) {
        throw "Copy-all button was not found."
    }

    $historyList = Find-ByAutomationId "SubtitleHistoryList"
    $fixtureNameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Clipboard smoke fixture")
    $fixtureElement = $historyList.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $fixtureNameCondition)
    if ($null -eq $fixtureElement) {
        throw "Clipboard fixture was not loaded into subtitle history."
    }

    $clipboardDeadline = (Get-Date).AddSeconds(3)
    do {
        $clipboardLocked = [ClipboardSmokeNative]::OpenClipboard([IntPtr]::Zero)
        if (-not $clipboardLocked) {
            Start-Sleep -Milliseconds 50
        }
    } while (-not $clipboardLocked -and (Get-Date) -lt $clipboardDeadline)

    if (-not $clipboardLocked) {
        throw "The smoke test could not acquire the Windows clipboard."
    }

    Invoke-Element $copyButton "Copy-all button"

    $toastTitle = $null
    $toastMessage = $null
    $toastDeadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) {
            throw "StreamTranslator exited after the clipboard write failed."
        }

        $toastTitle = Find-ByName "复制失败"
        $toastMessage = Find-ByName "暂时无法访问系统剪贴板，请稍后重试。"
    } while (($null -eq $toastTitle -or $null -eq $toastMessage) -and (Get-Date) -lt $toastDeadline)

    $copyStatus = Find-ByAutomationId "SubtitleHistoryCopyStatus"
    if ($null -eq $toastTitle -or $null -eq $toastMessage) {
        $statusName = if ($null -eq $copyStatus) { "<missing>" } else { $copyStatus.Current.Name }
        throw "Clipboard failure Snackbar was not shown. Copy status: $statusName"
    }

    if ($null -eq $copyStatus -or $copyStatus.Current.Name -notmatch "剪贴板暂时不可用") {
        throw "Subtitle history did not expose the clipboard failure status."
    }

    "PASS clipboard-toast-smoke"
}
finally {
    if ($clipboardLocked) {
        [ClipboardSmokeNative]::CloseClipboard() | Out-Null
    }

    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        Start-Sleep -Seconds 2
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }

    if ($historyExisted) {
        [IO.File]::WriteAllBytes($historyPath, $historyBackup)
    }
    else {
        Remove-Item -LiteralPath $historyPath -ErrorAction SilentlyContinue
    }
}
