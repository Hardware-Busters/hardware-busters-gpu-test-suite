[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseStage, [Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path -LiteralPath $ReleaseStage).Path
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$depsFile = @(Get-ChildItem $stage -Filter *.deps.json -File | Select-Object -First 1)
if ($depsFile.Count -ne 1) { throw 'Published release stage must contain exactly one .deps.json file.' }
$deps = Get-Content $depsFile[0].FullName -Raw | ConvertFrom-Json
$target = $deps.targets.psobject.Properties | Where-Object Name -match '/win-x64$' | Select-Object -First 1
if (-not $target) { throw 'Published dependency manifest has no win-x64 graph.' }
$assemblyPackages = @{}
foreach ($entry in $target.Value.psobject.Properties) {
    $library = $deps.libraries.psobject.Properties[$entry.Name]
    if (-not $library -or $library.Value.type -ne 'package') { continue }
    foreach ($kind in @('runtime','runtimeTargets','native')) {
        $assets = $entry.Value.psobject.Properties[$kind]
        if ($assets) { foreach ($asset in $assets.Value.psobject.Properties.Name) { $assemblyPackages[(Split-Path $asset -Leaf)] = $entry.Name } }
    }
}
function Get-PackageAttribution([string]$Package) {
    $parts = $Package -split '/', 2; $name = $parts[0]; $version = $parts[1]
    $dir = Join-Path $cache ("$($name.ToLowerInvariant())/$($version.ToLowerInvariant())")
    $nuspec = @(Get-ChildItem $dir -Filter *.nuspec -File -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($nuspec.Count -ne 1) { throw "Cannot find package metadata for shipped assembly package $Package." }
    [xml]$xml = Get-Content $nuspec[0].FullName -Raw; $meta = $xml.package.metadata
    $licenseNode = $meta.SelectSingleNode('*[local-name()="license"]'); $licenseUrlNode = $meta.SelectSingleNode('*[local-name()="licenseUrl"]')
    $copyrightNode = $meta.SelectSingleNode('*[local-name()="copyright"]')
    $licenseText = if ($licenseNode) { $licenseNode.InnerText.Trim() } else { '' }
    $licenseUrl = if ($licenseUrlNode) { $licenseUrlNode.InnerText.Trim() } else { '' }
    if ($name -in @('Microsoft.Extensions.Logging.Abstractions','Mono.Posix.NETStandard')) { $licenseText = 'Microsoft .NET Library EULA'; $licenseUrl = '' }
    if ([string]::IsNullOrWhiteSpace($licenseText) -and [string]::IsNullOrWhiteSpace($licenseUrl)) { throw "Package $Package has no usable license metadata; release is blocked." }
    $licenseFiles = @(Get-ChildItem $dir -Recurse -File -Include LICENSE*,COPYING*,NOTICE* -ErrorAction SilentlyContinue)
    $isFileLicense = $licenseText -match '^[^/\\]+\.(txt|md)$' -and $licenseFiles
    if ($isFileLicense) { $licenseText = "$name package license ($licenseText)"; $licenseUrl = '' }
    [pscustomobject]@{ Name=$name; Version=$version; License=$licenseText; LicenseUrl=$licenseUrl; Copyright=$(if($copyrightNode){$copyrightNode.InnerText.Trim()}else{'Not supplied by package metadata.'}); LicenseFiles=$licenseFiles }
}
$runtime = [pscustomobject]@{ Name='Microsoft .NET Runtime'; Version='9.0'; License='MIT'; LicenseUrl='https://github.com/dotnet/runtime/blob/main/LICENSE.TXT'; Copyright='Copyright (c) .NET Foundation and Contributors.'; LicenseFiles=@() }
$licenseDir = Join-Path $OutputDirectory 'licenses'; New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
$components = [Collections.Generic.List[object]]::new(); $notice = @('# Third-party runtime notices', '', 'This inventory covers every non-GpuSuite DLL shipped in the self-contained installer stage. Each line names the shipped assembly and its SHA-256.', '')
$files = @(Get-ChildItem $stage -Filter *.dll -File | Where-Object Name -notlike 'GpuSuite.*' | Sort-Object Name)
if ($files.Count -eq 0) { throw 'Published stage has no non-GpuSuite dependency assemblies.' }
foreach ($file in $files) {
    $attr = if ($assemblyPackages.ContainsKey($file.Name)) { Get-PackageAttribution $assemblyPackages[$file.Name] } else { $runtime }
    if ($file.Name -match '(?i)(xunit|testhost)' -or $attr.Name -match '(?i)(xunit|test)') { throw "Test component leaked into installer stage: $($file.Name)" }
    $licenseRef = $attr.LicenseUrl
    if ($licenseRef -match '(?i)(aka\.ms|deprecateLicenseUrl|go\.microsoft\.com/fwlink|webpi/eula)') { $licenseRef = '' }
    if ([string]::IsNullOrWhiteSpace($licenseRef)) { $licenseRef = "https://www.nuget.org/packages/$($attr.Name)/$($attr.Version)" }
    if ([string]::IsNullOrWhiteSpace($licenseRef)) { throw "No approved license reference for shipped assembly $($file.Name)." }
    foreach ($licenseFile in $attr.LicenseFiles) { $dest = Join-Path $licenseDir ("$($attr.Name)-$($attr.Version)-$($licenseFile.Name)"); if(-not(Test-Path $dest)){Copy-Item $licenseFile.FullName $dest} }
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $spdx = '^(Apache-2\.0|MIT|ISC|Unlicense|BSD-[0-9A-Za-z.\-]+|LGPL-[0-9A-Za-z.\-]+|MPL-[0-9A-Za-z.\-]+)(\s+(AND|OR|WITH)\s+[A-Za-z0-9.\-+]+)*$'
    $licenseObject = if ($attr.License -match $spdx) { @{ expression = $attr.License } } else { @{ name = $attr.License } }
    $component = [ordered]@{ type='library'; 'bom-ref'="pkg:runtime/$($file.Name)"; name=$attr.Name; version=$attr.Version; hashes=@(@{alg='SHA-256';content=$hash}); licenses=@($licenseObject); copyright=$attr.Copyright; externalReferences=@(@{type='distribution';url=$licenseRef}); properties=@(@{name='hardwarebusters:assembly';value=$file.Name}) }
    $components.Add($component)
    $notice += "- $($file.Name): $($attr.Name) $($attr.Version); SHA-256 $hash; license $($attr.License); $licenseRef"
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md'), ($notice -join "`n"), [Text.UTF8Encoding]::new($false))
$sbom=[ordered]@{bomFormat='CycloneDX';specVersion='1.5';serialNumber="urn:uuid:$([guid]::NewGuid())";version=1;metadata=@{component=@{type='application';name='hardware-busters-gpu-test-suite';version='release'}};components=@($components)}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'sbom.cdx.json'), ($sbom|ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
