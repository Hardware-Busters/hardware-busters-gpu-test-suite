[CmdletBinding()]
param([Parameter(Mandatory)][string]$AuditDirectory,[Parameter(Mandatory)][string]$ReleaseStage)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop'
$sbom=Get-Content (Join-Path $AuditDirectory 'sbom.cdx.json') -Raw|ConvertFrom-Json
if($sbom.bomFormat -ne 'CycloneDX' -or $sbom.specVersion -ne '1.5' -or -not $sbom.components){throw 'SBOM is not a valid CycloneDX 1.5 component inventory.'}
$expected=@(Get-ChildItem $ReleaseStage -Filter *.dll -File|? Name -notlike 'GpuSuite.*'|% Name|Sort-Object);$actual=@($sbom.components|%{$_.properties|? name -eq 'hardwarebusters:assembly'|% value}|Sort-Object)
if((Compare-Object $expected $actual)){throw 'SBOM does not completely represent the published non-GpuSuite DLL inventory.'}
foreach($c in $sbom.components){
 if($c.name -match '(?i)(xunit|testhost|test.sdk|gpusuite)'){throw "Test/internal component: $($c.name)"}
 if(-not $c.hashes -or $c.hashes[0].alg -ne 'SHA-256' -or $c.hashes[0].content -notmatch '^[0-9a-f]{64}$' -or -not $c.licenses -or -not $c.externalReferences){throw "Incomplete attribution: $($c.'bom-ref')"}
 $license=$c.licenses[0];$spdx='^(Apache-2\.0|MIT|ISC|Unlicense|BSD-[0-9A-Za-z.\-]+|LGPL-[0-9A-Za-z.\-]+|MPL-[0-9A-Za-z.\-]+)(\s+(AND|OR|WITH)\s+[A-Za-z0-9.\-+]+)*$'
 $expressionProperty=$license.psobject.Properties['expression'];$nameProperty=$license.psobject.Properties['name'];$expression=if($expressionProperty){$expressionProperty.Value}else{''};$licenseName=if($nameProperty){$nameProperty.Value}else{''}
 if($expression -and $expression -notmatch $spdx){throw "Non-SPDX license expression: $($c.'bom-ref')"}
 if(-not $expression -and [string]::IsNullOrWhiteSpace([string]$licenseName)){throw "Missing license choice: $($c.'bom-ref')"}
 foreach($r in $c.externalReferences){if([string]::IsNullOrWhiteSpace([string]$r.url) -or $r.url -match '(?i)(aka\.ms|deprecateLicenseUrl|go\.microsoft\.com/fwlink|webpi/eula)'){throw "Deprecated or missing reference: $($c.'bom-ref')"}}
}
Write-Host "Installer runtime attribution passed: $($expected.Count) shipped dependency assemblies represented." -ForegroundColor Green
