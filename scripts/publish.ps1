param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Python = "python",
    [switch]$SkipWorker
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $root "artifacts\StreamTranslator"
$workerRoot = Join-Path $publishRoot "worker"
$modelsRoot = Join-Path $publishRoot "models"
$pythonRoot = Join-Path $root "python"

& (Join-Path $PSScriptRoot "ensure-model.ps1") -ModelPath (Join-Path $root "models\silero_vad.onnx")

Remove-Item -Recurse -Force $publishRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishRoot, $workerRoot, $modelsRoot | Out-Null

dotnet publish (Join-Path $root "src\StreamTranslator.App\StreamTranslator.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $publishRoot

dotnet publish (Join-Path $root "src\StreamTranslator.Diagnostics\StreamTranslator.Diagnostics.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o (Join-Path $publishRoot "diagnostics")

Copy-Item (Join-Path $root "models\silero_vad.onnx") (Join-Path $modelsRoot "silero_vad.onnx") -ErrorAction Stop
Copy-Item (Join-Path $root "docs") (Join-Path $publishRoot "docs") -Recurse

if (-not $SkipWorker) {
    & $Python -m pip install -r (Join-Path $pythonRoot "requirements.txt")
    & $Python -m pip install pyinstaller
    & $Python -m PyInstaller `
        --onefile `
        --name asr_worker `
        --distpath $workerRoot `
        --workpath (Join-Path $root "artifacts\pyinstaller-work") `
        --specpath (Join-Path $root "artifacts") `
        (Join-Path $pythonRoot "asr_worker.py")

    & $Python -m PyInstaller `
        --onefile `
        --name translation_worker `
        --distpath $workerRoot `
        --workpath (Join-Path $root "artifacts\pyinstaller-translation-work") `
        --specpath (Join-Path $root "artifacts") `
        (Join-Path $pythonRoot "translation_worker.py")
}

New-Item -ItemType Directory -Force -Path (Join-Path $publishRoot "data") | Out-Null
Write-Host "Portable package: $publishRoot"
