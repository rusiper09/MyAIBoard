# Helpers shared by install.ps1 and uninstall.ps1: find the Claude Code and Codex CLIs and
# (un)register the MyAIBoard MCP server with them. Dot-source this file.

$McpName = 'myaiboard'
$CodexInstructionsStart = '<!-- myaiboard:start -->'
$CodexInstructionsEnd = '<!-- myaiboard:end -->'

function Find-ClaudeCli {
    $command = Get-Command claude -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Find-CodexCli {
    $command = Get-Command codex -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    # The Codex desktop app ships its CLI here without putting it on PATH.
    $bundled = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin') -Filter codex.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($bundled) { return $bundled.FullName }
    return $null
}

# Runs a CLI without letting its stderr chatter (e.g. "server not found" on remove) stop the script.
function Invoke-Cli([string]$cli, [string[]]$arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $cli @arguments 2>&1
    return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
}

function Register-Claude([string]$cli, [string]$serverExe) {
    Invoke-Cli $cli @('mcp', 'remove', '--scope', 'user', $McpName) | Out-Null
    return Invoke-Cli $cli @('mcp', 'add', '--scope', 'user', $McpName, '--', $serverExe)
}

function Unregister-Claude([string]$cli) {
    return Invoke-Cli $cli @('mcp', 'remove', '--scope', 'user', $McpName)
}

function Register-Codex([string]$cli, [string]$serverExe) {
    Invoke-Cli $cli @('mcp', 'remove', $McpName) | Out-Null
    return Invoke-Cli $cli @('mcp', 'add', $McpName, '--', $serverExe)
}

function Unregister-Codex([string]$cli) {
    return Invoke-Cli $cli @('mcp', 'remove', $McpName)
}

function Get-CodexInstructionsPath {
    $codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    return Join-Path $codexHome 'AGENTS.md'
}

# Codex defers MCP tools until it searches for them, so without a hint it tends to look for
# "aiboard" in files first. This block in its global AGENTS.md points it at the tools.
function Add-CodexInstructions {
    $path = Get-CodexInstructionsPath
    $existing = if (Test-Path $path) { [IO.File]::ReadAllText($path) } else { '' }
    if ($existing.Contains($CodexInstructionsStart)) { return $false }

    $block = @"
$CodexInstructionsStart
# MyAIBoard

MyAIBoard ("aiboard", "AI 보드", "보드") is my desktop memo board. Use the ``$McpName`` MCP tools for it
(``add_memo``, ``list_memos``, ``get_memo``, ``update_memo``, ``delete_memo``, ``list_trash``, ``restore_memo``);
never look for it in files or the database.

- "aiboard에 추가해", "보드에 메모해둬" -> ``add_memo``. Short title; details, file paths and next steps in the body.
- Wrap file paths and URLs in single quotes ('E:\path\file.cs', 'https://...') so they become links.
- The session folder is added as a hashtag automatically; add other #tags only when useful.
- When I say a task on the board is done, delete that memo (it goes to the trash).
$CodexInstructionsEnd
"@
    $separator = if ($existing.Trim()) { "`r`n`r`n" } else { '' }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, $existing.TrimEnd() + $separator + $block + "`r`n", (New-Object Text.UTF8Encoding $false))
    return $true
}

function Remove-CodexInstructions {
    $path = Get-CodexInstructionsPath
    if (-not (Test-Path $path)) { return $false }
    $existing = [IO.File]::ReadAllText($path)
    $pattern = '(\r?\n)*' + [regex]::Escape($CodexInstructionsStart) + '[\s\S]*?' + [regex]::Escape($CodexInstructionsEnd) + '(\r?\n)?'
    if ($existing -notmatch $pattern) { return $false }
    $updated = [regex]::Replace($existing, $pattern, "`r`n").Trim()
    [IO.File]::WriteAllText($path, $(if ($updated) { $updated + "`r`n" } else { '' }), (New-Object Text.UTF8Encoding $false))
    return $true
}

# Yes/no question that also works unattended: -Answer yes/no decides, and without a console
# to ask on, "ask" means no.
function Confirm-Step([string]$question, [string]$answer) {
    if ($answer -eq 'yes') { return $true }
    if ($answer -eq 'no') { return $false }
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) { return $false }
    $reply = Read-Host "$question [Y/n]"
    return ($reply -eq '' -or $reply -match '^(y|yes|예|ㅇ)$')
}
