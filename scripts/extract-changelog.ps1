<#
.SYNOPSIS
    Gibt die Release Notes einer Version aus CHANGELOG.md aus.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Path = (Join-Path $PSScriptRoot '..\CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'

$version = $Version.TrimStart('v')
$lines = Get-Content $Path
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## \[$([regex]::Escape($version))\]") { $start = $i + 1; break }
}
if ($start -lt 0) { throw "Kein Abschnitt für Version $version in $Path." }

$section = for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[' -or $lines[$i] -match '^\[[^\]]+\]:\s') { break }
    $lines[$i]
}

($section -join "`n").Trim()
