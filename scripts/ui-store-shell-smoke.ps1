param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot "..\artifacts\StreamTranslator")
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PackageRoot "StreamTranslator.exe"
if (-not (Test-Path $exe)) {
    throw "StreamTranslator.exe was not found at $exe"
}

$process = Start-Process -FilePath $exe -WorkingDirectory $PackageRoot -PassThru

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class UiSmokeWindowInfo
{
    public string Handle { get; set; }
    public string Title { get; set; }
    public long Style { get; set; }
    public bool Visible { get; set; }
}

public static class UiSmokeNative
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
    }

    public static UiSmokeWindowInfo[] GetWindows(int processId)
    {
        var windows = new List<UiSmokeWindowInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != processId) return true;

            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            windows.Add(new UiSmokeWindowInfo
            {
                Handle = "0x" + hWnd.ToInt64().ToString("X"),
                Title = title.ToString(),
                Style = GetWindowLongPtr(hWnd, -16).ToInt64(),
                Visible = IsWindowVisible(hWnd)
            });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static void BringToFront(string handleText)
    {
        var handle = new IntPtr(Convert.ToInt64(handleText.Substring(2), 16));
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    public static void CloseWindow(string handleText)
    {
        var handle = new IntPtr(Convert.ToInt64(handleText.Substring(2), 16));
        SendMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

    public static void ClickScreenPoint(double x, double y)
    {
        SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@

try {
    $main = $null
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            throw "StreamTranslator exited during startup."
        }

        $windows = [UiSmokeNative]::GetWindows($process.Id)
        $main = $windows | Where-Object { $_.Title -eq "StreamTranslator" -and $_.Visible } | Select-Object -First 1
    } while ($null -eq $main -and (Get-Date) -lt $deadline)

    if ($null -eq $main) {
        throw "Main window was not visible."
    }

    [UiSmokeNative]::BringToFront($main.Handle)
    Start-Sleep -Milliseconds 500

    [UiSmokeNative]::GetWindows($process.Id) |
        Where-Object { $_.Visible -and $_.Title -and $_.Title -ne "StreamTranslator" } |
        ForEach-Object { [UiSmokeNative]::CloseWindow($_.Handle) }
    Start-Sleep -Milliseconds 800

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "StreamTranslator")
    $windowType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $mainCondition = New-Object System.Windows.Automation.AndCondition($pidCondition, $nameCondition, $windowType)
    $mainElement = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $mainCondition)
    if ($null -eq $mainElement) {
        throw "Main window was not reachable through UI Automation."
    }

    function Find-ByAutomationId([string]$automationId) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
        return $mainElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    function Invoke-Element($element, [string]$description) {
        if ($null -eq $element) {
            throw "$description was not found."
        }

        try {
            $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
            return
        }
        catch [System.InvalidOperationException] {
        }

        try {
            $pattern = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            return
        }
        catch [System.InvalidOperationException] {
        }

        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
            [UiSmokeNative]::ClickScreenPoint($rect.Left + ($rect.Width / 2), $rect.Top + ($rect.Height / 2))
            return
        }

        throw "$description cannot be invoked, selected, or clicked through UI Automation."
    }

    $navigation = Find-ByAutomationId "MainNavigation"
    if ($null -eq $navigation) {
        $navigationCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "NavigationItems")
        $navigation = $mainElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navigationCondition)
    }
    $startButton = Find-ByAutomationId "StartStopButton"
    $floatingButton = Find-ByAutomationId "ShowFloatingWindowButton"
    $removedSearchBox = Find-ByAutomationId "ShellSearchBox"
    $subtitlesNav = Find-ByAutomationId "NavSubtitlesPage"
    $audioNav = Find-ByAutomationId "NavAudioPage"
    $minimizeButton = Find-ByAutomationId "TitleBarMinimizeButton"
    $maximizeButton = Find-ByAutomationId "TitleBarMaximizeButton"
    $closeButton = Find-ByAutomationId "TitleBarCloseButton"

    if ($null -eq $navigation) { throw "Store-like NavigationView was not found." }
    if ($null -eq $startButton) { throw "Top command start/stop button was not found." }
    if ($null -eq $floatingButton) { throw "Top command floating window button was not found." }
    if ($null -ne $removedSearchBox) { throw "Title bar search/status box should not exist." }
    if ($null -eq $subtitlesNav) { throw "Subtitles navigation item was not found." }
    if ($null -eq $audioNav) { throw "Audio navigation item was not found." }
    if ($null -eq $minimizeButton) { throw "Title bar minimize button was not found." }
    if ($null -eq $maximizeButton) { throw "Title bar maximize button was not found." }
    if ($null -eq $closeButton) { throw "Title bar close button was not found." }

    $navRect = $navigation.Current.BoundingRectangle
    $startRect = $startButton.Current.BoundingRectangle
    $floatingRect = $floatingButton.Current.BoundingRectangle
    if ($startRect.Left -le ($navRect.Left + 120)) {
        throw "Start/stop button still appears inside the left navigation area."
    }

    if ($startRect.Height -gt 46) {
        throw "Start/stop title-bar button is too tall."
    }

    if ($floatingRect.Height -gt 46) {
        throw "Floating-window title-bar button is too tall."
    }

    Invoke-Element $subtitlesNav "Subtitles navigation item"
    Start-Sleep -Milliseconds 500
    Invoke-Element $audioNav "Audio navigation item"
    Start-Sleep -Milliseconds 500

    $subtitlesNav = Find-ByAutomationId "NavSubtitlesPage"
    $audioNav = Find-ByAutomationId "NavAudioPage"

    if ($audioNav.Current.ItemStatus -ne "Active") {
        throw "Navigation active state did not move to the clicked audio page."
    }

    if ($subtitlesNav.Current.ItemStatus -eq "Active") {
        throw "Previous navigation item remained active after switching pages."
    }

    Invoke-Element $floatingButton "Top command floating window button"
    Start-Sleep -Milliseconds 900

    $floatingWindow = [UiSmokeNative]::GetWindows($process.Id) |
        Where-Object { $_.Visible -and $_.Title -and $_.Title -ne "StreamTranslator" } |
        Select-Object -First 1
    if ($null -eq $floatingWindow) {
        throw "Floating subtitle window did not open from the top command button."
    }

    Invoke-Element $floatingButton "Top command floating window button"
    Start-Sleep -Milliseconds 900

    $floatingWindow = [UiSmokeNative]::GetWindows($process.Id) |
        Where-Object { $_.Visible -and $_.Title -and $_.Title -ne "StreamTranslator" } |
        Select-Object -First 1
    if ($null -ne $floatingWindow) {
        throw "Floating subtitle window did not hide after the top command button was invoked again."
    }

    "PASS ui-store-shell-smoke"
}
finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        Start-Sleep -Seconds 2
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
