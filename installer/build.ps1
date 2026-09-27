<#
.SYNOPSIS
    Публикует DesktopOrganizer.UI как self-contained сборку (win-x64) и сразу
    собирает инсталлятор через Inno Setup — одной командой, чтобы install.exe
    никогда не мог "отстать" от исходников.

.DESCRIPTION
    Code-signing (ADR-038, G1) — полностью опционально, ничего не ломает при отсутствии секретов:
    если задана переменная окружения DESKTOPORGANIZER_SIGN_CERT, скрипт подписывает опубликованные
    DesktopOrganizer*.exe/*.dll, а затем передаёт ISCC свой собственный sign tool ("dosign"), которым
    Inno Setup подписывает итоговый Setup.exe (и деинсталлятор — SignedUninstaller=yes в [Setup],
    включается тем же /DSignEnabled). Без DESKTOPORGANIZER_SIGN_CERT — прежнее поведение, инсталлятор
    неподписан, печатается явное уведомление об этом.

    Переменные окружения:
      DESKTOPORGANIZER_SIGN_CERT      — путь к .pfx-файлу ИЛИ SHA1-отпечаток сертификата из
                                          локального хранилища сертификатов (ПРЕДПОЧТИТЕЛЬНЫЙ вариант —
                                          пароль тогда нигде не участвует вообще). Обязательна для подписи.
      DESKTOPORGANIZER_SIGN_PASSWORD  — пароль к .pfx (не нужен для отпечатка из хранилища).
      DESKTOPORGANIZER_SIGN_TIMESTAMP — URL RFC3161 timestamp-сервера (по умолчанию
                                          http://timestamp.digicert.com).
      SIGNTOOL_PATH                   — путь к signtool.exe (по умолчанию ищется в Windows SDK /
                                          через PATH).

    Секреты (пароль сертификата) никогда не попадают в Write-Host/Write-Warning вывод и никогда — в
    команду подписи, которую видит ISCC (M5/L8, release-hardening review): для .iss/ISCC этот скрипт
    ВСЕГДА подписывает по отпечатку сертификата, импортируя .pfx во временный сертификат текущего
    пользователя (Cert:\CurrentUser\My) при необходимости и удаляя его перед выходом. Пароль (/p)
    попадает только в прямой вызов signtool для publish-выхода (exe/dll) — тот, что маскируется в
    выводе этого скрипта, а не передаётся другому процессу. В CI (.github/workflows/ci.yml) секрет
    .pfx импортируется в хранилище сертификатов ДО запуска этого скрипта, и DESKTOPORGANIZER_SIGN_CERT
    получает уже готовый отпечаток — так что пароль в CI вообще не доходит до этого скрипта на втором
    прогоне подписи (только на первом, прямом signtool-вызове publish-выхода, где он thumbprint'ом уже
    заменён тоже).

.EXAMPLE
    .\installer\build.ps1
    .\installer\build.ps1 -SkipInstaller   # только публикация, без ISCC

.EXAMPLE
    $env:DESKTOPORGANIZER_SIGN_CERT = "C:\secrets\desktoporganizer.pfx"
    $env:DESKTOPORGANIZER_SIGN_PASSWORD = "..."
    .\installer\build.ps1   # публикация + подпись exe/dll + подписанный Setup.exe/uninstaller
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

# --- Code signing (ADR-038, G1) -------------------------------------------------------------------

function Find-SignToolPath {
    if ($env:SIGNTOOL_PATH -and (Test-Path $env:SIGNTOOL_PATH)) {
        return $env:SIGNTOOL_PATH
    }

    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    # Newest-first: Windows SDK ships signtool.exe per-architecture under versioned bin folders.
    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($sdkRoot in $sdkRoots) {
        $candidate = Get-ChildItem -Path $sdkRoot -Recurse -Filter "signtool.exe" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Get-CertificateArguments {
    param([string]$SignTimestampUrl)

    # DESKTOPORGANIZER_SIGN_CERT is either a path to a .pfx file (paired with an optional password) or
    # a SHA1 thumbprint of a certificate already present in a local certificate store (no password).
    if (Test-Path $env:DESKTOPORGANIZER_SIGN_CERT) {
        $certArgs = @("/f", $env:DESKTOPORGANIZER_SIGN_CERT)
        if ($env:DESKTOPORGANIZER_SIGN_PASSWORD) {
            $certArgs += @("/p", $env:DESKTOPORGANIZER_SIGN_PASSWORD)
        }
    }
    else {
        $certArgs = @("/sha1", $env:DESKTOPORGANIZER_SIGN_CERT)
    }

    $certArgs += @("/fd", "sha256", "/tr", $SignTimestampUrl, "/td", "sha256")
    return $certArgs
}

function Get-OrImportSigningThumbprint {
    param(
        [string]$CertRef,
        [string]$Password
    )

    # M5/L8 (release-hardening review): the ISCC "dosign" command line must NEVER carry a .pfx
    # password — unlike the direct signtool call below (masked in this script's own console output,
    # never persisted anywhere else), ISCC echoes the SignTool= command line it runs into its own
    # compile log, and a plaintext password there would be a real leak (build logs/artifacts often
    # outlive the ephemeral runner they were produced on). So the ISCC line always signs by
    # certificate-store thumbprint (/sha1), never by /f <pfx> /p <password>.
    #
    # $CertRef is either such a thumbprint already (nothing to do) or a path to a .pfx file, in which
    # case it is imported into the CURRENT USER's personal store here so a thumbprint exists to sign
    # by. The caller is responsible for removing an imported certificate again once ISCC has finished
    # (see the `finally` block around its only call site) — CI additionally removes it in its own
    # always() step as a second, independent safety net.
    if (-not (Test-Path $CertRef)) {
        return [pscustomobject]@{ Thumbprint = $CertRef; Imported = $false }
    }

    $importArgs = @{ FilePath = $CertRef; CertStoreLocation = "Cert:\CurrentUser\My" }
    if ($Password) {
        $importArgs["Password"] = ConvertTo-SecureString -String $Password -AsPlainText -Force
    }

    $imported = Import-PfxCertificate @importArgs
    return [pscustomobject]@{ Thumbprint = $imported.Thumbprint; Imported = $true }
}

function Get-MaskedArguments {
    param([string[]]$Arguments)

    # Never echo the .pfx password (or, out of caution, whatever value follows /p) to the console/log.
    $masked = [System.Collections.Generic.List[string]]::new()
    $redactNext = $false
    foreach ($token in $Arguments) {
        if ($redactNext) {
            $masked.Add("***")
            $redactNext = $false
            continue
        }

        $masked.Add($token)
        if ($token -eq "/p") {
            $redactNext = $true
        }
    }

    return ($masked -join " ")
}

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
    -p:SatelliteResourceLanguages=en%3Bru `
    -p:ContinuousIntegrationBuild=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с ошибкой (код $LASTEXITCODE)"
}

Write-Host "Публикация готова: $outDir"

# docs/PLANS/localization.md §4.2/§6 (G-8): без -p:SatelliteResourceLanguages (и без Directory.Build.props'ного
# аналога — эта строка дублирует его явно, на случай если publish когда-нибудь запустят с другими props)
# WPF/.NET тащат сателлитные сборки для ~13 культур фреймворка. Явно перепроверяем сам publish-выход, а не
# просто верим свойству: если сателлит для не-ru культуры всё-таки просочился (например, из-за более
# приоритетного .user.props или системной переменной среды), инсталлятор не должен молча унести чужие языки.
Write-Host "Проверяю состав publish: культуры сателлитных сборок..."
$satelliteDlls = Get-ChildItem -Path $outDir -Recurse -Filter "*.resources.dll" -File
$cultureFolders = $satelliteDlls | ForEach-Object { $_.Directory.Name } | Sort-Object -Unique
$unexpectedCultures = $cultureFolders | Where-Object { $_ -ne "ru" }

if ($unexpectedCultures.Count -gt 0) {
    throw "publish\$Runtime содержит папки культур сверх 'ru': $($unexpectedCultures -join ', '). " +
        "Проверь -p:SatelliteResourceLanguages здесь и <SatelliteResourceLanguages> в Directory.Build.props."
}

$localizationSatellite = Join-Path $outDir "ru\DesktopOrganizer.Localization.resources.dll"
if (-not (Test-Path $localizationSatellite)) {
    throw "Не найден $localizationSatellite — русская сателлитная сборка ресурсов локализации отсутствует в publish."
}

Write-Host "Состав культур в порядке: только 'ru', $localizationSatellite на месте."

# --- Подпись опубликованных файлов (ADR-038, G1) ---------------------------------------------------
# Полностью опциональна: без DESKTOPORGANIZER_SIGN_CERT ничего не меняется — сборка остаётся
# неподписанной, как раньше, с явным уведомлением ниже.
$signIsccArgs = @()

# M5/L8 (release-hardening review): если ниже импортируем .pfx в Cert:\CurrentUser\My ради отпечатка
# для ISCC-строки, этот объект помечает, что сертификат нужно удалить из хранилища перед выходом из
# скрипта (см. `finally` в самом низу) — независимо от того, дошли ли мы до ISCC или вышли раньше
# (SkipInstaller, ISCC.exe не найден, ошибка компиляции).
$importedSigningCert = $null

if (-not $env:DESKTOPORGANIZER_SIGN_CERT) {
    Write-Host ""
    $unsignedNotice = "Неподписанная сборка: переменная DESKTOPORGANIZER_SIGN_CERT не задана — exe/dll и " +
        "Setup.exe останутся без цифровой подписи (SmartScreen покажет ""Неизвестный издатель"")."
    Write-Warning $unsignedNotice
}
else {
    $signTool = Find-SignToolPath
    if (-not $signTool) {
        $missingSignToolMessage = "DESKTOPORGANIZER_SIGN_CERT задан, но signtool.exe не найден. Установи " +
            "Windows SDK (входит signtool.exe) или укажи путь явно через SIGNTOOL_PATH."
        throw $missingSignToolMessage
    }

    $timestampUrl = if ($env:DESKTOPORGANIZER_SIGN_TIMESTAMP) { $env:DESKTOPORGANIZER_SIGN_TIMESTAMP } else { "http://timestamp.digicert.com" }
    $certArgs = Get-CertificateArguments -SignTimestampUrl $timestampUrl

    $filesToSign = Get-ChildItem -Path $outDir -Recurse -File |
        Where-Object { $_.Name -like "DesktopOrganizer*.exe" -or $_.Name -like "DesktopOrganizer*.dll" }

    Write-Host ""
    Write-Host "Подписываю $($filesToSign.Count) файл(ов) publish-выхода через $signTool ..."
    Write-Host "  signtool sign $(Get-MaskedArguments -Arguments $certArgs) <файлы>"

    & $signTool sign @certArgs @($filesToSign.FullName)
    if ($LASTEXITCODE -ne 0) {
        throw "signtool завершился с ошибкой при подписи publish-выхода (код $LASTEXITCODE)"
    }

    Write-Host "Publish-выход подписан."

    # M5/L8 (release-hardening review): ISCC's own SignTool= directive (guarded by #ifdef SignEnabled in
    # the .iss) needs a *command line* for the named tool "dosign" — $f is Inno Setup's own placeholder
    # for the file it is signing (Setup.exe, and the uninstaller when SignedUninstaller=yes), substituted
    # by ISCC itself, not by PowerShell here (hence the literal, unexpanded '$f' below). Unlike the direct
    # signtool call just above, this command line is handed to ISCC and echoed into ITS OWN compile log
    # — a .pfx password must never appear in it. Always sign by certificate-store thumbprint here (never
    # /f <pfx> /p <password>), importing the .pfx first if that's what was given (see
    # Get-OrImportSigningThumbprint's remarks).
    $importedSigningCert = Get-OrImportSigningThumbprint -CertRef $env:DESKTOPORGANIZER_SIGN_CERT -Password $env:DESKTOPORGANIZER_SIGN_PASSWORD
    $isccSignArgs = @("/sha1", $importedSigningCert.Thumbprint, "/fd", "sha256", "/tr", $timestampUrl, "/td", "sha256")
    $signCommandLine = "`"$signTool`" sign $($isccSignArgs -join ' ') `$f"
    $signIsccArgs = @("/DSignEnabled", "/Sdosign=$signCommandLine")
}

try {
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
    if ($signIsccArgs.Count -gt 0) {
        Write-Host "Собираю инсталлятор (с подписью Setup.exe/деинсталлятора): $iscc $issFile"
    }
    else {
        Write-Host "Собираю инсталлятор: $iscc $issFile"
    }
    & $iscc @signIsccArgs $issFile

    if ($LASTEXITCODE -ne 0) {
        throw "ISCC завершился с ошибкой (код $LASTEXITCODE)"
    }

    Write-Host ""
    Write-Host "Готово. Setup.exe лежит в installer\Output\."
}
finally {
    # M5/L8 (release-hardening review): never leave a .pfx-derived certificate sitting in the CURRENT
    # USER's personal store — remove it regardless of how this script exits (SkipInstaller, ISCC.exe
    # missing, a thrown build error, or a normal successful run).
    if ($importedSigningCert -and $importedSigningCert.Imported) {
        Remove-Item -Path "Cert:\CurrentUser\My\$($importedSigningCert.Thumbprint)" -Force -ErrorAction SilentlyContinue
    }
}
