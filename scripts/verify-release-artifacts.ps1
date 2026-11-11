<#
.SYNOPSIS
    Prüft die vollständige und unveränderte Dateiliste eines GitHub-Releases.
#>
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [string] $Tag
)
$ErrorActionPreference = 'Stop'

$version = $Tag.TrimStart('v')
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Ungültige Release-Version: $Tag"
}
$root = (Resolve-Path -LiteralPath $Directory).Path
$expected = @(
    "FastEnvSetter-$version-win-x64.exe",
    "FastEnvSetter-$version-win-arm64.exe",
    "FastEnvSetter.Mcp-$version-win-x64.exe",
    "FastEnvSetter.Mcp-$version-win-arm64.exe",
    "FastEnvSetter.Core.$version.nupkg",
    "FastEnvSetter.Plugin.Sdk.$version.nupkg",
    "fastenvsetter-$version.vsix",
    'SHA256SUMS'
) | Sort-Object
$actual = @(Get-ChildItem -LiteralPath $root -File | Select-Object -ExpandProperty Name | Sort-Object)
$difference = Compare-Object -ReferenceObject $expected -DifferenceObject $actual
if ($difference) {
    throw "Unerwartete Release-Dateiliste: $($actual -join ', ')"
}

$checksums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS')) {
    if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})  (?<name>[^/\\]+)$') {
        throw "Ungültiger Eintrag in SHA256SUMS: $line"
    }
    $name = $Matches.name
    if ($checksums.ContainsKey($name)) {
        throw "Doppelter SHA256SUMS-Eintrag: $name"
    }
    $checksums[$name] = $Matches.hash.ToUpperInvariant()
}
$expectedChecksums = @($expected | Where-Object { $_ -ne 'SHA256SUMS' })
$checksumDifference = Compare-Object -ReferenceObject $expectedChecksums -DifferenceObject @($checksums.Keys) -CaseSensitive
if ($checksumDifference) {
    throw 'SHA256SUMS enthält nicht genau alle Release-Artefakte.'
}
foreach ($name in $expectedChecksums) {
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash
    if ($actualHash -ne $checksums[$name]) {
        throw "SHA256-Prüfung fehlgeschlagen: $name"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-ZipEntries([string] $Path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($archive.Entries | Select-Object -ExpandProperty FullName) }
    finally { $archive.Dispose() }
}

$vsixEntries = Read-ZipEntries (Join-Path $root "fastenvsetter-$version.vsix")
foreach ($required in @(
    'extension/package.json', 'extension/changelog.md', 'extension/readme.md',
    'extension/LICENSE.txt', 'extension/dist/extension.js', 'extension/media/icon.png'
)) {
    if ($required -cnotin $vsixEntries) {
        throw "VSIX enthält die erforderliche Datei nicht: $required"
    }
}

foreach ($package in @("FastEnvSetter.Core.$version.nupkg", "FastEnvSetter.Plugin.Sdk.$version.nupkg")) {
    $entries = Read-ZipEntries (Join-Path $root $package)
    if (@($entries | Where-Object { $_ -match '\.nuspec$' }).Count -ne 1 -or
        @($entries | Where-Object { $_ -match '^lib/[^/]+/.+\.dll$' }).Count -eq 0) {
        throw "NuGet-Paket enthält kein gültiges Manifest und keine Library: $package"
    }
}

foreach ($runtime in @('win-x64', 'win-arm64')) {
    $expectedMachine = if ($runtime -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    foreach ($name in @("FastEnvSetter-$version-$runtime.exe", "FastEnvSetter.Mcp-$version-$runtime.exe")) {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $root $name))
        if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            throw "Ungültige Windows-Executable: $name"
        }
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
            $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
            [BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne $expectedMachine) {
            throw "Executable-Architektur stimmt nicht mit $runtime überein: $name"
        }
    }
}

Write-Output "Release-Artefakte für v$version geprüft: $($expected.Count) Dateien, Hashes, VSIX, NuGet-Pakete und PE-Architekturen sind konsistent."