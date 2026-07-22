param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot "..\artifacts\StreamTranslator")
)

$ErrorActionPreference = "Stop"

$translationWorker = Join-Path $PackageRoot "worker\translation_worker.exe"
$asrWorker = Join-Path $PackageRoot "worker\asr_worker.exe"
if (-not (Test-Path $translationWorker)) {
    throw "translation_worker.exe was not found at $translationWorker"
}
if (-not (Test-Path $asrWorker)) {
    throw "asr_worker.exe was not found at $asrWorker"
}

$translationInput = @(
    '{"id":"cfg-smoke","type":"configure","profile":{"profileId":"9a7a57da-5c95-4e44-9e3b-54795ae90998","baseUrl":"http://127.0.0.1:8000/v1","model":"smoke-model","apiKey":"","requestCompatibility":"Standard","customExtraBody":{},"timeoutMs":10000,"maxConcurrency":2},"promptVersion":"translation-v1"}',
    '{"id":"shutdown-smoke","type":"shutdown"}'
)
function Invoke-JsonWorker([string]$Executable, [string[]]$Messages) {
    $inputPath = [System.IO.Path]::GetTempFileName()
    $outputPath = [System.IO.Path]::GetTempFileName()
    $errorPath = [System.IO.Path]::GetTempFileName()
    try {
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($inputPath, ($Messages -join "`n") + "`n", $utf8)
        $process = Start-Process `
            -FilePath $Executable `
            -RedirectStandardInput $inputPath `
            -RedirectStandardOutput $outputPath `
            -RedirectStandardError $errorPath `
            -WindowStyle Hidden `
            -PassThru
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "worker did not exit after protocol shutdown: $Executable"
        }
        $process.Refresh()
        $stderr = [System.IO.File]::ReadAllText($errorPath, $utf8)
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            throw "worker wrote stderr during lifecycle smoke: $stderr"
        }
        $stdout = [System.IO.File]::ReadAllText($outputPath, $utf8)
        return @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    finally {
        Remove-Item -LiteralPath $inputPath, $outputPath, $errorPath -Force -ErrorAction SilentlyContinue
    }
}

$translationOutput = Invoke-JsonWorker $translationWorker $translationInput
$translationMessages = @($translationOutput | ForEach-Object { ConvertFrom-Json -InputObject $_ })
if ($translationMessages.Count -ne 3) {
    throw "translation worker lifecycle protocol failed"
}
$translationReadyType = $translationMessages[0].PSObject.Properties["type"].Value
$translationReadyOk = $translationMessages[0].PSObject.Properties["ok"].Value
$translationConfiguredType = $translationMessages[1].PSObject.Properties["type"].Value
$translationConfiguredOk = $translationMessages[1].PSObject.Properties["ok"].Value
$translationShutdownType = $translationMessages[2].PSObject.Properties["type"].Value
$translationShutdownOk = $translationMessages[2].PSObject.Properties["ok"].Value
if ($translationReadyType -ne "ready" -or -not $translationReadyOk) {
    throw "translation worker ready protocol failed"
}
if ($translationConfiguredType -ne "configured" -or -not $translationConfiguredOk) {
    throw "translation worker configure protocol failed"
}
if ($translationShutdownType -ne "shutdown" -or -not $translationShutdownOk) {
    throw "translation worker shutdown protocol failed"
}
$expectedEndpoint = "http://127.0.0.1:8000/v1/chat/completions"
if ($translationMessages[1].PSObject.Properties["finalEndpoint"].Value -ne $expectedEndpoint) {
    throw "translation endpoint mismatch: $($translationMessages[1].finalEndpoint)"
}

$asrOutput = Invoke-JsonWorker $asrWorker @('{"id":"shutdown-smoke","type":"shutdown"}')
$asrMessages = @($asrOutput | ForEach-Object { ConvertFrom-Json -InputObject $_ })
if ($asrMessages.Count -ne 2) {
    throw "ASR worker lifecycle protocol failed"
}
$asrReadyType = $asrMessages[0].PSObject.Properties["type"].Value
$asrReadyOk = $asrMessages[0].PSObject.Properties["ok"].Value
$asrShutdownType = $asrMessages[1].PSObject.Properties["type"].Value
$asrShutdownOk = $asrMessages[1].PSObject.Properties["ok"].Value
if ($asrReadyType -ne "ready" -or -not $asrReadyOk) {
    throw "ASR worker ready protocol failed"
}
if ($asrShutdownType -ne "shutdown" -or -not $asrShutdownOk) {
    throw "ASR worker shutdown protocol failed"
}

Write-Host "PASS translation-worker-smoke ($expectedEndpoint)"
