[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseStage,
    [string]$DownloadedFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedHash = 'b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af'
$assetUrl = 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe'
$temporaryDownload = $null
try {
    New-Item -ItemType Directory -Path $ReleaseStage -Force | Out-Null
    if ([string]::IsNullOrWhiteSpace($DownloadedFile)) {
        $temporaryDownload = Join-Path $ReleaseStage ("PresentMon-2.6.0-download-{0}.exe" -f [guid]::NewGuid())
        Invoke-WebRequest -Uri $assetUrl -OutFile $temporaryDownload
        $DownloadedFile = $temporaryDownload
    }
    if (-not (Test-Path -LiteralPath $DownloadedFile -PathType Leaf)) { throw "PresentMon download is missing: $DownloadedFile" }
    $file = Get-Item -LiteralPath $DownloadedFile
    if ($file.Length -ne 980320) { throw "PresentMon 2.6.0 has unexpected size $($file.Length)." }
    $actualHash = (Get-FileHash -LiteralPath $DownloadedFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) { throw "PresentMon 2.6.0 SHA-256 mismatch: $actualHash" }

    $destinationDirectory = Join-Path $ReleaseStage 'tools/PresentMon'
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $DownloadedFile -Destination (Join-Path $destinationDirectory 'PresentMon.exe') -Force
    Write-Host "Staged official PresentMon 2.6.0 ($actualHash)." -ForegroundColor Green
} finally {
    if ($temporaryDownload -and (Test-Path -LiteralPath $temporaryDownload)) {
        [IO.File]::Delete($temporaryDownload)
    }
}
