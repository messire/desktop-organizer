<#
.SYNOPSIS
    Measures DesktopOrganizer.UI startup time and working set over N runs, for Phase 0
    performance baseline measurements (docs/Refactoring/Testing/performance-baseline-procedure.md).

.DESCRIPTION
    Launches the app N+1 times (first run is a discarded warm-up per the procedure), measures
    elapsed time from process start until the process becomes idle (no CPU activity for a short
    settle window — a proxy for "last panel rendered" since the process exposes no readiness
    signal), samples working set shortly after, then closes it. Prints a markdown table row ready
    to paste into performance-baseline-template.md. Does NOT invent numbers — every value is an
    actual measurement from the run it reports.

.PARAMETER ExePath
    Path to DesktopOrganizer.UI.exe (published build).

.PARAMETER Runs
    Number of measured runs (in addition to one discarded warm-up run). Median is reported.

.PARAMETER SettleMs
    How long CPU usage must stay near zero to be considered "idle" (startup complete).

.EXAMPLE
    ./tools/perf/Measure-Startup.ps1 -ExePath 'installer/publish/win-x64/DesktopOrganizer.UI.exe' -Runs 3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [int]$Runs = 3,

    [int]$SettleMs = 1000
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: '$ExePath'. Publish the app first (see build.ps1)."
}

function Measure-SingleRun {
    param([string]$ExePath, [int]$SettleMs)

    $proc = Start-Process -FilePath $ExePath -PassThru
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $lastCpu = $proc.TotalProcessorTime
        $settleStart = $null

        while ($true) {
            Start-Sleep -Milliseconds 200
            $proc.Refresh()
            $cpu = $proc.TotalProcessorTime
            $delta = ($cpu - $lastCpu).TotalMilliseconds
            $lastCpu = $cpu

            if ($delta -lt 5) {
                if (-not $settleStart) { $settleStart = [System.Diagnostics.Stopwatch]::StartNew() }
                elseif ($settleStart.ElapsedMilliseconds -ge $SettleMs) { break }
            } else {
                $settleStart = $null
            }

            if ($sw.ElapsedMilliseconds -gt 30000) {
                throw "Startup did not settle within 30s — app may be waiting on input or crashed."
            }
        }

        $startupMs = $sw.ElapsedMilliseconds - $SettleMs
        $proc.Refresh()
        $workingSetMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)

        return [pscustomobject]@{
            StartupMs    = $startupMs
            WorkingSetMb = $workingSetMb
        }
    }
    finally {
        if (-not $proc.HasExited) {
            $proc.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 500
            if (-not $proc.HasExited) { $proc.Kill() }
        }
    }
}

Write-Host "Warm-up run (discarded)..."
Measure-SingleRun -ExePath $ExePath -SettleMs $SettleMs | Out-Null

$results = for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "Measured run $i of $Runs..."
    Measure-SingleRun -ExePath $ExePath -SettleMs $SettleMs
}

$medianStartup = ($results.StartupMs | Sort-Object)[[math]::Floor($Runs / 2)]
$medianWorkingSet = ($results.WorkingSetMb | Sort-Object)[[math]::Floor($Runs / 2)]

Write-Host ""
Write-Host "Raw runs:"
$results | Format-Table -AutoSize

Write-Host ""
Write-Host "Median — paste into performance-baseline-template.md:"
Write-Host "| <scenario> | ${medianStartup}ms | ${medianWorkingSet}MB | <CPU idle> | <CPU interaction> | |"
