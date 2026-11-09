<#
.SYNOPSIS
    Baut die eigenständigen Windows-Executables, das Plugin-SDK und die VS Code Extension nach -Output.
#>
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string[]] $Runtimes = @('win-x64', 'win-arm64')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = (Join-Path $PSScriptRoot '..' | Resolve-Path).Path
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = $props.Project.PropertyGroup.Version

New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path

$projects = @(
    @{ Path = 'src/FastEnvSetter.App/FastEnvSetter.App.csproj'; Name = 'FastEnvSetter' },
    @{ Path = 'src/FastEnvSetter.Mcp/FastEnvSetter.Mcp.csproj'; Name = 'FastEnvSetter.Mcp' }
)

foreach ($runtime in $Runtimes) {
    foreach ($project in $projects) {
        $publish = Join-Path $Output "publish-$runtime-$($project.Name)"
        dotnet publish (Join-Path $root $project.Path) -c Release -r $runtime --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish --nologo -v q
        Copy-Item (Join-Path $publish "$($project.Name).exe") (Join-Path $Output "$($project.Name)-$version-$runtime.exe")
        Remove-Item $publish -Recurse -Force -Confirm:$false
    }
}

foreach ($package in 'FastEnvSetter.Core', 'FastEnvSetter.Plugin.Sdk') {
    dotnet pack (Join-Path $root "src/$package/$package.csproj") -c Release -o $Output --nologo -v q
}

Push-Location (Join-Path $root 'extensions/vscode')
try {
    npm ci --no-fund --no-audit
    npm run package
    Move-Item -Force "fastenvsetter-$version.vsix" (Join-Path $Output "fastenvsetter-$version.vsix")
}
finally {
    Pop-Location
}

Get-ChildItem $Output -File | Where-Object Name -NE 'SHA256SUMS' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $Output 'SHA256SUMS')
