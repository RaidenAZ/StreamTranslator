param(
    [string]$SamplesPath = (Join-Path $PSScriptRoot "translation-samples.jsonl"),
    [string]$SettingsPath = (Join-Path $PSScriptRoot "..\data\settings.json"),
    [string]$ProfileId,
    [string]$Python = "python",
    [switch]$AllowLiveApi
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$output = Join-Path $root "data\translation-evaluation\$timestamp"
$evaluator = Join-Path $root "python\translation_evaluate.py"

if (-not (Test-Path -LiteralPath $SamplesPath)) {
    throw "Translation sample set was not found: $SamplesPath"
}

$arguments = @(
    $evaluator,
    "--samples", (Resolve-Path -LiteralPath $SamplesPath),
    "--output", $output
)

if ($AllowLiveApi) {
    if (-not (Test-Path -LiteralPath $SettingsPath)) {
        throw "Settings file was not found: $SettingsPath"
    }
    if ([string]::IsNullOrWhiteSpace($env:STREAMTRANSLATOR_TRANSLATION_API_KEY)) {
        throw "Set STREAMTRANSLATOR_TRANSLATION_API_KEY in this PowerShell session before using -AllowLiveApi."
    }
    $arguments += @("--allow-live-api", "--settings", (Resolve-Path -LiteralPath $SettingsPath))
    if (-not [string]::IsNullOrWhiteSpace($ProfileId)) {
        $arguments += @("--profile-id", $ProfileId)
    }
}

& $Python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Translation evaluation failed with exit code $LASTEXITCODE. Results: $output"
}

Write-Host "Translation evaluation: $output"
