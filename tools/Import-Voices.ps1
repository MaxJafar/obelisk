[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnglishWav,
    [Parameter(Mandatory)][string]$RussianWav,
    [string]$AudioDirectory=(Join-Path $env:LOCALAPPDATA 'Obelisk/audio')
)
$ErrorActionPreference='Stop'
$inputs=@(@{Path=$EnglishWav;Name='jarvis-en.wav'},@{Path=$RussianWav;Name='jarvis-ru.wav'})
foreach($voiceInput in $inputs){
    $path=(Resolve-Path -LiteralPath $voiceInput.Path).ProviderPath
    $file=Get-Item -LiteralPath $path
    if($file.Length -lt 44 -or $file.Length -gt 20MB){throw 'Use a short WAV reference under 20 MiB.'}
    $stream=[IO.File]::OpenRead($path)
    try{$header=[byte[]]::new(12);[void]$stream.Read($header,0,12)}finally{$stream.Dispose()}
    if([Text.Encoding]::ASCII.GetString($header,0,4) -ne 'RIFF' -or [Text.Encoding]::ASCII.GetString($header,8,4) -ne 'WAVE'){throw 'Reference files must be WAV recordings.'}
}
$voices=Join-Path ([IO.Path]::GetFullPath($AudioDirectory)) 'voices'
New-Item -ItemType Directory -Path $voices -Force | Out-Null
foreach($voiceInput in $inputs){Copy-Item -LiteralPath $voiceInput.Path -Destination (Join-Path $voices $voiceInput.Name) -Force}
Write-Output 'English and Russian voice references installed in the private audio directory.'
