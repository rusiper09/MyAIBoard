# Installs MyAIBoard for the current user:
#   %USERPROFILE%\.myaiboard\bin\MyAIBoard.exe      (widget)
#   %USERPROFILE%\.myaiboard\bin\MyAIBoard.Mcp.exe  (MCP server for Claude Code / Codex)
# plus a Start menu shortcut, unless -NoAutostart a "run at sign-in" entry, and optionally the
# MCP registration with Claude Code and Codex.
# The board itself lives next to it (%USERPROFILE%\.myaiboard\board.db). Not AppData: Store-packaged
# apps such as the Codex desktop app, and the MCP servers they start, only see a virtualized AppData.
#
# Works in two places:
#   - In a release zip (bin\ next to this script): installs those files; no .NET needed.
#   - In the source tree (scripts\install.ps1): builds first; needs the .NET 10 SDK.
#
# Usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-NoAutostart] [-NoLaunch] [-Mcp ask|yes|no]
#   -Mcp: register the MCP server with Claude Code / Codex (ask = prompt; without a console, no).
param(
    [switch]$NoAutostart,
    [switch]$NoLaunch,
    [ValidateSet('ask', 'yes', 'no')]
    [string]$Mcp = 'ask'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.ps1')

$dataDir = Join-Path $env:USERPROFILE '.myaiboard'
$installDir = Join-Path $dataDir 'bin'
$legacyDir = Join-Path $env:LOCALAPPDATA 'MyAIBoard'

$packaged = Join-Path $PSScriptRoot 'bin'
if (Test-Path (Join-Path $packaged 'MyAIBoard.exe')) {
    $staging = $packaged
    # Files from a downloaded zip carry the "from the internet" mark, which makes Windows warn on every start.
    Get-ChildItem $staging -Recurse -File | Unblock-File
}
else {
    $root = Split-Path $PSScriptRoot -Parent
    $staging = Join-Path $root 'artifacts\publish'
    Write-Host '==> Building' -ForegroundColor Cyan
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    foreach ($project in 'src\MyAIBoard.App', 'src\MyAIBoard.Mcp') {
        dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained false -o $staging -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    }
}

Write-Host '==> Installing to' $installDir -ForegroundColor Cyan
# Stop the widget (including dev builds); MCP servers are owned by AI sessions and are left running.
Get-Process MyAIBoard, MyAIBoard.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $installDir | Out-Null

# Earlier versions kept everything in %LOCALAPPDATA%\MyAIBoard. Copy the board and settings over
# once (the WAL file holds the latest writes); the old folder is left for you to delete.
if ((Test-Path (Join-Path $legacyDir 'board.db')) -and -not (Test-Path (Join-Path $dataDir 'board.db'))) {
    Write-Host '==> Moving the board from' $legacyDir -ForegroundColor Cyan
    foreach ($name in 'board.db', 'board.db-wal', 'settings.json') {
        $source = Join-Path $legacyDir $name
        if (Test-Path $source) { Copy-Item $source (Join-Path $dataDir $name) }
    }
}

# Leftovers from earlier updates whose processes have since exited, and files the new version no
# longer ships (e.g. after switching between framework-dependent and self-contained builds).
Get-ChildItem $installDir -Recurse -Filter '*.old-*' | Remove-Item -Force -ErrorAction SilentlyContinue
$shipped = @{}
Get-ChildItem $staging -Recurse -File | ForEach-Object { $shipped[$_.FullName.Substring($staging.Length + 1)] = $true }
Get-ChildItem $installDir -Recurse -File |
    Where-Object { -not $shipped.ContainsKey($_.FullName.Substring($installDir.Length + 1)) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

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
$serverExe = Join-Path $installDir 'MyAIBoard.Mcp.exe'

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
    # Leave the choice alone, but keep an existing entry pointing at this install.
    if (Get-ItemProperty -Path $runKey -Name 'MyAIBoard' -ErrorAction SilentlyContinue) {
        Set-ItemProperty -Path $runKey -Name 'MyAIBoard' -Value "`"$exe`""
        Write-Host '==> Autostart kept (path updated)' -ForegroundColor Cyan
    }
    else {
        Write-Host '==> Autostart skipped (-NoAutostart)' -ForegroundColor Cyan
    }
}
else {
    Write-Host '==> Autostart at sign-in (turn off from the widget menu)' -ForegroundColor Cyan
    Set-ItemProperty -Path $runKey -Name 'MyAIBoard' -Value "`"$exe`""
}

Write-Host '==> AI connections (MCP)' -ForegroundColor Cyan
$claude = Find-ClaudeCli
if (-not $claude) {
    Write-Host '    Claude Code: not found (see README to connect it later)'
}
elseif (Confirm-Step '    Connect Claude Code to the board?' $Mcp) {
    $result = Register-Claude $claude $serverExe
    if ($result.ExitCode -eq 0) { Write-Host '    Claude Code: connected (new sessions)' }
    else { Write-Warning "Claude Code registration failed: $($result.Output)" }
}
else {
    Write-Host '    Claude Code: skipped'
}

$codex = Find-CodexCli
if (-not $codex) {
    Write-Host '    Codex: not found (see README to connect it later)'
}
elseif (Confirm-Step '    Connect Codex to the board (and add a short note to ~/.codex/AGENTS.md)?' $Mcp) {
    $result = Register-Codex $codex $serverExe
    if ($result.ExitCode -eq 0) {
        $added = Add-CodexInstructions
        Write-Host "    Codex: connected (restart the Codex app)$(if ($added) { '; AGENTS.md note added' })"
    }
    else { Write-Warning "Codex registration failed: $($result.Output)" }
}
else {
    Write-Host '    Codex: skipped'
}

if (-not $NoLaunch) {
    Start-Process $exe
}
Write-Host 'Done.' -ForegroundColor Green
