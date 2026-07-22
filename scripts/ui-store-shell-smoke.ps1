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

    if ($mainWindowMarkup -notmatch 'Text="\{Binding TranslatedText\}"[\s\S]*?TextWrapping="Wrap"') {
        throw "Subtitle history translation should wrap below the source text."
    }

    if ($mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="SettingsTranslationGroup"') {
        throw "Settings should expose the V1.2 translation group."
    }

    if ($mainWindowMarkup -notmatch 'Text="显示字幕数"') {
        throw "Floating subtitle capacity should be expressed as subtitle groups, not lines."
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

    if ($mainWindowMarkup -notmatch 'x:Name="CurrentVadEndpointText"[\s\S]*?Grid\.Column="2"') {
        throw "The effective VAD endpoint should align with the default-device controls in the right settings column."
    }

    if ($mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="FollowDefaultDeviceSwitch"') {
        throw "The default-device toggle should expose a stable alignment boundary."
    }

    if ($mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="TranslationTargetLanguageBox"' -or
        $mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="TranslationEnabledSwitch"' -or
        $mainWindowMarkup -notmatch 'AutomationProperties\.AutomationId="TranslationProfileList"') {
        throw "Translation settings should expose stable automation boundaries for the two-column form and profile list."
    }

    if ($mainWindowMarkup -notmatch 'Text="模型配置"[\s\S]*?x:Name="TranslationProfileList"') {
        throw "Translation profiles should be presented as a labeled full-width settings section."
    }

    if ($mainWindowMarkup -match 'OpenAI Chat Completions compatible') {
        throw "The translation card should not display the API compatibility notice below its title."
    }

    if ($mainWindowMarkup -notmatch 'x:Name="TranslationTargetLanguageBox"[\s\S]*?Width="280"') {
        throw "The target-language selector should use a compact fixed width."
    }

    if ($mainWindowMarkup -notmatch 'Text="模型配置"[\s\S]*?x:Name="TranslationSelectionSummaryText"[\s\S]*?x:Name="TranslationProfileList"') {
        throw "Translation profile status should appear below the section heading and above the profile list."
    }
}

$mainWindowCode = Join-Path $PSScriptRoot "..\src\StreamTranslator.App\MainWindow.xaml.cs"
if (Test-Path $mainWindowCode) {
    $mainWindowSource = Get-Content -LiteralPath $mainWindowCode -Raw
    if ($mainWindowSource -match 'SelectedItem\s*=\s*items\.FirstOrDefault\([^\r\n]+\)\s*\?\?\s*items\.FirstOrDefault') {
        throw "Translation profiles must not auto-select the first item when no active profile exists."
    }
    if ($mainWindowSource -notmatch 'OnFloatingVisibleGroupsChanged') {
        throw "Floating-window visibility changes should update translation backpressure priorities."
    }
    if ($mainWindowSource -notmatch 'AutomationProperties\.SetAutomationId\(protocolNotice, "TranslationProtocolNotice"\)' -or
        $mainWindowSource -notmatch '兼容 OpenAI Chat Completions API') {
        throw "The translation profile editor should display the OpenAI Chat Completions compatibility notice."
    }
    if ($mainWindowSource -notmatch '标准兼容（不设置思考模式）' -or
        $mainWindowSource -notmatch 'DeepSeek（thinking\.type=disabled）' -or
        $mainWindowSource -notmatch 'Qwen \+ vLLM（enable_thinking=false）' -or
        $mainWindowSource -notmatch '自定义 extraBody') {
        throw "Translation request compatibility choices should describe their effective thinking behavior."
    }
}

