param(
    [Parameter(Mandatory = $true)]
    [string[]]$InputFiles,
    [string]$PackageRoot,
    [string]$OutputRoot,
    [switch]$EnforceAutomatedGates
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $scriptRoot "..\artifacts\StreamTranslator"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot "..\data\adaptive-vad-evaluation"
}
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$diagnosticsExe = Join-Path $PackageRoot "diagnostics\StreamTranslator.Diagnostics.exe"
if (-not (Test-Path -LiteralPath $diagnosticsExe)) {
    throw "Diagnostics executable was not found at $diagnosticsExe"
}

$resolvedInputs = @(foreach ($inputFile in $InputFiles) {
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) {
        throw "Evaluation input was not found: $inputFile"
    }

    (Resolve-Path -LiteralPath $inputFile).Path
})

$sessionRoot = Join-Path $OutputRoot (Get-Date -Format "yyyyMMdd-HHmmss")
New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null

function Invoke-EvaluationRun([string]$mode, [string]$inputFile, [int]$index) {
    $output = Join-Path $sessionRoot ("{0}-{1:000}" -f $mode.ToLowerInvariant(), $index)
    & $diagnosticsExe segment `
        --input $inputFile `
        --output $output `
        --endpoint-mode $mode `
        --end-silence-ms 300
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostics failed for $inputFile in $mode mode with exit code $LASTEXITCODE."
    }

    $metricsFile = Get-ChildItem -LiteralPath (Join-Path $output "sessions") -Filter "*.metrics.json" |
        Select-Object -First 1
    $metrics = Get-Content -LiteralPath $metricsFile.FullName -Raw | ConvertFrom-Json
    $endpoints = Get-Content (Join-Path $output "vad\*.vad.jsonl") |
        ForEach-Object { ($_ | ConvertFrom-Json).effectiveEndSilenceMs }

    [PSCustomObject]@{
        Mode = $mode
        Input = $inputFile
        Output = $output
        Metrics = $metrics
        Endpoints = @($endpoints)
    }
}

function Get-Percentile([int[]]$values, [double]$percentile) {
    if ($values.Count -eq 0) {
        return 0
    }

    $ordered = @($values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($ordered.Count * $percentile) - 1)
    return $ordered[$index]
}

$fixedRuns = @()
$balancedRuns = @()
for ($index = 0; $index -lt $resolvedInputs.Count; $index++) {
    $fixedRuns += Invoke-EvaluationRun "Fixed" $resolvedInputs[$index] $index
    $balancedRuns += Invoke-EvaluationRun "Balanced" $resolvedInputs[$index] $index
}

$fixedQuickResumes = ($fixedRuns | ForEach-Object { $_.Metrics.suspectedPrematureCutCount } | Measure-Object -Sum).Sum
$balancedQuickResumes = ($balancedRuns | ForEach-Object { $_.Metrics.suspectedPrematureCutCount } | Measure-Object -Sum).Sum
$fixedDurationMs = ($fixedRuns | ForEach-Object { $_.Metrics.totalAudioDurationMs } | Measure-Object -Sum).Sum
$balancedDurationMs = ($balancedRuns | ForEach-Object { $_.Metrics.totalAudioDurationMs } | Measure-Object -Sum).Sum
$fixedRate = if ($fixedDurationMs -eq 0) { 0 } else { $fixedQuickResumes * 60000 / $fixedDurationMs }
$balancedRate = if ($balancedDurationMs -eq 0) { 0 } else { $balancedQuickResumes * 60000 / $balancedDurationMs }
$reduction = if ($fixedRate -eq 0) { $null } else { 1 - ($balancedRate / $fixedRate) }
$balancedEndpoints = @($balancedRuns | ForEach-Object { $_.Endpoints })
$balancedMedian = Get-Percentile $balancedEndpoints 0.5
$balancedP95 = Get-Percentile $balancedEndpoints 0.95
$adjustmentCount = ($balancedRuns | ForEach-Object { $_.Metrics.endpointAdjustmentCount } | Measure-Object -Sum).Sum

$result = [PSCustomObject]@{
    InputCount = $resolvedInputs.Count
    OutputRoot = $sessionRoot
    FixedPrematureCutsPerMinute = $fixedRate
    BalancedPrematureCutsPerMinute = $balancedRate
    PrematureCutReduction = $reduction
    BalancedEndpointMedianMs = $balancedMedian
    BalancedEndpointP95Ms = $balancedP95
    BalancedAdjustmentCount = $adjustmentCount
    AutomatedGates = [PSCustomObject]@{
        PrematureCutReductionAtLeast30Percent = if ($null -eq $reduction) { $balancedRate -eq 0 } else { $reduction -ge 0.30 }
        BalancedMedianAtMost450Ms = $balancedMedian -le 450
        BalancedP95AtMost600Ms = $balancedP95 -le 600
    }
    ManualGate = "Manual review: incorrect subtitle merge rate must be below 5%."
}

$result | ConvertTo-Json -Depth 5

if ($EnforceAutomatedGates -and
    (-not $result.AutomatedGates.PrematureCutReductionAtLeast30Percent -or
     -not $result.AutomatedGates.BalancedMedianAtMost450Ms -or
     -not $result.AutomatedGates.BalancedP95AtMost600Ms)) {
    throw "Adaptive VAD automated acceptance gates were not met."
}
