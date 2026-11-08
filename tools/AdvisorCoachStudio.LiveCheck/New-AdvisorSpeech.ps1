<#
.SYNOPSIS
  Creates a 24 kHz, 16-bit, mono PCM WAV of synthetic advisor speech for the live check (Windows only).

.DESCRIPTION
  Uses the Windows speech synthesizer (System.Speech), so run it with Windows PowerShell 5.1. On other systems,
  record or convert your own clip, for example: ffmpeg -i input.wav -ar 24000 -ac 1 -c:a pcm_s16le advisor.wav

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\AdvisorCoachStudio.LiveCheck\New-AdvisorSpeech.ps1 -Text "Hello" -OutFile advisor-1.wav
#>
param(
    [Parameter(Mandatory = $true)] [string] $Text,
    [Parameter(Mandatory = $true)] [string] $OutFile,
    [string] $Voice
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(24000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    if ($Voice) { $synthesizer.SelectVoice($Voice) }
    $synthesizer.SetOutputToWaveFile($path, $format)
    $synthesizer.Speak($Text)
}
finally {
    $synthesizer.Dispose()
}