$floatingWindowCode = Join-Path $PSScriptRoot "..\src\StreamTranslator.App\FloatingSubtitleWindow.xaml.cs"
if (Test-Path $floatingWindowCode) {
    $floatingWindowSource = Get-Content -LiteralPath $floatingWindowCode -Raw
    if ($floatingWindowSource -notmatch 'WorkArea\.Height \* 0\.4') {
        throw "Floating subtitles should remain within 40% of the work area."
    }
    if ($floatingWindowSource -notmatch 'Chrome\.Measure\([\s\S]*double\.PositiveInfinity' -or
        $floatingWindowSource -notmatch 'Entries\.RemoveAt\(0\)') {
        throw "Floating subtitles should measure rendered height and trim the oldest group first."
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
    $translationWorkerStatus = Find-ByAutomationId "OverviewTranslationWorkerStatus"
    $translationApiStatus = Find-ByAutomationId "OverviewTranslationApiStatus"
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
    if ($null -eq $translationWorkerStatus) { throw "Translation worker status was not found in the overview panel." }
    if ($null -eq $translationApiStatus) { throw "Translation API status was not found in the overview panel." }
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
    $translationSettingsGroup = Find-ByAutomationId "SettingsTranslationGroup"
    $diagnosticsSettingsGroup = Find-ByAutomationId "SettingsDiagnosticsGroup"
    $balancedVadMode = Find-ByAutomationId "VadModeBalanced"
    $fixedEndSilenceBox = Find-ByAutomationId "FixedEndSilenceBox"
    $audioDeviceComboBox = Find-ByAutomationId "AudioDeviceComboBox"
    $currentVadEndpointText = Find-ByAutomationId "CurrentVadEndpointText"
    $followDefaultDeviceSwitch = Find-ByAutomationId "FollowDefaultDeviceSwitch"
    $translationTargetLanguageBox = Find-ByAutomationId "TranslationTargetLanguageBox"
    $translationEnabledSwitch = Find-ByAutomationId "TranslationEnabledSwitch"
    $translationSelectionSummaryText = Find-ByAutomationId "TranslationSelectionSummaryText"
    $translationProfileList = Find-ByAutomationId "TranslationProfileList"

    if ($settingsNav.Current.ItemStatus -ne "Active") {
        throw "Navigation active state did not move to the clicked settings page."
    }

    if ($subtitleHistoryNav.Current.ItemStatus -eq "Active") {
        throw "Previous navigation item remained active after switching pages."
    }

    if ($null -eq $audioSettingsGroup) { throw "Settings audio group was not found." }
    if ($null -eq $recognitionSettingsGroup) { throw "Settings recognition service group was not found." }
    if ($null -eq $floatingSettingsGroup) { throw "Settings floating subtitle group was not found." }
    if ($null -eq $translationSettingsGroup) { throw "Settings translation group was not found." }
    if ($null -eq $diagnosticsSettingsGroup) { throw "Settings diagnostics group was not found." }
    if ($null -eq $balancedVadMode) { throw "Balanced adaptive VAD mode was not found." }
    if ($null -eq $fixedEndSilenceBox) { throw "Fixed end-silence input was not found." }
    if ($fixedEndSilenceBox.Current.IsEnabled) { throw "Fixed end-silence input should be disabled in balanced mode." }
    if ($null -eq $audioDeviceComboBox) { throw "Audio-device selector was not found." }
    if ($null -eq $currentVadEndpointText) { throw "Current VAD endpoint status was not found." }
    if ($null -eq $followDefaultDeviceSwitch) { throw "Default-device toggle was not found." }
    if ($null -eq $translationTargetLanguageBox) { throw "Translation target-language control was not found." }
    if ($null -eq $translationEnabledSwitch) { throw "Translation enable toggle was not found." }
    if ($null -eq $translationSelectionSummaryText) { throw "Translation profile status was not found." }
    if ($null -eq $translationProfileList) { throw "Translation profile list was not found." }

    $vadStatusRect = $currentVadEndpointText.Current.BoundingRectangle
    $followDefaultRect = $followDefaultDeviceSwitch.Current.BoundingRectangle
    if ([Math]::Abs($vadStatusRect.Left - $followDefaultRect.Left) -gt 2) {
        throw "Current VAD endpoint status is not aligned with the default-device toggle."
    }

    $targetLanguageRect = $translationTargetLanguageBox.Current.BoundingRectangle
    $audioDeviceRect = $audioDeviceComboBox.Current.BoundingRectangle
    $profileStatusRect = $translationSelectionSummaryText.Current.BoundingRectangle
    $profileListRect = $translationProfileList.Current.BoundingRectangle
    if ($targetLanguageRect.Width -ge ($audioDeviceRect.Width * 0.65)) {
        throw "Translation target-language control is not visibly shorter than a full-width settings field."
    }
    if ($profileStatusRect.Top -le $targetLanguageRect.Bottom -or
        $profileStatusRect.Bottom -gt $profileListRect.Top) {
        throw "Translation profile status is not positioned below the form and above the profile list."
    }

    $addTranslationProfileButton = Find-ByAutomationId "AddTranslationProfileButton"
    Invoke-Element $addTranslationProfileButton "Add translation profile button"
    $protocolNoticeDeadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $translationProtocolNotice = Find-ByAutomationId "TranslationProtocolNotice"
    } while ($null -eq $translationProtocolNotice -and (Get-Date) -lt $protocolNoticeDeadline)
    if ($null -eq $translationProtocolNotice) {
        throw "Translation profile editor did not display the Chat Completions compatibility notice."
    }

    $cancelCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "取消")
    $cancelTranslationProfileButton = $mainElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cancelCondition)
    Invoke-Element $cancelTranslationProfileButton "Cancel translation profile editor button"
    Start-Sleep -Milliseconds 300

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
