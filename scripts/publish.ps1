param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Python = "python",
    [switch]$SkipWorker
)

$ErrorActionPreference = "Stop"

function Assert-LastExitCode {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $root "artifacts\StreamTranslator"
$workerRoot = Join-Path $publishRoot "worker"
$modelsRoot = Join-Path $publishRoot "models"
$pythonRoot = Join-Path $root "python"
$buildVenvRoot = Join-Path $root "artifacts\build-venv"

& (Join-Path $PSScriptRoot "ensure-model.ps1") -ModelPath (Join-Path $root "models\silero_vad.onnx")

# A leftover file that cannot be deleted (e.g. an exe still running) must fail the
# build instead of silently mixing stale artifacts into the new package.
if (Test-Path $publishRoot) {
    Remove-Item -Recurse -Force $publishRoot
}
New-Item -ItemType Directory -Force -Path $publishRoot, $workerRoot, $modelsRoot | Out-Null

dotnet publish (Join-Path $root "src\StreamTranslator.App\StreamTranslator.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $publishRoot
Assert-LastExitCode "dotnet publish StreamTranslator.App"

dotnet publish (Join-Path $root "src\StreamTranslator.Diagnostics\StreamTranslator.Diagnostics.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o (Join-Path $publishRoot "diagnostics")
Assert-LastExitCode "dotnet publish StreamTranslator.Diagnostics"

Copy-Item (Join-Path $root "models\silero_vad.onnx") (Join-Path $modelsRoot "silero_vad.onnx") -ErrorAction Stop
Copy-Item (Join-Path $root "docs") (Join-Path $publishRoot "docs") -Recurse

if (-not $SkipWorker) {
    # Workers are built inside a dedicated venv so the host site-packages neither
    # leak into the frozen exe nor get polluted by build-only dependencies.
    $venvPython = Join-Path $buildVenvRoot "Scripts\python.exe"
    if (-not (Test-Path $venvPython)) {
        & $Python -m venv $buildVenvRoot
        Assert-LastExitCode "python -m venv"
    }

    & $venvPython -m pip install --upgrade pip
    Assert-LastExitCode "pip upgrade"
    & $venvPython -m pip install -r (Join-Path $pythonRoot "requirements-build.txt")
    Assert-LastExitCode "pip install requirements-build.txt"

    & $venvPython -m PyInstaller `
        --onefile `
        --name asr_worker `
        --distpath $workerRoot `
        --workpath (Join-Path $root "artifacts\pyinstaller-work") `
        --specpath (Join-Path $root "artifacts") `
        (Join-Path $pythonRoot "asr_worker.py")
    Assert-LastExitCode "PyInstaller asr_worker"

    & $venvPython -m PyInstaller `
        --onefile `
        --name translation_worker `
        --distpath $workerRoot `
        --workpath (Join-Path $root "artifacts\pyinstaller-translation-work") `
        --specpath (Join-Path $root "artifacts") `
        (Join-Path $pythonRoot "translation_worker.py")
    Assert-LastExitCode "PyInstaller translation_worker"
}

New-Item -ItemType Directory -Force -Path (Join-Path $publishRoot "data") | Out-Null
Write-Host "Portable package: $publishRoot"
