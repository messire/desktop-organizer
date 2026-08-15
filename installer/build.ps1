<#
.SYNOPSIS
    Публикует DesktopOrganizer.UI как self-contained сборку (win-x64) и сразу
    собирает инсталлятор через Inno Setup — одной командой, чтобы install.exe
    никогда не мог "отстать" от исходников.

.EXAMPLE
    .\installer\build.ps1
    .\installer\build.ps1 -SkipInstaller   # только публикация, без ISCC
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot "src\DesktopOrganizer.UI\DesktopOrganizer.UI.csproj"
$outDir   = Join-Path $PSScriptRoot "publish\$Runtime"
$issFile  = Join-Path $PSScriptRoot "DesktopOrganizer.iss"

if (Test-Path $outDir) {
    Write-Host "Очищаю предыдущую публикацию: $outDir"
    Remove-Item -Recurse -Force $outDir
}

Write-Host "Публикую $project ($Configuration, $Runtime, self-contained)..."

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с ошибкой (код $LASTEXITCODE)"
}

Write-Host "Публикация готова: $outDir"

if ($SkipInstaller) {
    Write-Host "SkipInstaller указан — инсталлятор не собираю. Не забудь собрать его вручную перед раздачей!"
    return
}

# Найти ISCC.exe (компилятор Inno Setup из командной строки).
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "ISCC.exe не найден (Inno Setup не установлен или стоит в нестандартной папке)."
    Write-Warning "Открой installer\DesktopOrganizer.iss в Inno Setup Compiler и собери вручную."
    return
}

Write-Host ""
Write-Host "Собираю инсталлятор: $iscc $issFile"
& $iscc $issFile

if ($LASTEXITCODE -ne 0) {
    throw "ISCC завершился с ошибкой (код $LASTEXITCODE)"
}

Write-Host ""
Write-Host "Готово. Setup.exe лежит в installer\Output\."
