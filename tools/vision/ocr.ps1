# ocr.ps1 — Windows.Media.Ocr over an image file -> JSON {lines:[{text,x,y,w,h}]}.
# MUST run under Windows PowerShell 5.1 (powershell.exe): WinRT projection is unavailable in pwsh 7.
# Usage: powershell.exe -NoProfile -EncodedCommand <b64 of this with $ImagePath set>, or -File ocr.ps1 -ImagePath <png>
param([string]$ImagePath = $env:OCR_IMG)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq "AsTask" -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq "IAsyncOperation``1" })[0]
function Await($op, $t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); [void]$task.Wait(-1); $task.Result }
[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]

$file    = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
$stream  = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp     = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($null -eq $engine) { '{"error":"no OCR language pack"}'; exit 1 }
$res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])

$lines = @()
foreach ($line in $res.Lines) {
  if ($line.Words.Count -eq 0) { continue }
  $minX = [double]::MaxValue; $minY = [double]::MaxValue; $maxR = 0.0; $maxB = 0.0
  foreach ($w in $line.Words) {
    $r = $w.BoundingRect
    if ($r.X -lt $minX) { $minX = $r.X }
    if ($r.Y -lt $minY) { $minY = $r.Y }
    if (($r.X + $r.Width)  -gt $maxR) { $maxR = $r.X + $r.Width }
    if (($r.Y + $r.Height) -gt $maxB) { $maxB = $r.Y + $r.Height }
  }
  $lines += [pscustomobject]@{
    # Strip raw control chars (Windows OCR sometimes emits e.g. BEL 0x07); PS 5.1 ConvertTo-Json leaves
    # them unescaped, producing JSON that strict parsers reject.
    text = ($line.Text -replace '[\x00-\x1F]', '')
    x = [int]$minX; y = [int]$minY; w = [int]($maxR - $minX); h = [int]($maxB - $minY)
    cx = [int]($minX + ($maxR - $minX) / 2); cy = [int]($minY + ($maxB - $minY) / 2)
  }
}
[pscustomobject]@{ width = $decoder.PixelWidth; height = $decoder.PixelHeight; lines = $lines } | ConvertTo-Json -Depth 5 -Compress
