#requires -Version 5.1
<#
.SYNOPSIS
  Sinh fixture giọng nói bằng Windows SAPI (offline): mỗi câu một file WAV PCM16 16 kHz mono.
  Chạy bằng Windows PowerShell 5.1 (cần System.Speech): powershell -File fixtures/audio/make-tts-fixtures.ps1
#>
param([string] $OutDir = (Join-Path $PSScriptRoot 'tts'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null

$sentences = [ordered]@{
    'en_1' = 'Today I will explain how to use this feature.'
    'en_2' = 'First, open the settings window and choose the audio source.'
    'en_3' = 'Press start.'
    'en_4' = 'The translated subtitles will appear on top of the video, sentence by sentence, while the original audio keeps playing.'
}

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $synth.SelectVoice('Microsoft Zira Desktop')
    foreach ($k in $sentences.Keys) {
        $path = Join-Path $OutDir "$k.wav"
        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($sentences[$k])
        $synth.SetOutputToNull()
        Write-Host $path
    }
}
finally {
    $synth.Dispose()
}
