# Removes MyAIBoard for the current user: the widget and MCP server, the Start menu shortcut,
# the "run at sign-in" entry, the MCP registrations with Claude Code and Codex, and the note
# install.ps1 added to ~/.codex/AGENTS.md.
# The board (board.db) and settings are kept unless you pass -RemoveData.
#
# Usage: powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-RemoveData]
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mcp-clients.ps1')

$dataDir = Join-Path $env:USERPROFILE '.myaiboard'
$installDir = Join-Path $dataDir 'bin'

Write-Host '==> Stopping MyAIBoard' -ForegroundColor Cyan
# MCP servers started by AI sessions from this install go too; those sessions just lose the board tools.
Get-Process MyAIBoard, MyAIBoard.Mcp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host '==> Start menu shortcut and autostart' -ForegroundColor Cyan
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'MyAIBoard.lnk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MyAIBoard' -ErrorAction SilentlyContinue

Write-Host '==> AI connections (MCP)' -ForegroundColor Cyan
$claude = Find-ClaudeCli
if ($claude) {
    $result = Unregister-Claude $claude
    Write-Host "    Claude Code: $(if ($result.ExitCode -eq 0) { 'disconnected' } else { 'was not connected' })"
}
$codex = Find-CodexCli
if ($codex) {
    $result = Unregister-Codex $codex
    Write-Host "    Codex: $(if ($result.ExitCode -eq 0) { 'disconnected' } else { 'was not connected' })"
}
if (Remove-CodexInstructions) { Write-Host '    Codex: removed the note from AGENTS.md' }

Write-Host '==> Removing files' -ForegroundColor Cyan
$target = if ($RemoveData) { $dataDir } else { $installDir }
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $target) {
    Write-Warning "Some files in $target are still in use; delete the folder after closing your AI sessions."
}
if ($RemoveData) {
    Write-Host '    Board and settings deleted.'
}
elseif (Test-Path $dataDir) {
    Write-Host "    Your board and settings are kept in $dataDir (use -RemoveData to delete them)."
}
Write-Host 'Done.' -ForegroundColor Green
