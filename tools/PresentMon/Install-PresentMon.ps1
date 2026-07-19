[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DownloadedFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$Sha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $DownloadedFile -PathType Leaf)) { throw "Downloaded file was not found: $DownloadedFile" }
$actual = (Get-FileHash -LiteralPath $DownloadedFile -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw 'PresentMon SHA-256 did not match the value verified from the upstream release.' }
$target = Join-Path $PSScriptRoot 'PresentMon.exe'
Copy-Item -LiteralPath $DownloadedFile -Destination $target -Force
Write-Host "Placed locally verified PresentMon at $target. Do not commit this binary." -ForegroundColor Green
