# Проход «Планировщик»: техническое задание на пакет промптов

## Исходная потребность
Референс Ambient Accent демонстрирует визуально единую систему панелей. В текущем скриншоте видно несовпадение ширин, вертикальных высот, плотности иконок, пустоту в single-item панелях, слабые подписи и неотрисованные значки. Обсуждение определило P0: глобальная workspace-геометрия и decoupling panel size от icon size; далее фиксированная icon-grid, fit-to-content, visual polish и icon-rendering.

## Границы
Не внедрять самостоятельно native rewrite, JSON migration, новый framework UI, новые продуктовые features. Текущий архив кода уже содержит NativePanelResizeController / PanelChrome / ADR-018 / тесты. Не объявлять эти элементы отсутствующими. Создать промпты с требованиями, testable DoD, зависимостями, выходными артефактами, разбором конфликтующих ADR и правилами обратной совместимости.

## План пакета
0. Аудит и baseline; 1. ADR на workspace grid и icon layout policy; 2. чистая геометрия + test matrix; 3. интеграция movement/snap/collision/monitor; 4. sizing/presets/native + roll-up; 5. fixed icon layout; 6. fit-content/legacy migration; 7. Ambient Accent polish; 8. icon diagnostic/reliability; 9. e2e comparison/release review.

## Условия приёмки пакета
Каждый промпт задаёт роль, область, конкретные точки входа в коде, деливераблы, DoD, тесты, out-of-scope, зависимости, rollback. Рецензент сверяет каждое требование из текущего обсуждения с одним или несколькими промптами и требует переделки при пропусках.
