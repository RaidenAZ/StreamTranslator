param(
    [string]$ModelPath = (Join-Path $PSScriptRoot "..\models\silero_vad.onnx"),
    [string]$ModelUri = "https://raw.githubusercontent.com/snakers4/silero-vad/b163605b3f44c3aadf28f97b125a2f7c461e9a7f/src/silero_vad/data/silero_vad.onnx",
    [string]$ExpectedSha256 = "1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3"
)

$ErrorActionPreference = "Stop"
$resolvedModelPath = [System.IO.Path]::GetFullPath($ModelPath)

function Test-ModelHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return [string]::Equals($actual, $ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)
}

if (Test-ModelHash $resolvedModelPath) {
    Write-Host "Silero model verified: $resolvedModelPath"
    return
}

$modelDirectory = Split-Path -Parent $resolvedModelPath
New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null
$temporaryPath = "$resolvedModelPath.download"

try {
    $downloaded = $false
    for ($attempt = 1; $attempt -le 3 -and -not $downloaded; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $ModelUri -OutFile $temporaryPath
            $downloaded = $true
        }
        catch {
            if ($attempt -eq 3) {
                throw
            }

            Start-Sleep -Seconds $attempt
        }
    }

    if (-not (Test-ModelHash $temporaryPath)) {
        $actual = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash
        throw "Silero model checksum mismatch. Expected $ExpectedSha256, got $actual."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $resolvedModelPath -Force
    Write-Host "Silero model downloaded and verified: $resolvedModelPath"
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}
