<#
.SYNOPSIS
    Generates a synthetic panel layout (N panels x M dummy files each) for performance baseline
    measurements (docs/Refactoring/Testing/performance-baseline-procedure.md), and can restore the
    real user state afterwards.

.DESCRIPTION
    Since Phase 4 (versioned JSON store, ADR-024, commit C6 "wire UI onto the JSON store"),
    DesktopOrganizer no longer reads structural panel state from Properties.Settings.Default
    .ChildWindowsState / user.config at all — WindowManager was rewired onto
    IApplicationStateService, which loads from a single schema-v3 JSON document at
    %LOCALAPPDATA%\DesktopOrganizer\State\application-state.json (see
    src/DesktopOrganizer.Services/Storage/JsonApplicationStateStore.cs,
    src/DesktopOrganizer.UI/State/Dto/*.cs). The pre-Phase-4 version of this script wrote into
    user.config, which after C6 is dead code as far as the running app is concerned — it would
    silently generate a fixture the app never reads (ADR-024 R-13).

    This version writes application-state.json directly, in the exact schema-v3 shape
    ApplicationStateDocumentDto (DesktopOrganizer.UI.State.Dto) produces: camelCase property names,
    string enum values, panels grouped into geometry/appearance/content — see
    tests/DesktopOrganizer.Tests/Resources/Storage/golden/v3-full.json for a hand-authored reference
    document in this same shape. It backs up any existing application-state.json (and its .bak, if
    present) before overwriting, and can restore both afterwards.

.PARAMETER Panels
    Number of panels to generate.

.PARAMETER ItemsPerPanel
    Number of dummy files per panel.

.PARAMETER Restore
    Restores the most recent backup created by this script instead of generating a new fixture.

.EXAMPLE
    ./tools/perf/New-PerfFixture.ps1 -Panels 5 -ItemsPerPanel 100

.EXAMPLE
    ./tools/perf/New-PerfFixture.ps1 -Restore
#>
[CmdletBinding()]
param(
    [int]$Panels = 5,
    [int]$ItemsPerPanel = 100,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

# Mirrors StateStorePaths (src/DesktopOrganizer.Services/Storage/StateStorePaths.cs): primary document
# lives under %LOCALAPPDATA%\DesktopOrganizer\State\application-state.json. This script deliberately
# does NOT use StateStorePaths' atomic-write protocol (temp file + fsync + File.Replace, ADR-024 D-6)
# — it is a developer tool generating a throwaway fixture, not production write-path code; a plain
# Set-Content is enough here, and DesktopOrganizer's own recovery ladder (D-7) tolerates whatever this
# script leaves behind on a bad exit (worst case: a valid .bak survives, or the primary is simply
# regenerated on the next real run).
$stateDir = Join-Path $env:LOCALAPPDATA 'DesktopOrganizer\State'
$primaryPath = Join-Path $stateDir 'application-state.json'
$backupPath = "$primaryPath.perf-fixture-backup"
$bakSidecarPath = "$primaryPath.bak"
$bakSidecarBackupPath = "$bakSidecarPath.perf-fixture-backup"

if ($Restore) {
    if (-not (Test-Path $backupPath)) {
        throw "No backup found at '$backupPath' — nothing to restore."
    }

    Copy-Item -Path $backupPath -Destination $primaryPath -Force
    Remove-Item -Path $backupPath -Force
    Write-Host "Restored original state from backup to '$primaryPath'."

    if (Test-Path $bakSidecarBackupPath) {
        Copy-Item -Path $bakSidecarBackupPath -Destination $bakSidecarPath -Force
        Remove-Item -Path $bakSidecarBackupPath -Force
        Write-Host "Restored original '.bak' sidecar to '$bakSidecarPath'."
    } elseif (Test-Path $bakSidecarPath) {
        # No original .bak existed before this script ran (fresh-primary case below) — the fixture
        # run's own .bak (created by File.Replace-equivalent overwrite semantics, if any) must not
        # linger as if it were real prior state.
        Remove-Item -Path $bakSidecarPath -Force
    }

    return
}

New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

if (Test-Path $primaryPath) {
    if (-not (Test-Path $backupPath)) {
        Copy-Item -Path $primaryPath -Destination $backupPath
        Write-Host "Backed up original state to '$backupPath'."
    } else {
        Write-Host "Backup already exists at '$backupPath' (not overwritten) — original state is preserved."
    }
} else {
    Write-Host "No existing '$primaryPath' — nothing to back up (this run will create the file fresh)."
}

if ((Test-Path $bakSidecarPath) -and -not (Test-Path $bakSidecarBackupPath)) {
    Copy-Item -Path $bakSidecarPath -Destination $bakSidecarBackupPath
    Write-Host "Backed up original '.bak' sidecar to '$bakSidecarBackupPath'."
}

# Real dummy files so icon loading / IconProvider caching behaves like a real fixture, not just
# JSON with dangling paths (missing paths would render grayed-out, see FileItem.Exists).
$fixtureRoot = Join-Path $env:TEMP 'DesktopOrganizer.PerfFixture'
if (Test-Path $fixtureRoot) { Remove-Item $fixtureRoot -Recurse -Force }
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

# Schema-v3 panel shape — field-for-field the same as PanelStateDto/PanelGeometryDto/
# PanelAppearanceDto/PanelContentDto (src/DesktopOrganizer.UI/State/Dto/*.cs) serialize via StateJson
# (camelCase, JsonStringEnumConverter, nulls omitted). See tests/DesktopOrganizer.Tests/Resources/
# Storage/golden/v3-full.json for the authoritative reference shape this must keep matching.
$panelModels = for ($p = 0; $p -lt $Panels; $p++) {
    $panelDir = Join-Path $fixtureRoot "panel-$p"
    New-Item -ItemType Directory -Path $panelDir | Out-Null

    $filePaths = for ($i = 0; $i -lt $ItemsPerPanel; $i++) {
        $filePath = Join-Path $panelDir "item-$i.txt"
        Set-Content -Path $filePath -Value "perf fixture" -NoNewline
        $filePath
    }

    [ordered]@{
        id         = [guid]::NewGuid().ToString()
        title      = "Perf Fixture $p"
        pageId     = "default"
        geometry   = [ordered]@{
            x               = 40 + ($p * 60)
            y               = 40 + ($p * 40)
            width           = 320
            height          = 240
            monitorDeviceId = $null
            windowState     = "Normal"
            isRolledUp      = $false
            isVisible       = $true
        }
        appearance = [ordered]@{
            backgroundColor   = "#FF2D2D30"
            backgroundOpacity = 0.85
            textColor         = "#FFFFFFFF"
            iconSize          = 48
        }
        content    = [ordered]@{
            viewMode         = "List"
            contentFilter    = "ShowAll"
            sortMode         = "Manual"
            sourceFolderPath = $null
            filePaths        = @($filePaths)
        }
    }
}

$document = [ordered]@{
    schemaVersion    = 3
    writtenAtUtc     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
    appVersion       = "perf-fixture"
    panels           = @($panelModels)
    pages            = [ordered]@{
        ids           = @("default")
        currentPageId = "default"
    }
    layoutSnapshots  = @()
    systemIconsPanel = [ordered]@{
        isEnabled               = $false
        showOnAllVirtualDesktops = $false
        iconSize                = "Medium"
        anchorVirtualDesktopId  = $null
        items                   = @()
    }
}

$json = $document | ConvertTo-Json -Depth 10

# UTF-8 without BOM, matching AtomicJsonFileWriter's on-disk encoding (ADR-024 D-6) — Set-Content's
# 'utf8NoBOM' encoding name only exists on PowerShell 6+ (pwsh); this works identically on Windows
# PowerShell 5.1 and pwsh.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($primaryPath, $json, $utf8NoBom)

Write-Host "Wrote fixture: $Panels panels x $ItemsPerPanel items to '$primaryPath'."
Write-Host "Restore the original state afterwards with: ./tools/perf/New-PerfFixture.ps1 -Restore"
