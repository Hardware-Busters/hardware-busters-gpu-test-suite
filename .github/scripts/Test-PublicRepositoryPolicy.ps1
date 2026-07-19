[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$errors = [Collections.Generic.List[string]]::new()
$email = '(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b'
$private = '(?i)(\b[A-Z]:[\\/]|(?<!\\)\\\\(?!\.)[A-Za-z0-9._-]+\\[A-Za-z0-9.$_-]+|/home/|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[0-1])\.\d{1,3}|gx10-\d+\.local)'
$credential = '(?i)((api[_-]?key|password|secret|credential)\s*[:=]\s*["''](?!["''])[A-Za-z0-9_\-]{8,}|BEGIN (RSA |OPENSSH )?PRIVATE KEY)'
$githubToken = '(?i)\b(gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
$genericToken = '(?i)\btoken\s*[:=]\s*["'']?[A-Za-z0-9_\-]{8,}'
$bearer = '(?i)authorization\s*[:=]?\s*["'']?bearer\s+[A-Za-z0-9._\-]{8,}'
$deniedTerm = '(?i)paypal'
$deniedExtensions = @('.exe', '.dll', '.pdb', '.zip', '.7z', '.rar', '.png', '.jpg', '.jpeg', '.mp4', '.mov', '.csv', '.log', '.db', '.sqlite')

function Assert-PatternRejects {
    param([string]$Pattern, [string[]]$Values, [string]$Name)
    foreach ($value in $Values) { if ($value -notmatch $Pattern) { throw "Scanner adversarial test missed ${Name}: $value" } }
}

function Find-ProfilePrivateValue {
    param($Node)
    if ($Node -is [string]) { if ($Node -match $private) { return $Node }; return $null }
    if ($Node -is [pscustomobject]) { foreach ($property in $Node.psobject.Properties) { if ($property.Name -match $private) { return $property.Name }; $found = Find-ProfilePrivateValue $property.Value; if ($null -ne $found) { return $found } } }
    elseif ($Node -is [System.Collections.IDictionary]) { foreach ($key in $Node.Keys) { if ([string]$key -match $private) { return [string]$key }; $found = Find-ProfilePrivateValue $Node[$key]; if ($null -ne $found) { return $found } } }
    elseif ($Node -is [System.Collections.IEnumerable]) { foreach ($item in $Node) { $found = Find-ProfilePrivateValue $item; if ($null -ne $found) { return $found } } }
    return $null
}

function Test-ProfileJsonScannerRegression {
    if ($null -ne (Find-ProfilePrivateValue ([pscustomobject]@{ config = '%USERPROFILE%\Documents\Game\settings.ini' }))) { throw 'USERPROFILE profile config was incorrectly classified as UNC.' }
    if ($null -eq (Find-ProfilePrivateValue ([pscustomobject]@{ paths = @('\\privatehost\share\file.ini') }))) { throw 'Decoded UNC array value was not rejected.' }
    if ($null -eq (Find-ProfilePrivateValue ([pscustomobject]@{ 'D:\private\operator' = 'safe' }))) { throw 'Decoded private profile key was not rejected.' }
}

# Keep the scanner's negative cases executable, so a regex simplification cannot reopen these leaks.
Assert-PatternRejects $private @('D:\private\result.txt', '\\server\share\result.txt') 'absolute Windows or UNC path'
Assert-PatternRejects $genericToken @('token=abcDEF123456', 'TOKEN: abcDEF123456') 'generic token'
Assert-PatternRejects $bearer @('Authorization: Bearer abcDEF123456') 'Authorization bearer value'
Test-ProfileJsonScannerRegression

# This deliberately scans only Git's tracked index. A prior build may leave bin/ and obj/ files,
# but they are never source-policy inputs and cannot make a clean public checkout fail.
$relativeFiles = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed; policy scans only tracked repository files.' }
if ($relativeFiles.Count -eq 0) { throw 'Policy scan found no tracked files.' }
foreach ($relative in $relativeFiles) {
    $filePath = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { $errors.Add("Tracked file is missing: $relative"); continue }
    $file = Get-Item -LiteralPath $filePath
    if ($deniedExtensions -contains $file.Extension.ToLowerInvariant()) { $errors.Add("Denied binary/artifact extension: $relative"); continue }
    if ($file.Length -gt 2MB) { $errors.Add("Unexpected large file: $relative") }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes -contains 0) { $errors.Add("Binary content: $relative"); continue }
    $content = [Text.Encoding]::UTF8.GetString($bytes)
    if ($relative -ne '.github/scripts/Test-PublicRepositoryPolicy.ps1') {
        if ($content -match $email) { $errors.Add("Email address: $relative") }
        if ($relative -notlike 'profiles/*.json' -and $content -match $private) { $errors.Add("Private host/path/IP: $relative") }
        if ($content -match $credential) { $errors.Add("Credential-like content: $relative") }
        if ($content -match $githubToken) { $errors.Add("GitHub token-like content: $relative") }
        if ($content -match $genericToken) { $errors.Add("Token-like content: $relative") }
        if ($content -match $bearer) { $errors.Add("Authorization bearer content: $relative") }
        if ($content -match $deniedTerm) { $errors.Add("Denied payment-provider reference: $relative") }
    }
    if ($relative -like 'profiles/*.json') {
        try { $privateValue = Find-ProfilePrivateValue ($content | ConvertFrom-Json); if ($null -ne $privateValue) { $errors.Add("Decoded private host/path/IP/key: $relative") } }
        catch { $errors.Add("Invalid profile JSON: $relative") }
    }
}
foreach ($required in @('LICENSE', 'NOTICE', 'LICENSES/CC-BY-4.0.txt', 'docs/LICENSE-POLICY.md', 'settings.example.json', 'profiles/PROVENANCE.md', 'profiles/profile-pack.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required))) { $errors.Add("Missing required policy file: $required") }
}
if ($errors.Count -gt 0) { throw "Public policy check failed:`n - $($errors -join "`n - ")" }
Write-Host 'Public repository policy passed.' -ForegroundColor Green
