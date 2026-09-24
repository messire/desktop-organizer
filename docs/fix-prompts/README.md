# DesktopOrganizer: промпты для перехода к единой геометрии и Ambient Accent

Основа: DesktopOrganizer.zip (срез от 21.09.2026), скрин текущей реализации и выбранный третий референс Ambient Accent. Это **набор заданий для будущего исполнителя**, а не готовые изменения приложения. В предыдущем обсуждении была предложена архитектура общей сетки; конкретные размеры в DIP являются гипотезами и должны быть проверены на реальных мониторах.

Порядок: выполнить `prompts/00-audit.md`, затем `01`—`08` по очереди. Перед каждым заданием исполнитель читает `AGENT_CONTRACT.md`; после каждого делает отчёт по указанному DoD и не продолжает при провале тестов. `09` — финальная комплексная проверка. Планировочный и критический проходы документированы в `PLANNER_TASK.md` и `CRITIC_REVIEW.md`.

Важно: текущая ветка **уже содержит** NativePanelResizeController, ADR-021…027, PanelChrome и tests. НЕЛЬЗЯ описывать это как отсутствующие компоненты и переписывать работающие решения вслепую. Особо важно: ADR-018 предписывает растяжение расстояний между иконками в полных рядах. Новая политика фиксированных промежутков **конфликтует** с ним, поэтому нужна явная новая ADR-замена, feature flag, миграция и проверки регрессий.

Референс — визуальная концепция, а не пиксельный технический контракт. Сохраняем свободное размещение панелей по выбору пользователя и возможность произвольного размера в legacy/manual mode, если не принято отдельное решение об ограничении.

---

## Статус выполнения (2026-09-22)

Промпты `00`–`08` выполнены на ветке `bugfix/workspace-grid` (от `rc/0.4.0` @ `dde28df`); `09`
(финальный релизный gate) — следующий шаг. Каждый этап выполнялся на изолированном worktree и мержился
`--no-ff`; после каждого мержа — полный `dotnet build` + `dotnet test`, затем code-review ∥ UX-review и
fix-цикл (не более 2 итераций), затем финальная верификация. Полная история — `git log --oneline dde28df..HEAD`.

| Этап | Артефакт(ы) | Merge-коммиты в `bugfix/workspace-grid` | Статус |
|---|---|---|---|
| `00-audit.md` | `docs/PLANS/workspace-grid-00-baseline.md` | `22faada` (аудит + characterization tests) | done |
| `01-architecture.md` | `adr/ADR-028-workspace-grid.md`, `adr/ADR-029-icon-layout-policy.md`, `docs/PLANS/workspace-grid-migration.md` | `0e98663`, enums `6270fc4` | done |
| `02-metrics.md` | `docs/PLANS/workspace-grid-02-metrics.md` | `11485a2`; fix 1 `f827bc9`; fix 2 `ee11ff5`; carry-over `e44cd00` | done (Approve) |
| P02b (persistence, схема v4→v5) | `docs/PLANS/workspace-grid-migration.md` §10 | `670363a`; fix 1 `7160a60`; fix 2 `72d228c`; null-guard `ae7961b` | done (Approve) |
| Стадия S (фундамент P03/P04) | `docs/PLANS/workspace-grid-03-04-integration.md` §1 | `60d0110` | done |
| `03-positioning.md` | `…-03-04-integration.md` §2 | `0b4582f`; fix 1 `b9edf71`; gate/диагностика `8831275` | done (Approve-with-debt) |
| `04-resize.md` | `…-03-04-integration.md` §3 | `3978da9`; fix 1 `c2e99d1`; committed-size `e44cd00` | done (Approve-with-debt) |
| `05-icon-layout.md` | ADR-029 «Реализация P05» | `9f9340e`; fix 1 `b6cf910`; fix 2 `d514fb4` | done (Approve) |
| `06-fit-content.md` | `docs/PLANS/workspace-grid-06-fit-content.md` | `0efc3b4`; fix 1 `917ba9e` | done (Approve) |
| `07-visual.md` | `docs/PLANS/workspace-grid-07-visual.md` | `2c30ea1`; fix 1 `ab60986`; fix 2 `f7ce361` | done (Approve-with-debt: WGD-01/02) |
| `08-icons.md` | `docs/DIAGNOSTICS/icon-loading-findings.md` | `a98d5d7`; fix 1 `50d5bd2`; fix 2 `0473481` | done (Approve) |
| `09-acceptance.md` | `docs/REVIEWS/workspace-grid-final.md` (+ `workspace-grid-debt.md`, `workspace-grid-manual-verify.md`) | `534ea3a` (BLOCKED), повторный gate `6a87a2c` | **GO with manual-verify gate** |

Входные артефакты для `09-acceptance.md`, подготовленные этим проходом (`architecture-documenter`,
2026-09-22): единый бэклог долга [`docs/REVIEWS/workspace-grid-debt.md`](../REVIEWS/workspace-grid-debt.md)
и сводная manual-verify матрица [`docs/REVIEWS/workspace-grid-manual-verify.md`](../REVIEWS/workspace-grid-manual-verify.md).
Оба ADR (028, 029) остаются в статусе `Proposed` до прохождения `09` — см. их шапки.
