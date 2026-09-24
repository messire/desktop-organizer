# Phase 4 — Versioned JSON Store

## Цель

Отделить простые user preferences от структурного состояния приложения.

## Target

`ApplicationSettingsBase` оставить для примитивных preferences, если удобно:

- UI language;
- simple toggles;
- small scalar defaults.

Структурное состояние перевести в versioned JSON:

- panels;
- geometry;
- appearance;
- pages/layout snapshots;
- panel content metadata;
- virtual desktop bindings.

## Формат

```json
{
  "schemaVersion": 2,
  "writtenAtUtc": "...",
  "appVersion": "...",
  "panels": []
}
```

Запись:

1. serialize to temp;
2. fsync/flush;
3. validate parse;
4. atomic replace;
5. keep `.bak`;
6. only then update in-memory committed version.

## Migration

- `ISettingsMigration` chain: v1 -> v2 -> v3.
- Первый запуск читает legacy settings и пишет JSON.
- Legacy data не удаляется минимум один release.
- Import/export JSON не должен совпадать 1:1 с internal store contract.

## Артефакты

- `ADR/ADR-024-versioned-json-persistence.md`
- `Storage/IApplicationStateStore.cs`
- `Storage/ApplicationStateEnvelope.cs`
- `Storage/migration-plan.md`

## DoD

- Migration существующей установки без потери panels/items/layout.
- Simulated crash во время write оставляет либо старый, либо новый валидный state.
- Corrupt primary -> recovery from backup.
- Golden migration tests минимум для 3 schema versions.
- Rollback на предыдущий app release задокументирован.

## Статус

**`[~]` Код + автотесты завершены (1551/1551 тестов зелёные, `dotnet build` чистый); manual verify на
живом Windows (Win10/Win11) ожидает ручного прогона.**

Ветка `refactor/versioned-json-store` (от `rc/0.4.0`). Мастер-план реализации:
`docs/PLANS/phase-4-versioned-json-store.md`. Коммиты C1–C7 (`131f009`..`d16f313`) + пост-ревью фиксы
`dec61fe`, `fa38f68`, `e3c845d`, `a15df11`, `b015849` + документационный коммит C8. Прошли
`code-quality-reviewer` (1 CRITICAL + 6 MAJOR найдено и исправлено, включая критическую потерю сети
безопасности для частичной загрузки панелей) и `fences-ux-parity-reviewer` (расхождений с эталонным
поведением не найдено, все 17 персистируемых полей `ChildWindowModel` подтверждены round-trip).
ADR-024: `Proposed` → `Accepted`.

### Реализовано (code-complete)

- Единый versioned JSON-store (`%LOCALAPPDATA%\DesktopOrganizer\State\application-state.json`) вместо
  четырёх независимых писателей в `user.config` (панели/страницы/layout-снапшоты/системная панель),
  каждый со своим JSON-диалектом (ADR-024 §0.1).
- Атомарная запись: temp → fsync → validate parse → `File.Replace` (даёт `.bak` бесплатно) → только
  после подтверждённого успеха двигается committed-состояние (ADR-024 D-6); тестовый шов на 5 стадиях
  краш-инъекции.
- 7-ступенчатая read/recovery-лестница: `Loaded`/`RecoveredFromBackup` (×2 rungs)/`Quarantined`/
  `ImportedFromLegacy`/`FirstRun`/`NewerSchema` (rung 7 — документ новее, не трогаем; основа rollback,
  ADR-024 D-7).
- 3-версионная цепочка миграций (v1 — дословная транскрипция legacy; v2 — распаковка
  `PanelJsonItems` + единый camelCase/строковые enum'ы; v3 — группировка `geometry`/`appearance`/
  `content`, ADR-024 D-9), golden-тесты для всех трёх плюс legacy-импорта.
- Advisory storage-root file lock (`FileShare.None`, `.storage-lock`) — закрывает `ADR-023 R-15`
  (ADR-024 D-8).
- Единая точка загрузки в `Program.Main` до `new App(...)` + исполняемый двухчастный барьер записи:
  `IApplicationStateStore.Save` (до `Load()`) и `IApplicationStateService.ConsumersReady`/
  `MarkConsumersReady()` (до того как все 4 потребителя прочитали свой срез, пост-ревью M-1, ADR-024
  D-11).
- `SaveScheduler` переехал в `ApplicationStateService`; публичный контракт `IWindowManager` не менялся
  (ADR-024 D-12).
- `--restore-state-backup` CLI-переключатель (C7) + `docs/Refactoring/Storage/rollback.md` (3
  сценария отката).
- Найдены и исправлены при ревью: CRITICAL C-1 (потеря сети безопасности для частичной загрузки
  панелей — новый `IApplicationStateStore.QuarantineLoadedDocument`/`IApplicationStateService.
  ReportPartialLoad`); MAJOR M-1 (второй барьер записи `ConsumersReady`); MAJOR M-2
  (`LayoutSnapshotService.Load()` не был идемпотентен); MAJOR M-3 (`UnauthorizedAccessException` не
  ловилась наравне с `IOException` при Controlled Folder Access/AV); MAJOR M-4 (инвертированный
  порядок записи/карантина в `StateRestoreCommand`, приводивший к потере primary-файла при неудачной
  записи); MAJOR M-6 (необработанное исключение миграции кирпичило запуск на каждом следующем старте
  — теперь карантин + деградация до пустого состояния).
- ADR-024 переведён `Proposed` → `Accepted`.

### НЕ выполнено в этой среде — manual verify (Win10/Win11)

Среда разработки ограничена `dotnet build`/`dotnet test`, нет живого Windows GUI. Остаются пунктами
`docs/Refactoring/Testing/storage-manual-matrix.md` (S-1..S-11): реальный апгрейд реальной установки
(S-1), апгрейд без панелей/с битым `ChildWindowsState` (S-2/S-3), идемпотентность повторного запуска
(S-4), откат по сценариям A/B (S-5/S-6), FUS-конфликт записи (S-7), жёсткое выключение питания во
время drag (S-8), `--restore-state-backup` на битом primary (S-9), Controlled Folder Access/AV (S-10),
перф-бюджет старта (S-11).

Фаза **не** закрывается как `[x]` до тех пор, пока матрица S-1..S-11 не будет воспроизведена вручную на
Win10/Win11.
