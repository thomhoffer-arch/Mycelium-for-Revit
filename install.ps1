<#
.SYNOPSIS
  Build, deploy, and register the Loam Revit Connector.

.DESCRIPTION
  One-shot installer: builds for a given Revit version, copies the artefacts
  into Revit's add-in folder, and registers the MCP listener in Claude
  Desktop's config and (when the CLI is on PATH) Claude Code's config so the
  tools are immediately available to both clients.

.PARAMETER RevitVersion
  Revit major version to target. 2024 -> net48; 2025/2026 -> net8.0-windows.

.PARAMETER RevitApiDir
  Optional override for the Revit install path (where RevitAPI.dll lives).

.PARAMETER Url
  URL Claude clients dial. Defaults to http://127.0.0.1:47100/mcp.

.PARAMETER Name
  MCP server entry name registered in Claude configs.
  Defaults to 'loam-revit'.

.PARAMETER SkipBuild
  Skip the dotnet build step (deploy existing output only).

.PARAMETER SkipDeploy
  Skip copying to Revit's add-in folder (build + Claude registration only).

.PARAMETER SkipClaude
  Skip Claude MCP registration (build + Revit deploy only).

.EXAMPLE
  .\install.ps1 -RevitVersion 2025
#>
[CmdletBinding()]
param(
    [ValidateSet('2024','2025','2026')]
    [string]$RevitVersion = '2025',
    [string]$RevitApiDir,
    [string]$Url = 'http://127.0.0.1:47100/mcp',
    [string]$Name = 'loam-revit',
    [switch]$SkipBuild,
    [switch]$SkipDeploy,
    [switch]$SkipClaude
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo

$tfm = if ($RevitVersion -eq '2024') { 'net48' } else { 'net8.0-windows' }

# ── Build ────────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "==> Building for Revit $RevitVersion ($tfm)..." -ForegroundColor Cyan
    $args = @('build', '.\LoamRevitConnector.sln', '-c', 'Release', '-f', $tfm, "-p:RevitVersion=$RevitVersion")
    if ($RevitApiDir) { $args += "-p:RevitApiDir=$RevitApiDir" }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}

$buildDir = Join-Path $repo "src\bin\Release\$tfm"
if (-not (Test-Path (Join-Path $buildDir 'LoamRevitConnector.dll'))) {
    throw "Build output not found at $buildDir. Run without -SkipBuild first."
}

# ── Revit add-in deploy ──────────────────────────────────────────────────────
if (-not $SkipDeploy) {
    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    Write-Host "==> Deploying to $addinDir" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $addinDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $buildDir '*') $addinDir
}

# ── Claude MCP registration ──────────────────────────────────────────────────
if (-not $SkipClaude) {
    Write-Host "==> Registering MCP server '$Name' -> $Url" -ForegroundColor Cyan

    # 1) Claude Desktop — edit %APPDATA%\Claude\claude_desktop_config.json
    $desktopDir  = Join-Path $env:APPDATA 'Claude'
    $desktopPath = Join-Path $desktopDir 'claude_desktop_config.json'
    if (Test-Path $desktopDir) {
        $cfg = if (Test-Path $desktopPath) {
            Get-Content $desktopPath -Raw | ConvertFrom-Json
        } else {
            [PSCustomObject]@{}
        }
        if (-not $cfg.PSObject.Properties['mcpServers']) {
            $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([PSCustomObject]@{})
        }
        $entry = [PSCustomObject]@{ type = 'http'; url = $Url }
        if ($cfg.mcpServers.PSObject.Properties[$Name]) {
            $cfg.mcpServers.$Name = $entry
        } else {
            $cfg.mcpServers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry
        }
        $cfg | ConvertTo-Json -Depth 10 | Set-Content -Path $desktopPath -Encoding UTF8
        Write-Host "    Claude Desktop: $desktopPath" -ForegroundColor Green
    } else {
        Write-Host "    Claude Desktop config dir not found — skipping ($desktopDir)" -ForegroundColor Yellow
    }

    # 2) Claude Code — use the CLI when it's on PATH (handles user/project scope).
    $claudeCli = Get-Command claude -ErrorAction SilentlyContinue
    if ($claudeCli) {
        & claude mcp remove $Name --scope user 2>$null | Out-Null
        & claude mcp add --scope user --transport http $Name $Url
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Claude Code: registered via 'claude mcp add' (user scope)" -ForegroundColor Green
        } else {
            Write-Host "    Claude Code: 'claude mcp add' returned $LASTEXITCODE" -ForegroundColor Yellow
        }
    } else {
        Write-Host "    Claude Code CLI not on PATH — skipping (install: 'npm i -g @anthropic-ai/claude-code')" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done. Start Revit $RevitVersion, open a project, then verify:" -ForegroundColor Cyan
Write-Host "  Invoke-RestMethod -Method Post -Uri $Url -ContentType application/json ``" -ForegroundColor Gray
Write-Host "    -Body '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}'" -ForegroundColor Gray
