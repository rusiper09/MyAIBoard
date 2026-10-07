# Builds MyAIBoard and installs it for the current user:
#   %LOCALAPPDATA%\MyAIBoard\bin\MyAIBoard.exe      (widget)
#   %LOCALAPPDATA%\MyAIBoard\bin\MyAIBoard.Mcp.exe  (MCP server for Claude Code / Codex)
# plus a Start menu shortcut and, unless -NoAutostart, a "run at sign-in" entry.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/install.ps1 [-NoAutostart] [-NoLaunch]
param(
    [switch]$NoAutostart,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:LOCALAPPDATA 'MyAIBoard\bin'
$staging = Join-Path $root 'artifacts\publish'

Write-Host '==> Building' -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
foreach ($project in 'src\MyAIBoard.App', 'src\MyAIBoard.Mcp') {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained false -o $staging -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Write-Host '==> Installing to' $installDir -ForegroundColor Cyan
# Stop the widget (including dev builds); MCP servers are owned by AI sessions and are left running.
Get-Process MyAIBoard, MyAIBoard.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $installDir | Out-Null

# Leftovers from earlier updates whose processes have since exited.
Get-ChildItem $installDir -Recurse -Filter '*.old-*' | Remove-Item -Force -ErrorAction SilentlyContinue

$renamed = 0
foreach ($file in Get-ChildItem $staging -Recurse -File) {
    $target = Join-Path $installDir $file.FullName.Substring($staging.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    try {
        Copy-Item $file.FullName $target -Force
    }
    catch [System.IO.IOException] {
        # In use by a running MCP server: Windows lets us rename it out of the way.
        Rename-Item $target "$(Split-Path $target -Leaf).old-$([DateTime]::Now.Ticks)"
        Copy-Item $file.FullName $target -Force
        $renamed++
    }
}
if ($renamed -gt 0) {
    Write-Host "    $renamed file(s) were in use by running AI sessions; those sessions keep the old version until restarted."
}

$exe = Join-Path $installDir 'MyAIBoard.exe'

Write-Host '==> Start menu shortcut' -ForegroundColor Cyan
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'MyAIBoard.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'AI Board desktop memo widget'
$shortcut.Save()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutostart) {
    Write-Host '==> Autostart skipped (-NoAutostart)' -ForegroundColor Cyan
}
else {
    Write-Host '==> Autostart at sign-in (turn off from the widget menu)' -ForegroundColor Cyan
    Set-ItemProperty -Path $runKey -Name 'MyAIBoard' -Value "`"$exe`""
}

if (-not $NoLaunch) {
    Start-Process $exe
}
Write-Host 'Done.' -ForegroundColor Green
