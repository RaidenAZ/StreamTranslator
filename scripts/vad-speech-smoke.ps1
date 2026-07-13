param(
    [string]$PackageRoot = (Join-Path $PSScriptRoot "..\artifacts\StreamTranslator")
)

$ErrorActionPreference = "Stop"
$diagnosticsExe = Join-Path $PackageRoot "diagnostics\StreamTranslator.Diagnostics.exe"
if (-not (Test-Path -LiteralPath $diagnosticsExe)) {
    throw "Diagnostics executable was not found at $diagnosticsExe"
}

Add-Type -AssemblyName System.Speech
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("stream-translator-vad-" + [Guid]::NewGuid().ToString("N"))
$wavPath = Join-Path $temporaryRoot "speech.wav"
$outputPath = Join-Path $temporaryRoot "output"
New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

try {
    $synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $voice = $synthesizer.GetInstalledVoices() |
            Where-Object Enabled |
            Sort-Object @{ Expression = { if ($_.VoiceInfo.Culture.Name -eq "en-US") { 0 } else { 1 } } } |
            Select-Object -First 1
        if ($null -eq $voice) {
            throw "No enabled Windows speech synthesis voice is installed."
        }

        $synthesizer.SelectVoice($voice.VoiceInfo.Name)
        $synthesizer.SetOutputToWaveFile($wavPath)
        $text = "Welcome to the live caption application. This sentence verifies speech detection and segmentation."
        $synthesizer.Speak($text)
    }
    finally {
        $synthesizer.Dispose()
    }

    & $diagnosticsExe segment --input $wavPath --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostics process failed with exit code $LASTEXITCODE."
    }

    $sessionFile = Get-ChildItem -LiteralPath (Join-Path $outputPath "sessions") -Filter "session-*.json" |
        Where-Object Name -NotLike "*.metrics.json" |
        Select-Object -First 1
    $session = Get-Content -LiteralPath $sessionFile.FullName -Raw | ConvertFrom-Json
    $vadRecords = Get-Content (Join-Path $outputPath "vad\*.vad.jsonl") | ForEach-Object { $_ | ConvertFrom-Json }
    $maxProbability = ($vadRecords | Measure-Object -Property probability -Maximum).Maximum

    if ($session.segmentCount -lt 1) {
        throw "Silero VAD did not produce a speech segment from Windows TTS audio."
    }
    if ($maxProbability -lt 0.5) {
        throw "Silero VAD maximum probability was only $maxProbability."
    }

    "PASS vad-speech-smoke segments=$($session.segmentCount) maxProbability=$maxProbability voice=$($voice.VoiceInfo.Name)"
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
