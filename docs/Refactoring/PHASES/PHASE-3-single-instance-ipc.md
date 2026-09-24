# Phase 3 — Single Instance + IPC + Lifecycle Hardening

## Проблема

`App.xaml.cs` создаёт `DesktopOrganizerSingleInstanceMutex` с `initiallyOwned:false`, но это detection mutex для installer, а не настоящий guard. Одновременные процессы потенциально конфликтуют по storage/state.

## Работы

1. Реальный per-user mutex guard.
2. Второй процесс не продолжает startup.
3. Второй процесс отправляет primary instance команду `ActivateSettings` через Named Pipe.
4. Primary instance показывает/активирует settings UI без изменения desktop panels.
5. Installer detection contract сохранить отдельно или задокументировать совместное имя.
6. Корректное shutdown/dispose IPC server.

## Артефакты

- `ADR/ADR-023-single-instance-and-ipc.md`
- `Templates/SingleInstanceCoordinator.cs`
- integration tests/manual matrix

## DoD

- 20 быстрых повторных запусков -> ровно один процесс.
- Второй запуск открывает существующее MainWindow.
- Нет гонки при primary shutdown + simultaneous launch.
- Installer detection продолжает работать.
- Storage никогда не открывается двумя writable app instances.

## Статус

**`[~]` Код + автотесты завершены (1387/1387 тестов зелёные, 13 прогонов подряд стабильно, `dotnet
build` чистый); manual verify на живом Windows GUI (Win10/Win11) ожидает ручного прогона.**

Ветка `feat/single-instance-ipc` (от `rc/0.4.0`). Мастер-план реализации:
`docs/PLANS/phase-3-single-instance-ipc.md`. Коммиты C1–C6 (`812743b`..`7098c00`) + пост-ревью фиксы
`c3ac4f3`, `1554282`, `2b7d115`. Прошли `code-quality-reviewer` (2 CRITICAL + 4 MAJOR найдено и
исправлено, включая экспериментально подтверждённую утечку pipe-инстанса → busy-spin 100% CPU и
`ObjectDisposedException` на каждом обычном выходе) и `fences-ux-parity-reviewer` (обычный/единственный
запуск не изменился, регрессий не найдено). ADR-023: `Proposed` → `Accepted`.

### Реализовано (code-complete)

- Реальный per-session guard mutex (`Local\DesktopOrganizer.SingleInstance.{sidHash}`) отдельно от
  installer detection mutex (`DesktopOrganizerSingleInstanceMutex`, имя не меняется, ADR-023 D-1).
- Guard выполняется в собственном `DesktopOrganizer.UI.Program.Main` (`<StartupObject>`), до создания
  `Application` — второй процесс никогда не доходит до `App.OnExit` и не стирает `ChildWindowsState`
  (`"[]"`)/`SystemIconsPanel` (ADR-023 D-2, ключевая находка R-1) + defense-in-depth `_role`-проверка в
  `OnExit` (D-3).
- Named Pipe (`maxNumberOfServerInstances: 1`) как машинно-глобальный тай-брейкер поверх session-scoped
  mutex — закрывает Fast User Switching race (ADR-023 D-4); ACL пипа ограничивает доступ текущим
  пользователем, требует прав `CreateNewInstance` **и** `TakeOwnership` (экспериментально подтверждено
  на Windows 11 26200 — отклонение от исходного плана).
- Протокол `DO/1` (`ACTIVATE_SETTINGS`), одна команда, ответ обязателен (`OK`/`SHUTTING_DOWN`/
  `OTHER_SESSION`/`UNSUPPORTED`, ADR-023 D-5); рукопожатие acquire/send с бюджетом 3с (D-6);
  неудачное рукопожатие никогда не поднимает второй writable-инстанс (D-7).
- Активация — строго `IWindowManager.ShowMainWindow()`, ничего больше (пиннится
  `VerifyNoOtherCalls()`), `AllowSetForegroundWindow` перед отправкой (ADR-023 D-8).
- Shutdown: `EnterShutdown()` → существующий teardown → новый `CompleteShutdown()` (не было в исходном
  плане; нужен, чтобы писать `SingleInstanceShutdownSequence` ДО того как `IDiagnosticsLog` станет
  недоступен через `ServiceProvider.Dispose()`, фикс CRITICAL C-2) → `Dispose()` (ADR-023 D-9).
- 5 диагностических событий категории `session` (`SingleInstancePrimaryAcquired`,
  `SingleInstanceSecondaryDetected`, `ActivationRequestReceived`, `ActivationServerUnavailable`,
  `SingleInstanceShutdownSequence`), без пользовательских данных.
- Найдены и исправлены при ревью: CRITICAL утечка pipe-инстанса → busy-spin 100% CPU; CRITICAL
  `ObjectDisposedException` на каждом обычном выходе (C-2); MAJOR неверная классификация
  degraded-primary (M-2: `AclFailed`/`Faulted` ≠ `PipeBusy`, guard не освобождается); MAJOR отсутствие
  top-level guard вокруг рукопожатия в `Program.Main` (M-4).
- ADR-023 переведён `Proposed` → `Accepted`.

### НЕ выполнено в этой среде — manual verify (Win10/Win11)

Среда разработки ограничена `dotnet build`/`dotnet test`, нет живого Windows GUI. Остаются пунктами
`docs/Refactoring/Testing/single-instance-manual-matrix.md` (M-1..M-13): 20 быстрых повторных запусков
(M-1), регресс R-1 после перезапуска primary (M-2), фокус существующего окна при активации (M-3),
неизменность панелей/иконок (M-4), abandoned-mutex после kill primary (M-5), выход из трея +
немедленный запуск (M-6), инсталлятор при запущенном приложении (M-7), автозапуск + ручной клик (M-8),
два разных пользователя Windows (M-9), FUS — одна учётная запись, две сессии (M-10), зависший primary
(M-11), Debug-сборка 20 запусков без вымывания лога (M-12), тайминг холодного старта (M-13).

Остаточный архитектурный риск: R-15 (одновременная запись в storage при FUS, до появления файлового
лока storage-root) — принят и явно перенесён в Фазу 4 (ADR-024, versioned JSON persistence).

Фаза **не** закрывается как `[x]` до тех пор, пока матрица M-1..M-13 не будет воспроизведена вручную на
Win10/Win11.
