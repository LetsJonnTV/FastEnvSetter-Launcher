<#
.SYNOPSIS
    Packt einen Plugin-Ordner als .fesplugin-Datei. plugin.json muss direkt im Ordner liegen.
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $Output
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = (Resolve-Path $Path).Path
if (-not (Test-Path (Join-Path $source 'plugin.json'))) { throw "In $source liegt keine plugin.json." }

$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
if (Test-Path $target) { Remove-Item $target -Confirm:$false }
[IO.Compression.ZipFile]::CreateFromDirectory($source, $target)
Write-Output $target
