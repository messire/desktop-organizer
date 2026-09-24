<#
.SYNOPSIS
    Stress-checks the single-instance guard (ADR-023) by launching DesktopOrganizer.UI N times in
    rapid succession -- scenario M-1 of docs/Refactoring/Testing/single-instance-manual-matrix.md.

.DESCRIPTION
    Launches the executable N times with a short pause between launches, waits for the secondary
    processes to finish their handshake and exit, then asserts the two things M-1 is about:

      1. exactly one DesktopOrganizer.UI process is left alive (the primary);
      2. every process that exited did so with code 0 -- a secondary that successfully handed its
         activation to the primary (ADR-023 D-6).

    Exit codes other than 0 are reported individually rather than summarized, because each one means
    something specific and different (see the table in the manual matrix): 3 = handshake failed, the
    primary is alive but unresponsive (D-7); 4 = a primary exists in another Windows session (D-4).
    Any other non-zero code is an ordinary application crash and not a single-instance finding at all.

    Note on what this does NOT verify: the *first* launch may find no primary at all and become one
    itself, so "1 process alive" is the invariant, not "the process I started first". The script
    leaves the primary running only with -KeepRunning, because manual scenarios M-2 (the R-1 state
    regression) and M-12 (log retention) both need to inspect state produced by that exact primary.

    Requires a real interactive desktop: the app is a WPF shell integration, and secondaries exercise
    named pipes plus AllowSetForegroundWindow. Do not expect meaningful results in a headless session.

.PARAMETER ExePath
    Path to DesktopOrganizer.UI.exe (published or bin\Debug build).

.PARAMETER Launches
    Number of launches, including the one that becomes primary. M-1 specifies 20.

.PARAMETER PauseMs
    Pause between launches, in milliseconds. M-1 specifies 100.

.PARAMETER SettleSeconds
    How long to wait, after the last launch, for secondary processes to exit. Must comfortably exceed
    the handshake budget (3s, ADR-023 D-6) plus the first launch's own startup time.

.PARAMETER KeepRunning
    Leave the surviving primary running instead of closing it -- required when this run is the setup
    step for M-2 or M-12.

.EXAMPLE
    ./tools/lifecycle/Invoke-SingleInstanceStress.ps1 -ExePath 'installer/publish/win-x64/DesktopOrganizer.UI.exe'

.EXAMPLE
    # Setup for M-2 / M-12: keep the primary alive so its persisted state and log files can be inspected.
    ./tools/lifecycle/Invoke-SingleInstanceStress.ps1 -ExePath '.\DesktopOrganizer.UI.exe' -KeepRunning
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [int]$Launches = 20,

    [int]$PauseMs = 100,

    [int]$SettleSeconds = 15,

    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: '$ExePath'. Publish or build the app first (see build.ps1)."
}

$exe = (Resolve-Path $ExePath).Path
$processName = [System.IO.Path]::GetFileNameWithoutExtension($exe)

$alreadyRunning = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($alreadyRunning.Count -gt 0) {
    throw "$($alreadyRunning.Count) '$processName' process(es) already running. M-1 starts from a clean state -- close the app first."
}

Write-Host "Launching '$processName' $Launches times, ${PauseMs}ms apart..."

$started = for ($i = 1; $i -le $Launches; $i++) {
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Milliseconds $PauseMs
    $proc
}

Write-Host "Waiting up to ${SettleSeconds}s for secondary processes to complete their handshake and exit..."

$deadline = [System.Diagnostics.Stopwatch]::StartNew()
while ($deadline.Elapsed.TotalSeconds -lt $SettleSeconds) {
    # One survivor is the expected steady state; stop waiting as soon as we reach it.
    if (@($started | Where-Object { -not $_.HasExited }).Count -le 1) { break }
    Start-Sleep -Milliseconds 250
}

$exited = @($started | Where-Object { $_.HasExited })
$alive = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

$badExits = @($exited | Where-Object { $_.ExitCode -ne 0 })

Write-Host ""
Write-Host "Launched:            $Launches"
Write-Host "Exited:              $($exited.Count)"
Write-Host "Processes:           $($alive.Count)   (expected 1)"
Write-Host "Non-zero exit codes: $($badExits.Count)   (expected 0)"

foreach ($proc in $badExits) {
    $meaning = switch ($proc.ExitCode) {
        3 { 'handshake failed -- primary alive but unresponsive (ADR-023 D-7)' }
        4 { 'primary exists in another Windows session (ADR-023 D-4)' }
        default { 'not a single-instance exit code -- investigate as an ordinary crash' }
    }

    Write-Host "  pid $($proc.Id): exit code $($proc.ExitCode) -- $meaning"
}

$passed = ($alive.Count -eq 1) -and ($badExits.Count -eq 0)

if (-not $KeepRunning) {
    foreach ($proc in $alive) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc.Kill() }
    }
}
else {
    Write-Host ""
    Write-Host "Primary left running (-KeepRunning) -- continue with M-2 / M-12."
}

Write-Host ""
if ($passed) {
    Write-Host "M-1 PASS" -ForegroundColor Green
    exit 0
}

Write-Host "M-1 FAIL" -ForegroundColor Red
exit 1
