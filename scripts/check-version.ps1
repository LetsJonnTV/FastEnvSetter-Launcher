<#
.SYNOPSIS
    Prüft, dass alle Manifeste dieselbe Version tragen. Mit -Tag zusätzlich gegen einen Git-Tag.
#>
param([string] $Tag)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..' | Resolve-Path

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = $props.Project.PropertyGroup.Version
$extension = (Get-Content (Join-Path $root 'extensions/vscode/package.json') -Raw | ConvertFrom-Json).version

if ($extension -ne $version) {
    throw "extensions/vscode/package.json hat $extension, Directory.Build.props hat $version."
}
if ($Tag -and $Tag.TrimStart('v') -ne $version) {
    throw "Tag $Tag passt nicht zur Version $version."
}
if (-not (Select-String -Path (Join-Path $root 'CHANGELOG.md') -Pattern "^## \[$([regex]::Escape($version))\]" -Quiet)) {
    throw "CHANGELOG.md enthält keinen Abschnitt für $version."
}

Write-Output "Version $version ist konsistent."
