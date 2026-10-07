# Builds the release zip: artifacts\release\MyAIBoard-<version>-win-x64.zip
# The programs are self-contained (the .NET runtime is included), so users need nothing else.
# The version comes from Directory.Build.props.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$name = "MyAIBoard-$version-win-x64"
$releaseDir = Join-Path $root 'artifacts\release'
$packageDir = Join-Path $releaseDir $name
$zip = Join-Path $releaseDir "$name.zip"

Write-Host "==> Building $name" -ForegroundColor Cyan
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
foreach ($project in 'src\MyAIBoard.App', 'src\MyAIBoard.Mcp') {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -o (Join-Path $packageDir 'bin') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

foreach ($file in 'scripts\install.ps1', 'scripts\uninstall.ps1', 'scripts\mcp-clients.ps1', 'README.md', 'CHANGELOG.md', 'LICENSE') {
    $source = Join-Path $root $file
    if (Test-Path $source) { Copy-Item $source $packageDir }
}

Write-Host '==> Zipping' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)" -ForegroundColor Green
