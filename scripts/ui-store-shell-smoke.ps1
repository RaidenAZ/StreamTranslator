param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot "..\artifacts\StreamTranslator")
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PackageRoot "StreamTranslator.exe"
if (-not (Test-Path $exe)) {
    throw "StreamTranslator.exe was not found at $exe"
}

$mainWindowXaml = Join-Path $PSScriptRoot "..\src\StreamTranslator.App\MainWindow.xaml"
if (Test-Path $mainWindowXaml) {
    $mainWindowMarkup = Get-Content -LiteralPath $mainWindowXaml -Raw
    if ($mainWindowMarkup -notmatch 'WindowBackdropType="Mica"') {
        throw "Main window should use the Mica backdrop."
    }

    if ($mainWindowMarkup -notmatch '<ui:FluentWindow[\s\S]*Background="Transparent"') {
        throw "Main window should leave the backdrop visible through a transparent window background."
    }

    if ($mainWindowMarkup -notmatch '<Grid Background="Transparent">') {
        throw "Main shell root should be transparent so the Mica backdrop remains visible."
    }

    if ($mainWindowMarkup -notmatch '<Border x:Name="HomeIssuesCard"[\s\S]*AutomationProperties\.AutomationId="HomeIssuesPanel"') {
        throw "Home issues should be presented in its own card, outside the overview panel."
    }

    if ($mainWindowMarkup -notmatch '<ListBox x:Name="SubtitleList"[\s\S]*?AutomationProperties\.AutomationId="SubtitleHistoryList"') {
        throw "Subtitle history list should expose a stable UI automation boundary."
    }

    if ($mainWindowMarkup -notmatch '<ListBox x:Name="SubtitleList"[\s\S]*?HorizontalContentAlignment="Stretch"') {
        throw "Subtitle history rows should stretch to the available width."
    }

    if ($mainWindowMarkup -notmatch '<ListBox x:Name="SubtitleList"[\s\S]*?ScrollViewer\.HorizontalScrollBarVisibility="Disabled"') {
        throw "Subtitle history should not expose a horizontal scrollbar."
    }

    if ($mainWindowMarkup -notmatch 'Text="\{Binding GeneratedTimeText\}"') {
        throw "Subtitle history rows should display the recorded system time."
    }

    if ($mainWindowMarkup -notmatch 'Text="\{Binding SourceText\}"[\s\S]*?TextWrapping="Wrap"') {
        throw "Subtitle history text should wrap within the available width."
    }

    if ($mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="VadModeBalanced"') {
        throw "Settings should expose the balanced adaptive VAD mode."
    }

    if ($mainWindowMarkup -notmatch 'x:Name="EndSilenceBox"[\s\S]*?AutomationProperties\.AutomationId="FixedEndSilenceBox"[\s\S]*?IsEnabled="False"') {
        throw "Fixed end-silence input should be disabled while balanced adaptive mode is selected."
    }

    if ($mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="CurrentVadEndpointText"') {
        throw "Settings should display the current effective VAD endpoint."
    }
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
    $titleBarAppTitle = Find-ByAutomationId "TitleBarAppTitle"
    $removedTitleBarAppIcon = Find-ByAutomationId "TitleBarAppIcon"
    $startButton = Find-ByAutomationId "StartStopButton"
    $floatingButton = Find-ByAutomationId "HomeFloatingWindowButton"
    $removedTitleBarFloatingButton = Find-ByAutomationId "ShowFloatingWindowButton"
    $removedSearchBox = Find-ByAutomationId "ShellSearchBox"
    $homeNav = Find-ByAutomationId "NavHomePage"
    $subtitleHistoryNav = Find-ByAutomationId "NavSubtitleHistoryPage"
    $settingsNav = Find-ByAutomationId "NavSettingsPage"
    $aboutNav = Find-ByAutomationId "NavAboutPage"
    $removedAudioNav = Find-ByAutomationId "NavAudioPage"
    $removedServiceNav = Find-ByAutomationId "NavServicePage"
    $removedFloatingNav = Find-ByAutomationId "NavFloatingPage"
    $homeControlBar = Find-ByAutomationId "HomeControlBar"
    $homeOverviewPanel = Find-ByAutomationId "HomeOverviewPanel"
    $removedHomeStatusTiles = Find-ByAutomationId "HomeStatusTiles"
    $audioInputStatus = Find-ByAutomationId "OverviewAudioInputStatus"
    $speechDetectionStatus = Find-ByAutomationId "OverviewSpeechDetectionStatus"
    $recognitionWorkerStatus = Find-ByAutomationId "OverviewRecognitionWorkerStatus"
    $recognitionApiStatus = Find-ByAutomationId "OverviewRecognitionApiStatus"
    $subtitleOutputStatus = Find-ByAutomationId "OverviewSubtitleOutputStatus"
    $homeDeviceText = Find-ByAutomationId "HomeAudioDeviceText"
    $homeAudioLevel = Find-ByAutomationId "HomeAudioLevelBar"
    $homeIssuesPanel = Find-ByAutomationId "HomeIssuesPanel"
    $minimizeButton = Find-ByAutomationId "TitleBarMinimizeButton"
    $maximizeButton = Find-ByAutomationId "TitleBarMaximizeButton"
    $closeButton = Find-ByAutomationId "TitleBarCloseButton"

    if ($null -eq $navigation) { throw "Store-like NavigationView was not found." }
    if ($null -eq $titleBarAppTitle) { throw "Lightweight title-bar app title was not found." }
    if ($null -ne $removedTitleBarAppIcon) { throw "Title-bar app icon should not exist." }
    if ($null -eq $startButton) { throw "Home start/stop button was not found." }
    if ($null -eq $floatingButton) { throw "Home floating window button was not found." }
    if ($null -ne $removedTitleBarFloatingButton) { throw "Title-bar floating window button should not exist." }
    if ($null -ne $removedSearchBox) { throw "Title bar search/status box should not exist." }
    if ($null -eq $homeNav) { throw "Home navigation item was not found." }
    if ($null -eq $subtitleHistoryNav) { throw "Subtitle history navigation item was not found." }
    if ($null -eq $settingsNav) { throw "Settings navigation item was not found." }
    if ($null -eq $aboutNav) { throw "About navigation item was not found." }
    if ($null -ne $removedAudioNav) { throw "Audio should not remain as a top-level navigation item." }
    if ($null -ne $removedServiceNav) { throw "Service should not remain as a top-level navigation item." }
    if ($null -ne $removedFloatingNav) { throw "Floating window should not remain as a top-level navigation item." }
    if ($null -eq $homeControlBar) { throw "Home workbench control bar was not found." }
    if ($null -eq $homeOverviewPanel) { throw "Home overview panel was not found." }
    if ($null -ne $removedHomeStatusTiles) { throw "Home should not use the all-card dashboard layout." }
    if ($null -eq $audioInputStatus) { throw "Audio input status was not found in the overview panel." }
    if ($null -eq $speechDetectionStatus) { throw "Speech detection status was not found in the overview panel." }
    if ($null -eq $recognitionWorkerStatus) { throw "Recognition worker status was not found in the overview panel." }
    if ($null -eq $recognitionApiStatus) { throw "Recognition API status was not found in the overview panel." }
    if ($null -eq $subtitleOutputStatus) { throw "Subtitle output status was not found in the overview panel." }
    if ($null -eq $homeDeviceText) { throw "Home audio device text was not found." }
    if ($null -eq $homeAudioLevel) { throw "Home audio level bar was not found." }
    if ($null -eq $homeIssuesPanel) { throw "Home issues panel should be a separate card." }
    if ($null -eq $minimizeButton) { throw "Title bar minimize button was not found." }
    if ($null -eq $maximizeButton) { throw "Title bar maximize button was not found." }
    if ($null -eq $closeButton) { throw "Title bar close button was not found." }

    $navRect = $navigation.Current.BoundingRectangle
    $startRect = $startButton.Current.BoundingRectangle
    if ($startRect.Top -lt ($navRect.Top + 80)) {
        throw "Start/stop button still appears in the title bar instead of the home workbench."
    }

    Invoke-Element $subtitleHistoryNav "Subtitle history navigation item"
    Start-Sleep -Milliseconds 500
    Invoke-Element $settingsNav "Settings navigation item"
    $navigationDeadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $subtitleHistoryNav = Find-ByAutomationId "NavSubtitleHistoryPage"
        $settingsNav = Find-ByAutomationId "NavSettingsPage"
        $navigationUpdated =
            $null -ne $settingsNav -and
            $settingsNav.Current.ItemStatus -eq "Active" -and
            $null -ne $subtitleHistoryNav -and
            $subtitleHistoryNav.Current.ItemStatus -ne "Active"
    } while (-not $navigationUpdated -and (Get-Date) -lt $navigationDeadline)

    $audioSettingsGroup = Find-ByAutomationId "SettingsAudioGroup"
    $recognitionSettingsGroup = Find-ByAutomationId "SettingsRecognitionGroup"
    $floatingSettingsGroup = Find-ByAutomationId "SettingsFloatingGroup"
    $diagnosticsSettingsGroup = Find-ByAutomationId "SettingsDiagnosticsGroup"
    $balancedVadMode = Find-ByAutomationId "VadModeBalanced"
    $fixedEndSilenceBox = Find-ByAutomationId "FixedEndSilenceBox"
    $currentVadEndpointText = Find-ByAutomationId "CurrentVadEndpointText"

    if ($settingsNav.Current.ItemStatus -ne "Active") {
        throw "Navigation active state did not move to the clicked settings page."
    }

    if ($subtitleHistoryNav.Current.ItemStatus -eq "Active") {
        throw "Previous navigation item remained active after switching pages."
    }

    if ($null -eq $audioSettingsGroup) { throw "Settings audio group was not found." }
    if ($null -eq $recognitionSettingsGroup) { throw "Settings recognition service group was not found." }
    if ($null -eq $floatingSettingsGroup) { throw "Settings floating subtitle group was not found." }
    if ($null -eq $diagnosticsSettingsGroup) { throw "Settings diagnostics group was not found." }
    if ($null -eq $balancedVadMode) { throw "Balanced adaptive VAD mode was not found." }
    if ($null -eq $fixedEndSilenceBox) { throw "Fixed end-silence input was not found." }
    if ($fixedEndSilenceBox.Current.IsEnabled) { throw "Fixed end-silence input should be disabled in balanced mode." }
    if ($null -eq $currentVadEndpointText) { throw "Current VAD endpoint status was not found." }

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

    Invoke-Element $subtitleHistoryNav "Subtitle history navigation item"
    Start-Sleep -Milliseconds 500
    $clearHistoryButton = Find-ByAutomationId "ClearHistoryButton"
    if ($null -eq $clearHistoryButton) { throw "Clear history button was not found." }

    Invoke-Element $clearHistoryButton "Clear history button"
    Start-Sleep -Milliseconds 500
    $confirmWindow = [UiSmokeNative]::GetWindows($process.Id) |
        Where-Object { $_.Visible -and $_.Title -and $_.Title -ne "StreamTranslator" } |
        Select-Object -First 1
    if ($null -eq $confirmWindow) {
        throw "Clear history confirmation dialog did not appear."
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
