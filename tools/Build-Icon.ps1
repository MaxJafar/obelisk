$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$source=[Drawing.Image]::FromFile((Join-Path $root 'docs/assets/obelisk-icon.png'))
try {
 $frames=@()
 foreach($size in @(32,48,64,128,256)){
  $bitmap=[Drawing.Bitmap]::new($size,$size)
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $graphics.DrawImage($source,0,0,$size,$size)
  $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
  $frames+=,@{Size=$size;Bytes=$stream.ToArray()}
  $graphics.Dispose();$bitmap.Dispose();$stream.Dispose()
 }
 $directory=Join-Path $root 'tauri/src-tauri/icons';New-Item -ItemType Directory -Path $directory -Force|Out-Null
 $writer=[IO.BinaryWriter]::new([IO.File]::Create((Join-Path $directory 'icon.ico')))
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count);$offset=6+16*$frames.Count
  foreach($frame in $frames){$dimension=if($frame.Size -eq 256){0}else{$frame.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.Bytes.Length}
  foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
 }finally{$writer.Dispose()}
}finally{$source.Dispose()}
Write-Output 'Windows icon packaged from the original generated artwork.'
