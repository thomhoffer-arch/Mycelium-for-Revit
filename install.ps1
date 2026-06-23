#Requires -Version 5.1
<#
.SYNOPSIS
  One-click installer for the Mycelium Studio Revit Connector.

.DESCRIPTION
  Downloads the latest pre-built release from GitHub, installs it into every
  detected Revit add-in folder (2024, 2025, 2026), and registers the MCP
  listener in Claude Desktop's config and (when the CLI is on PATH) Claude
  Code's config.

  Run as: right-click -> "Run with PowerShell", or via install.bat (double-click).

  Environment overrides (optional):
    $env:MYCELIUM_REVIT_URL   MCP server URL (default http://127.0.0.1:47100/mcp)
    $env:MYCELIUM_MCP_NAME    MCP entry name  (default mycelium-revit)
#>

$ErrorActionPreference = 'Stop'
$Url  = if ($env:MYCELIUM_REVIT_URL)  { $env:MYCELIUM_REVIT_URL }  else { 'http://127.0.0.1:47100/mcp' }
$Name = if ($env:MYCELIUM_MCP_NAME)   { $env:MYCELIUM_MCP_NAME }   else { 'mycelium-revit' }

Write-Host ""
Write-Host "==> Mycelium Studio Revit Connector installer" -ForegroundColor Cyan
Write-Host ""

# ── Detect installed Revit versions ────────────────────────────────────────────
$detected = @()
foreach ($year in @('2024', '2025', '2026')) {
    if (Test-Path "C:\Program Files\Autodesk\Revit $year\Revit.exe") {
        $detected += $year
        Write-Host "    Found Revit $year" -ForegroundColor Green
    }
}
if ($detected.Count -eq 0) {
    Write-Host "No Revit installation found (checked 2024-2026). Exiting." -ForegroundColor Yellow
    exit 0
}

# ── Fetch latest release metadata ──────────────────────────────────────────────
Write-Host ""
Write-Host "==> Fetching latest release from GitHub..." -ForegroundColor Cyan
$release = Invoke-RestMethod 'https://api.github.com/repos/thomhoffer-arch/Mycelium-for-Revit/releases/latest'
Write-Host "    Release: $($release.tag_name)" -ForegroundColor Green

$tmpDir = Join-Path $env:TEMP 'mycelium-revit-install'
New-Item -ItemType Directory -Force $tmpDir | Out-Null

# ── Download + install per version ─────────────────────────────────────────────
foreach ($version in $detected) {
    $assetName = if ($version -eq '2024') {
        'loam-revit-connector-revit2024.zip'
    } else {
        'loam-revit-connector-revit2025-2026.zip'
    }

    $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if (-not $asset) {
        Write-Host "    [WARN] Asset '$assetName' not found in release $($release.tag_name) — skipping Revit $version." -ForegroundColor Yellow
        continue
    }

    $zipPath = Join-Path $tmpDir $assetName
    Write-Host ""
    Write-Host "==> Downloading $assetName..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    Write-Host "==> Installing to $addinDir" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $addinDir | Out-Null
    Expand-Archive -Force -Path $zipPath -DestinationPath $addinDir
    Write-Host "    Revit ${version}: done." -ForegroundColor Green
}

Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue

# ── Claude Desktop registration ────────────────────────────────────────────────
Write-Host ""
Write-Host "==> Registering MCP server '$Name' -> $Url" -ForegroundColor Cyan

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
    Write-Host "    Claude Desktop: updated $desktopPath" -ForegroundColor Green
} else {
    Write-Host "    Claude Desktop config dir not found — skipping ($desktopDir)" -ForegroundColor Yellow
}

# ── Claude Code CLI registration ───────────────────────────────────────────────
$claudeCli = Get-Command claude -ErrorAction SilentlyContinue
if ($claudeCli) {
    & claude mcp remove $Name --scope user 2>$null | Out-Null
    & claude mcp add --scope user --transport http $Name $Url
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    Claude Code: registered (user scope)" -ForegroundColor Green
    } else {
        Write-Host "    Claude Code: 'claude mcp add' returned $LASTEXITCODE" -ForegroundColor Yellow
    }
} else {
    Write-Host "    Claude Code CLI not on PATH — skipping." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done! Start Revit and open a project — the MCP server starts automatically." -ForegroundColor Cyan
Write-Host "MCP URL: $Url" -ForegroundColor Gray
Write-Host ""
