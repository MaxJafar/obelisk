[CmdletBinding()]
param([string]$AudioDirectory=(Join-Path $env:LOCALAPPDATA 'Obelisk/audio'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$AudioDirectory=[IO.Path]::GetFullPath($AudioDirectory)
foreach($name in @('downloads','runtime','models','voices','data')){New-Item -ItemType Directory -Path (Join-Path $AudioDirectory $name) -Force | Out-Null}
function Get-VerifiedFile([string]$Uri,[string]$Destination,[string]$Hash){
    if((Test-Path -LiteralPath $Destination) -and (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Hash){return}
    $partial=$Destination+'.partial'
    Invoke-WebRequest -Uri $Uri -OutFile $partial
    if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash){throw 'Download checksum mismatch. The partial file was not installed.'}
    Move-Item -LiteralPath $partial -Destination $Destination -Force
}
$runtime=Get-Content -LiteralPath (Join-Path $root 'audio-engine/runtime-manifest.json') -Raw | ConvertFrom-Json
foreach($asset in $runtime){
    $archive=Join-Path $AudioDirectory ('downloads/'+$asset.name)
    Get-VerifiedFile $asset.browser_download_url $archive ($asset.digest -replace '^sha256:','')
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $AudioDirectory 'runtime') -Force
}
$manifest=Get-Content -LiteralPath (Join-Path $root 'audio-engine/model-manifest.json') -Raw | ConvertFrom-Json
foreach($asset in $manifest.files){
    $url='https://huggingface.co/ggml-org/Qwen3-TTS-12Hz-1.7B-Base-GGUF/resolve/'+$manifest.sha+'/'+$asset.rfilename
    Get-VerifiedFile $url (Join-Path $AudioDirectory ('models/'+$asset.rfilename)) $asset.lfs.sha256
}
Write-Output 'Pinned runtime and model files verified. Add your two authorized WAV references with Import-Voices.ps1.'
