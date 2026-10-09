# План: единственный путь ресайза — native (ADR-022 C8)

**Статус (2026-10-10): Фазы 0–2 выполнены.** Фаза 0 — коммиты `e0b7fe8`, `36aece3` (+ ревью-фиксы `550a096`); гейт «native проверен» пройден
пользователем; Фаза 1 — коммит `bc60c8e` (Thumb-путь и ключ `NativePanelSizing` удалены, golden → regression-golden); Фаза 2 —
документация (ADR-022 Amendment «Фаза 1 выполнена», пометки ADR-005/021/026/030/037, `diagnostics-log-events.md`, матрицы,
ROADMAP п. 3, CHANGELOG `[0.9.0]`). **Остаётся известным:** MV-7 (порядок `WM_EXITSIZEMOVE` и восстановления rect при ESC —
только живая Windows); G-7/G-8/G-9 (Alt в цикле, hover на краю DO-052, курсоры DO-053); mouse-only (DO-054); мёртвый код
DO-050 (`NativeSizingCoordinateMapper`, `NativePanelResizeController.Detach()`); **в manual verify:** запуск сборки Фазы 1 со
старым `user.config`, содержащим `NativePanelSizing` (в т.ч. `False`) — §4. Текст ниже сохранён как исходный план.

**Ветка-основа:** `bugfix/adr-audit-fixes` @ a8e376e (build 0 warnings, 6221 тестов).
**Решение пользователя (2026-10-10):** оставить NATIVE путь (Win32 sizing loop), удалить WPF-путь через `Thumb`;
дефолт `NativePanelSizing` → `True`, затем ключ удаляется из `IAppSettings`/`Settings.settings`/`user.config`
(явное разрешение на этот ключ замороженного контура получено). Удаление старого пути — только после manual verify
native-пути пользователем на Win11.
**Scope: XL** (нативный слой + UI + ключ настроек), поэтому разбит на фазы и пакеты ниже. Закрывает ROADMAP
«Решения, ожидающие пользователя», п. 3.

---

## 0. Порядок и гейты

```
Фаза 0A (флип дефолта + скрыть тумблер) ─┐
Фаза 0B (закрыть пробелы native-пути)    ├─► сборка 0/0, тесты зелёные ─► ГЕЙТ: manual verify Win11 (пользователь)
Фаза 0C (визуальные грипы Edit Layout)   ─┘                                        │ «native OK»
                                                                                    ▼
                         Фаза 1 (удаление Thumb-пути и ключа) ─► Фаза 2 (ADR/ROADMAP/CHANGELOG)
```

- Фаза 0 — **не релиз**, а сборка для проверки. В Фазе 0 оба пути ещё живы, откат без перекомпиляции:
  `--no-native-sizing` или `DESKTOPORGANIZER_NATIVESIZING=0` (`NativeSizingSwitch.cs:43-65`).
- Фаза 1 стартует только после явного сообщения пользователя «native-путь проверен».
- Инварианты на всё время: `ResizeGeometryCalculator.cs` не меняется ни строкой (ADR-022 Invariants); новые
  P/Invoke — только в `INativeApi`/`Tools/NativeApi.cs`; `schemaVersion`, `Migrations/V*To*`, `JsonApplicationStateStore`,
  DTO, `UserConfigFolderMigration` — не трогаются (проверяется пустым `git diff` по этим путям).

---

## 1. Инвентаризация

### 1.1 Удалить (живёт только ради Thumb-пути)

| Артефакт | Где | ~строк | Фаза |
|---|---|---|---|
| `PanelResizeController` (весь класс) | `UI/Behaviors/PanelResizeController.cs` | 559 | 1 |
| `ThumbOverlayAffordance` | `UI/Behaviors/ThumbOverlayAffordance.cs` | 34 | 1 (логика Visibility переезжает в грипы, см. 1.3) |
| `NativeSizingSwitch` (env `DESKTOPORGANIZER_NATIVESIZING`, `--native-sizing`/`--no-native-sizing`) | `UI/Interaction/Native/NativeSizingSwitch.cs` | 66 | 1 |
| `ResizeOverlay` Grid + 8 `Thumb` + обработчики `Mouse.Got/LostMouseCapture`, `PreviewMouseLeftButtonDown` | `UI/Views/ChildWindow.xaml:649-723` | ~75 | 1 |
| Ветка `else` (Thumb) конструктора, `_thumbResizeController`, `Resize_Drag*`, `ResizeOverlay_*` | `ChildWindow.xaml.cs:103, 215-222, 280-315, 987-1032` | ~110 | 1 |
| `ResizeHandleStyle` (`TargetType="Thumb"`) | `Resources/Styles/Controls.xaml:384-425` | 42 | 1 (заменяется Border-стилем, 1.3) |
| `IAppSettings.NativePanelSizing` / `AppSettings.NativePanelSizing` | `Services/IAppSettings.cs:21-33`, `Services/AppSettings.cs:21-25` | 18 | 1 |
| `<Setting Name="NativePanelSizing">` / свойство в Designer | `Properties/Settings.settings:21-23`, `Settings.Designer.cs:159-169` | 14 | 1 |
| `GlobalDefaultsViewModel.NativePanelSizing`, `MainWindowViewModel.NativePanelSizing` | `GlobalDefaultsViewModel.cs:155-170`, `MainWindowViewModel.cs:200-208` | 25 | 1 |
| SettingsCard «Native resize (experimental)» | `Views/Settings/GeneralSection.xaml:63-71` | 9 | 0A — `Collapsed`, 1 — удалить |
| resx `Settings_General_NativeResize_Header/_Description` (en+ru) | `Localization/Strings.resx:741-746`, `Strings.ru.resx:741-746` | 12 | 1 (вместе с карточкой — иначе красный `KeyUsageCoverageTests`) |
| `IGestureDiagnostics.CaptureGot/CaptureLost` + реализация (единственный вызыватель — `ResizeOverlay_*`) | `IGestureDiagnostics.cs:119-139`, `GestureDiagnostics.cs:293-318` | ~45 | 1 |
| Тесты: `NativeSizingSwitchTests` целиком; `CaptureGot/Lost` кейсы в `GestureDiagnosticsTests.cs:379-382, 581-582`; `SetupProperty(s => s.NativePanelSizing, false)` в 8 STA-харнессах (`ChildWindowInitializeTests:340`, `ChildWindowHeaderMenuTests:203`, `ChildWindowContentSizingTests:981`, `ChildWindowSizingGridTests:570`, `ResizeOverlayGeometryTests:245`, `PanelChromeLayeredGeometryTests:328`, `DialogPresenterTests:139`, `WindowManagerCloneTests:503`) | tests | ~150 | 1 |

**Итого удаляется ≈ 950 строк src + ≈ 150–250 строк тестов (≈ 1.1–1.2 k), добавляется ≈ 150–250 (пробелы 0B, грипы, новые тесты).**

### 1.2 Оставить (общее для обоих путей / нужно native)

| Артефакт | Почему остаётся |
|---|---|
| `NativePanelResizeController` | единственный контроллер |
| `INativePanelSizingService`/`INativePanelSizingClient`/`NativeSizingEdge` (Abstractions), `NativePanelSizingService` (Services/Tools), `INativeApi` примитивы | транспорт Win32-сообщений (D-1/D-2) |
| `ResizeGestureSession`, `ResizeGeometryCalculator` (НЕ трогать), `ResizeConstraints`, `ResizeEdge` (+`AnchorsRight`/`AffectsHeight`) | общая геометрия; native вызывает `TryUpdate`→`Accumulate`/`Apply` синтетической дельтой |
| `ResizeConstraintsFactory` (`BuildConstraints`, `ResolveSizeGridStep`, `IsIconGridSnapActive`) | единое правило снапа (после WP-C) |
| `PanelSizingGridSupport`, `IPanelSizingGridHost`, `WorkspaceLayoutPolicy` | spans/anti-overlap/commit-нормализация (ADR-028/029) |
| `NativeHitTestMapper`, `NativeSizingEdgeMap`, `NativeSizingOriginResolver` | hit-test и калибровка `WM_SIZING` |
| `IResizeAffordance`, `NativeSizingAffordance`, `ResizeAffordanceGate` | гейт roll-up + политики ADR-026 (`SetAllowedByPolicy`) |
| `IPanelResizeController` | оставить как узкий шов `ChildWindow` (`AbortResize`/`SnapWidthToGrid`), обновить doc |
| `PanelMetrics.ResizeEdgeThickness/ResizeCornerThickness(+Length)` | единый источник геометрии native hit-test и грипов |
| `PART_HitCatcher` (`Panels.xaml:98-113`) | закрывает alpha=0 click-through и для `WM_NCHITTEST` (`RoundedRectClipConverter.cs:60-66`) |
| `ResizeEndReason` (все члены) | `Canceled` снова используется после G-4; `CaptureLost` — `SizeMoveAborted` |
| `NativeSizingCoordinateMapper` | мёртв в production (ADR-022 amendment 2026-10-09), но вне scope; опциональная чистка — отдельно |
| `ResizeEdgeParser.TryParse` | после Фазы 1 без production-вызовов (был только в `PanelResizeController.cs:148,241`); удаление с тестами — опционально |

### 1.3 Переписать

| Артефакт | Что сделать | Фаза |
|---|---|---|
| `ChildWindow` конструктор `:207-315` | одна ветка: `NativeSizingAffordance` (+грипы) → `ResizeAffordanceGate` → `RollUpBehavior` → `PanelDragMoveBehavior` → `NativePanelResizeController`; `_resizeController` типа `IPanelResizeController` = native | 1 |
| `ChildWindow.OnClosing:587` | `PanelClosing(_model?.Id, (Mouse.Captured as Thumb)?.IsDragging)` → флаг из `_interactionTracker.State == Resizing` (параметр `resizeActive`), обновить `diagnostics-log-events.md` | 1 |
| Визуальные грипы Edit Layout (G-3) | новый `ResizeGripVisuals` Grid (`IsHitTestVisible="False"`, та же сетка 8/*/8, 8 `Border`) + `ResizeGripVisualStyle` (`TargetType="Border"`, триггер `PanelTheme.IsLayoutEditing` → `PanelTheme.Accent`); видимость — через `NativeSizingAffordance(UIElement? gripVisuals)` | 0C |
| `ResizeOverlayGeometryTests`, `PanelChromeLayeredGeometryTests` T-V1, `ChildWindowSizingGridTests.NormalModeGesturesChange_ReAppliesTheResizeGate:389-413` | целиться в `ResizeGripVisuals` вместо `ResizeOverlay`; геометрия 8x8 углы / 6 края = полосы `NativeHitTestMapper` | 1 |
| Golden-эквивалентность | см. §5.3 — превратить в regression-golden только для native | 1 |
| XML-doc `cref` на удаляемые типы (иначе CS1574 → не 0 warnings) | src: `IGestureDiagnostics.cs:14`, `ChildWindow.xaml.cs:32,945,1005,1009`, `RollUpBehavior.cs:73`, `ResizeConstraintsFactory.cs:14,16`, `ResizeAffordanceGate.cs:6,39`, `PanelSizingGridSupport.cs:8`, `PanelDragMoveBehavior.cs:18,202,516`, `NativeSizingAffordance.cs:8`, `NativePanelResizeController.cs:128,222,633`, `IResizeAffordance.cs:8`, `IPanelSizingGridHost.cs:92`, `IPanelResizeController.cs:6,9`, `AdaptiveIconGridPanel.cs:88`, `ResizeGestureSession.cs:42,163`, `PanelInteractionState.cs:36`, `PanelInteractionTracker.cs:7`, `IAppSettings.cs:25,31`; tests: `ChildWindowInitializeTests.cs:48,49,165,261`, `NativeSizingAffordanceTests.cs:9`, `GestureDiagnosticsTests.cs:465`, `PanelSizingGridSupportNotifyHeightResizeCommittedTests.cs:10`, `ResizeGeometryCharacterizationTests.cs:10-16,306-307` | 1 |
| Комментарии «Thumb path still exists» и т. п. | `NativePanelSizingService.cs:162`, `DiConfig.cs:169`, `ChildWindow.xaml:1-7`, `ResizeEdge.cs:4-54`, `ResizeGestureSession.cs:15-21`, `LayoutEditModeBehavior.cs:21`, `RollUpBehavior.cs:114,164,208`, `Panels.xaml:103-109`, `RoundedRectClipConverter.cs:29-66`, `PanelMetrics.cs:33-90,332-348`, `NativePanelResizeController.cs:568-590` («duplicated by precedent» — дубликата больше нет) | 1 |

---

## 2. Пробелы native-пути (проверено по коду)

Класс: **B** — блокирует Фазу 1 (закрыть в 0B/0C до manual verify); **S** — сделать в 0B (дёшево, улучшает паритет);
**V** — только manual verify, фикс по результату; **A** — принять, задокументировать.

| # | Пробел | Код (file:line) | Класс | Решение |
|---|---|---|---|---|
| G-1 | `WM_DPICHANGED` во время жеста **обрывает** сессию (`SourceChanged`, deferPersist); остаток жеста — «сирота»: `ProposeBounds` при `!IsActive` возвращает `proposed` без снапа (`:403-414`), `WM_EXITSIZEMOVE` ничего не персистит | `NativePanelResizeController.cs:543-552`; сервис `NativePanelSizingService.cs:452-478` (handled=false → WPF применяет suggested rect) | **B** | Не обрывать: геометрия сессии в desktop-DIP (`DesktopScaleModel`, не зависит от DPI окна), поэтому жест продолжается. Снимок `_desktopScale.Current` фиксировать на `SizeMoveEntered` и использовать во всех `ToDesktopDip/ToNativePx` жеста. `ChildWindow.OnDpiChanged` уже пропускает deferred re-apply при `tracker != Idle` (`ChildWindow.xaml.cs:575`) — оставить. Если manual verify покажет дрожание от suggested-rect WPF → fallback: оборвать жест **и** прервать системный цикл (механизм G-2). Это амендмент ADR-037 «native лишь обрывает жест». |
| G-2 | `AbortResize` завершает только сессию — системный модальный цикл продолжает ресайзить окно без снапа/персиста. Thumb-путь отпускал capture (`PanelResizeController.cs:473, 554-558`) | `NativePanelResizeController.cs:560`; вызовы `ChildWindow.xaml.cs:592, 1045, 1488` (dispatcher-операции — смена режима/страницы, shell recovery — выполняются внутри вложенного цикла) | **S** | Токен `Attach` получает `CancelActiveSizing()` (Abstractions: `INativePanelSizingAttachment : IDisposable` или метод сервиса `Cancel(hwnd)`), реализация — `ReleaseCapture`/`SendMessage(WM_CANCELMODE)` через **новый** метод `INativeApi`. `AbortResize` внутри активного цикла: cancel-loop + `EndResize(reason, deferPersist: true)`. |
| G-3 | Видимые accent-грипы Edit Layout (ADR-026 D-6) существуют только как `Thumb`-шаблон; на native-пути `ResizeOverlay` свёрнут → в Edit Layout грипов **нет** | `Controls.xaml:405-425`; `ChildWindow.xaml.cs:242`; ADR-026:91-93 | **B** | `ResizeGripVisuals` (visual-only, §1.3). Видим, когда гейт разрешает resize; цвет — тот же триггер `IsLayoutEditing`. |
| G-4 | R-8: ESC в цикле = обычное завершение. Система восстанавливает исходный rect, но `PersistAfterGesture` выполняется: `NotifyHeightResizeCommitted` **демотирует AutoContent→Manual** (`:795`), `NormalizeCommittedSize` может сдвинуть off-lattice панель; `canceled` всегда false (`:733`) | `NativePanelResizeController.cs:537, 733, 760-796`; `ResizeGestureSession.cs:286` | **S** | В `SizeMoveExited(finalRect)`: если `finalRect == _startWindowRectPx` → `End(Canceled)` без `PersistAfterGesture`, `canceled: true` в диагностике. Частично снимает R-8 (амендмент ADR-022). |
| G-5 | `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` для **любого** клика по панели, не только по краю. В режиме E (child `WorkerW`) это меняет активацию по сравнению с сегодняшним дефолтом (клик по панели больше не активирует рабочий стол) → риск для клавиатуры (F2/Delete/Esc), закрытия меню по клику вне | `NativePanelSizingService.cs:303-307` | **B** | Отвечать `MA_NOACTIVATE` только если `LOWORD(lParam)` — sizing HT-код (10–17); иначе `handled=false` (поведение как без хука). Поправить `NativePanelSizingServiceTests.WmMouseActivate_AlwaysAnswersNoActivate:195`. |
| G-6 | Двойной клик по верхнему/нижнему краю (`WM_NCLBUTTONDBLCLK` + `HTTOP/HTBOTTOM`) → `DefWindowProc` может сделать «вертикальный maximize» (Aero Snap), то же — перетаскивание верхнего края к верху экрана. Thumb-путь этого не умел. Окно — `ResizeMode` по умолчанию (`CanResize`) | хук не обрабатывает `WM_NCLBUTTONDBLCLK`; `ChildWindow.xaml:29-33` | **S** + V | Проглатывать `WM_NCLBUTTONDBLCLK` на sizing HT-кодах (handled=true). Snap «к верху экрана» — manual verify (режим T), контрмера по результату — win32-interop-specialist. |
| G-7 | Alt (free resize) читается через `Keyboard.Modifiers` внутри вложенного цикла при неактивном окне | `ResizeGestureSession.cs:311-322` | V | Если не работает — native передаёт в сессию seam `isFreeResizeModifierDown` на `GetAsyncKeyState(VK_MENU)` через `INativeApi` (seam уже есть, `NativePanelResizeController.cs:264`). |
| G-8 | Hover-состояние панели: край — non-client, `PanelRoot.IsMouseOver` = false над полосой ресайза (у Thumb было true) → граница/хром «гаснут» при наведении на край | `ChildWindow.xaml:47`, `PanelVisualStateBehavior.cs:37` | V | Косметика; фикс только если заметно (TrackMouseEvent `TME_NONCLIENT` или флаг из `HitTest`). |
| G-9 | Курсоры: у Thumb задан `Cursor=SizeNS/…` (`ChildWindow.xaml:700-722`), native полагается на `DefWindowProc` `WM_SETCURSOR` для HT-кодов | — | V | Проверить 8 курсоров. |
| G-10 | `onResizeGestureStarting` (коммит rename, сброс выделения) на native вызывается до `TryBegin` | `NativePanelResizeController.cs:338-340` vs `PanelResizeController.cs:205-211` | A | Задокументировано в коде; `WM_ENTERSIZEMOVE` приходит только после принятия цикла системой. |
| G-11 | После Фазы 1 нет запасного пути: если `Attach` не удался — панель без ресайза | `NativePanelSizingService.cs:160-166` | A | Лог `AttachFailed` есть; HwndSource у WPF-окна всегда существует. Обновить комментарий. |

**Проверено — не пробел:** клавиатурный ресайз (Thumb не фокусируем — не было ни в одном пути; системный цикл даже
добавляет стрелки); hit-test поверх `PART_HitCatcher` (ADR-030 D-5 покрывает оба пути, `RoundedRectClipConverter.cs:60-66`);
ресайз свёрнутой панели (запрещён в обоих: `RollUpBehavior`→`IResizeAffordance`, `NativePanelResizeController.HitTest:320`);
Folder Portal (нет resize-специфичного кода, grep `Behaviors/` — только `PanelItemDragDataFactory`); Edit Layout/lock
(общий `ResizeAffordanceGate`); снап icon-grid/GridSize/workspace-grid/anti-overlap (`ResizeConstraintsFactory`,
`ResizeGestureSession.TryUpdate`, `PanelSizingGridSupport` — общие); `SnapWidthToGrid` (копия в native `:563-594`).
**ADR-049 vs sizing loop:** оба хука на одном `HwndSource`, ни один не трогает чужие сообщения; Z-order-фильтр не
ставит `handled`, кадры цикла идут с `SWP_NOZORDER` → горячий путь (`DesktopIntegrationService.cs:324-328`), режим E
не затрагивается (`:333`); попытка подъёма в начале цикла в режиме T будет ветирована (`InBand`). Конфликта по коду нет —
только пункт manual verify.

---

## 3. Фазы

### Фаза 0A — дефолт `True`, тумблер скрыт (ключ остаётся для отката)

| Шаг | Файл | Изменение |
|---|---|---|
| 0A-1 | `Properties/Settings.settings:21-23` | `False` → `True` |
| 0A-2 | `Properties/Settings.Designer.cs:161` | `DefaultSettingValueAttribute("True")` (генератор SDK-сборкой не запускается — правка руками, синхронно с .settings) |
| 0A-3 | `Views/Settings/GeneralSection.xaml:63-71` | `Visibility="Collapsed"` на `SettingsCard` (resx остаются ссылаемыми → `KeyUsageCoverageTests` зелёный; откат — один атрибут) |
| 0A-4 | `IAppSettings.cs:21-33` | doc: дефолт `True`, переходное состояние до удаления |

Важно: у пользователя, когда-то сохранившего `False` в `user.config`, сохранённое значение перекрывает дефолт → он
остаётся на Thumb-пути, а тумблер скрыт. Это сознательно (не меняем семантику загрузки); для проверки см. MV-0.

### Фаза 0B — пробелы native-пути (`win32-interop-specialist`)

G-1, G-2, G-4, G-5, G-6 из §2. Файлы: `NativePanelResizeController.cs`, `NativePanelSizingService.cs`,
`INativePanelSizingService.cs` (Abstractions), `INativeApi.cs`/`NativeApi.cs` (только для G-2). `ResizeGeometryCalculator.cs`
не трогать.

### Фаза 0C — визуальные грипы (`wpf-ui-builder`)

G-3: `ResizeGripVisuals` в `ChildWindow.xaml` (рядом с `ResizeOverlay`, `Grid.RowSpan="2"`, `IsHitTestVisible="False"`),
`ResizeGripVisualStyle` в `Controls.xaml`, `NativeSizingAffordance(UIElement? gripVisuals = null)`. В native-ветке
конструктора `ChildWindow` передать грипы; в Thumb-ветке грипы `Collapsed` (там работает старый `ResizeHandleStyle`).

### ГЕЙТ — manual verify на Windows 11 (пользователь)

| # | Сценарий | Ожидание |
|---|---|---|
| MV-0 | Активен ли native: в `user.config` (`%LOCALAPPDATA%\khagendev\...`) нет `NativePanelSizing=False` (иначе удалить строку или запуск с `--native-sizing`); в диагностическом логе ресайз = `SizeMoveEntered` + `captureOwner=NativeSizingLoop`. Записать режим слоя (E/T) из лога `DesktopScaleModel` | native активен |
| MV-1 | Ресайз за каждый край и угол (8), курсор соответствует краю (G-9) | противоположный край неподвижен, курсоры верные |
| MV-2 | Snap к icon-grid (Grid view, `SnapResizeToIconGrid`=on), `GridSize`>1, workspace-grid (`GridAligned` панель) | шаги совпадают с прежним поведением; после отпускания нет «доскока» |
| MV-3 | Alt во время ресайза (`GridAligned` и `GridSize`>1) (G-7) | свободный ресайз, на отпускании — нормализация к решётке |
| MV-4 | Roll-up: свёрнутую панель нельзя ресайзить; после разворота — можно | — |
| MV-5 | Anti-overlap ADR-017 (`GridAligned`): ресайз в соседа | упор в слот соседа, без наложения |
| MV-6 | Normal (MoveOnly): края не ресайзят; Edit Layout: ресайз + видимые accent-грипы (G-3); Lock: ресайза нет, в Edit Layout не войти; смена Normal-жестов в настройках — без рестарта | — |
| MV-7 | ESC внутри цикла (R-8, G-4): панель возвращается к исходному размеру; AutoContent-панель (Top/Bottom) не становится Manual; после рестарта размер исходный | — |
| MV-8 | Mixed-DPI (100 % + 150 %): ресайз через границу мониторов (`WM_DPICHANGED`, G-1) — жест не обрывается, снап работает до конца, размер сохраняется после рестарта; перетаскивание панели между мониторами после ресайза | без скачков/дрожания |
| MV-9 | ADR-049 (режим T): во время и после ресайза панель остаётся под окнами приложений, не «мигает» поверх; Win+D не ломается | — |
| MV-10 | Активация/клавиатура (G-5): после клика по панели и по краю — F2-rename, Delete, Enter, Esc (выход из Edit Layout), закрытие контекстного меню кликом мимо работают как раньше; фокус активного приложения при ресайзе за край не отнимается | — |
| MV-11 | Двойной клик по верхнему/нижнему краю; тащить верхний край к верху экрана (G-6) | нет вертикального maximize/Aero Snap |
| MV-12 | Hover у края (G-8), правый клик по краю (нет системного меню) | косметика приемлема |
| MV-13 | Рестарт приложения, перезапуск Explorer (shell recovery), смена страницы, clone панели, Folder Portal — затем ресайз | работает, размеры сохраняются |

### Фаза 1 — удаление Thumb-пути и ключа `NativePanelSizing` (после «native OK»)

| Шаг | Что | Пакет |
|---|---|---|
| 1-1 | `ChildWindow` — одна ветка (§1.3), удалить `ResizeOverlay`, обработчики, `_thumbResizeController`, `ResizeHandleStyle`, неиспользуемые `using` | P1-A |
| 1-2 | Удалить `PanelResizeController`, `ThumbOverlayAffordance`, `NativeSizingSwitch`, `CaptureGot/CaptureLost`; `PanelClosing` → `resizeActive` | P1-A |
| 1-3 | Удалить `NativePanelSizing` из `IAppSettings`, `AppSettings`, `Settings.settings`, `Settings.Designer.cs`, двух VM, `GeneralSection.xaml`, resx en+ru | P1-A |
| 1-4 | Все `cref`/комментарии из §1.3 | P1-A |
| 1-5 | Механически починить компиляцию тестов (8 харнессов, `GestureDiagnosticsTests`, удалить `NativeSizingSwitchTests`) | P1-A |
| 1-6 | Переписать геометрические тесты на `ResizeGripVisuals`, golden §5.3, новые регрессионные тесты §5.2 | P1-B |

### Фаза 2 — документация

Амендмент ADR-022 (Rollout/Invariants: флага нет, единственный путь; D-6/ADR-037: DPI mid-gesture продолжается;
R-8 частично — ESC при неизменном rect = Canceled; MOUSEACTIVATE только на краях; DBLCLK на краях проглатывается;
golden → regression-golden); короткие пометки в ADR-021 (`Canceled` теперь от native ESC, Thumb-capture-семантика
историческая), ADR-026 (грипы visual-only), ADR-030 (`PART_HitCatcher` комментарий про Thumbs), ADR-037 (DPI);
ROADMAP: «Решения, ожидающие пользователя» п. 3 → закрыт; CHANGELOG; `docs/Refactoring/Testing/resize-repro-matrix.md`,
`edit-layout-manual-matrix.md`, `diagnostics-log-events.md` (нет `CaptureGot/CaptureLost`, `PanelClosing.resizeActive`).

---

## 4. Удаление ключа `NativePanelSizing` — поведение старого `user.config`

| Вопрос | Ответ |
|---|---|
| Лишний `<setting name="NativePanelSizing">` при загрузке | `LocalFileSettingsProvider.GetPropertyValues` перебирает только объявленные `SettingsProperty` класса `Settings`; неизвестный элемент секции игнорируется молча — без исключения и без `SettingsLoaded`-ошибки. |
| При `Save()` | `ClientSettingsStore.WriteSettings` заменяет/добавляет только переданные настройки; чужие элементы секции не трогаются → «осиротевший» ключ остаётся в файле навсегда. Это безвредно. **Не чистить** — чистка = изменение семантики сохранения замороженного контура. |
| `Upgrade()`/`GetPreviousVersion` | в проекте не вызываются (grep: только `AppSettings.Save()` в `AppSettings.cs:147`) — пути копирования прошлых версий нет. |
| `UserConfigFolderMigration` | копирует файл как есть, ключ переезжает сиротой; **не трогать**. |
| Downgrade на сборку с ключом | старая сборка читает сироту (если пользователь когда-то переключал) или свой дефолт `False` → Thumb-путь; геометрия в JSON не затронута этим изменением. **Поправка 2026-10-10:** текущая схема — `schemaVersion = 7` (с коммита `5633bb4`, ADR-024 Amendment 2026-10-10), а не 6; сборки ≤0.8.0 открывают v7-файл как пустую read-only сессию (rung 7) независимо от этого ключа — см. Upgrade notes в `CHANGELOG.md`. |
| `Settings.Designer.cs` | правится руками синхронно с `.settings` (SingleFileGenerator в SDK-сборке не запускается). |
| Тесты настроек | отдельных тестов на ключ нет; добавить: Фаза 0 — `Settings.Default.Properties["NativePanelSizing"].DefaultValue == "True"` (паттерн `SizeStepPolicyTests.cs:99`); Фаза 1 — `Settings.Default.Properties["NativePanelSizing"] is null`. Ручная проверка (**вынесена в manual verify**, ADR-022 Amendment «Фаза 1»): запуск сборки Фазы 1 со старым `user.config`, содержащим ключ (в т.ч. `NativePanelSizing=False`) → без ошибок, остальные настройки (`GridSize`, `Language`, …) на месте, ресайз native, элемент остаётся в файле нетронутым. Фактически добавленный тест — `NativePanelSizingKeyRemovedTests` (объявленная поверхность настроек). |
| Чего НЕ касаемся | `schemaVersion`, миграции, DTO, `JsonApplicationStateStore`, остальные ключи `IAppSettings`, пути `%LOCALAPPDATA%`. |

---

## 5. Тесты

### 5.1 Фаза 0 (новые/изменённые)

| Тест | Проверяет |
|---|---|
| `NativePanelResizeControllerTests`: `DpiChanged_MidGesture_KeepsSessionActive_NextProposeBoundsStillSnapped_ExitPersistsOnce` | G-1 |
| `…: DpiChanged_MidGesture_UsesGestureStartScaleSnapshot` | G-1 (снимок масштаба на жест) |
| `…: AbortResize_DuringActiveCycle_RequestsLoopCancel_AndDefersPersist` | G-2 |
| `NativePanelSizingServiceTests`: `CancelActiveSizing_CallsNativeApi_OnlyWhileCycleActive`, idempotent | G-2 |
| `…: SizeMoveExited_FinalRectEqualsStart_EndsCanceled_NoPersist_NoAutoContentDemotion` | G-4 |
| `NativePanelSizingServiceTests`: `WmMouseActivate_OnSizingHitCode_NoActivate` / `…_OnClientHitCode_NotHandled` (заменяет `:195`) | G-5 |
| `…: WmNcLButtonDblClk_OnTopBottomEdge_Swallowed` / `…_OnClient_NotHandled` | G-6 |
| STA: `ResizeGripVisuals` геометрия = полосы `NativeHitTestMapper`; видимость следует за гейтом/roll-up; `IsHitTestVisible == false` | G-3 |
| `SettingsDefaultsTests` (или в существующий): дефолт `NativePanelSizing == "True"` | 0A |

### 5.2 Фаза 1

| Тест | Действие |
|---|---|
| `NativeSizingSwitchTests` | удалить |
| `ResizeOverlayGeometryTests` → `ResizeGripVisualsGeometryTests` | переписать на `Border`-грипы (углы 8x8 без клипа, края 6) |
| `PanelChromeLayeredGeometryTests.ResizeOverlay_SpansWholeWindow…:50` | целиться в `ResizeGripVisuals` |
| `ChildWindowSizingGridTests.NormalModeGesturesChange_ReAppliesTheResizeGate:389` | наблюдать `ResizeGripVisuals.Visibility` (+ при необходимости internal-аксессор `NativeSizingAffordance.IsEnabled`) |
| 8 STA-харнессов | убрать `SetupProperty(NativePanelSizing)`; `ChildWindow` теперь всегда строит native-контроллер — `Mock.Of<INativePanelSizingService>().Attach` → null, безопасно |
| `GestureDiagnosticsTests` | убрать `CaptureGot/CaptureLost`; `PanelClosing(resizeActive)` |
| Новый STA: `ChildWindow_Constructs_NativeResizeController_Unconditionally` | нет пути выбора |
| Новый: `Settings.Default.Properties["NativePanelSizing"] is null` | ключ удалён |

### 5.3 Golden-эквивалентность

- `ResizeGeometryCalculatorApplyEquivalenceTests` — **оставить**: это свойство самого калькулятора («Apply не зависит от
  того, как получен raw»), а native-путь по-прежнему вызывает `Accumulate` синтетической дельтой. Обновить только doc.
- «Honest golden equivalence» в `NativePanelResizeControllerTests` (`LegacyApply`, `:418-583, 731`) и
  `NativePanelResizeControllerGridAlignedTests` — **превратить в regression-golden только для native**: `LegacyApply` →
  `ExpectedPipelineOracle` (чистый `Accumulate→Apply→SnapBounds`), формулировки «matches legacy path» → «matches pure
  pipeline». Кейсы и ненулевой origin-offset сохраняются.
- `ResizeGeometryCharacterizationTests` — оставить (пиннинг математики), `cref` → `<c>`.

---

## 6. Пакеты

| Пакет | Агент | Фаза | Содержимое | Параллельно с |
|---|---|---|---|---|
| P0-A | `wpf-ui-builder` | 0A | шаги 0A-1…4 + тест дефолта | P0-B |
| P0-B | `win32-interop-specialist` | 0B | G-1, G-2, G-4, G-5, G-6 (+ метод `INativeApi` для G-2) | P0-A, P0-C |
| P0-C | `wpf-ui-builder` | 0C | G-3 грипы | P0-B (с P0-A — тем же агентом последовательно или отдельным worktree) |
| P0-T | `tdd-test-engineer` | 0 | тесты §5.1, сборка 0/0 | после P0-A/B/C |
| — | пользователь | гейт | MV-0…MV-13 | — |
| P1-A | `wpf-ui-builder` | 1 | шаги 1-1…1-5, атомарно (src + компиляция тестов) | — |
| P1-B | `tdd-test-engineer` | 1 | §5.2, §5.3 | после P1-A |
| P2 | `architecture-documenter` | 2 | ADR-022 амендмент и пр. (§3 Фаза 2) | после ревью |

Ревью после Фазы 0 и после Фазы 1: `code-quality-reviewer` ∥ `fences-ux-parity-reviewer`.

---

## 7. Definition of Done

**Фаза 0**
1. Дефолт `NativePanelSizing = True` в `.settings` и Designer; тест пиннит.
2. Карточка тумблера `Collapsed`, resx на месте; `KeyUsageCoverageTests` зелёный.
3. G-1, G-2, G-3, G-4, G-5, G-6 реализованы и покрыты тестами §5.1; `git diff -- src/DesktopOrganizer.UI/Interaction/ResizeGeometryCalculator.cs` пуст.
4. `dotnet build` и `dotnet test` по solution: 0 errors, 0 warnings, тесты зелёные (≥ 6221 + новые).
5. Пользователю передан список MV-0…MV-13.

**Фаза 1**
6. `grep -rnwE "PanelResizeController|ThumbOverlayAffordance|NativeSizingSwitch|NativePanelSizing|ResizeOverlay|Resize_Drag|native-sizing|NATIVESIZING|ResizeHandleStyle|CaptureGot|CaptureLost" src tests` → 0 совпадений, **с границами слова (`-w`/`\b`)**, иначе regex ловит легитимные `NativePanelResizeController`, `IPanelResizeController` (подстрока `PanelResizeController`), `ResizeEndReason.CaptureLost` (член enum, жив: `SizeMoveAborted`) и `NativePanelSizingKeyRemovedTests` (тест на отсутствие ключа; подстрока `NativePanelSizing`). (С `-w` остаются ровно два вида легитимных совпадений: `ResizeEndReason.CaptureLost` и строковые литералы `"NativePanelSizing"` в `NativePanelSizingKeyRemovedTests`; без `-w` — ещё три подстроки выше.) Исключения, которые допустимо оставлять: `IPanelResizeController`, `NativePanelResizeController`, `ResizeEndReason.CaptureLost`, `NativePanelSizingKeyRemovedTests` (и строковые литералы `"NativePanelSizing"` в нём), а также упоминания в комментариях/XML-doc с пометкой «removed»/«former» и исторические docs/ADR. Проверено на HEAD `f8c88c8` по `src/`, `installer/`, `.github/`: ни одного `NativePanelSizing`/`native-sizing`/`NATIVESIZING`.
7. `git diff` по `Migrations/`, `JsonApplicationStateStore`, `StateEnvelope`, DTO, `UserConfigFolderMigration` пуст; `schemaVersion` не менялся этой задачей (**текущее значение — 7** с `5633bb4`; «= 6» в исходной редакции плана устарело).
8. Golden переведён в regression-golden (§5.3); `ResizeGeometryCalculator.cs` не изменён.
9. Build/test 0/0, без `#pragma`/`SuppressMessage`/`NoWarn`.
10. Удалено ≈ 1.1–1.2 k строк (отчёт `git diff --stat`).

**Фаза 2**
11. ADR-022 амендмент + пометки ADR-021/026/030/037; ROADMAP п. 3 закрыт; CHANGELOG; матрицы и `diagnostics-log-events.md` обновлены.

---

## 8. Риски

**Статус на 2026-10-10 (после Фазы 2):** закрыты — сохранённый `False` (ключ удалён; разбор старого файла — manual verify),
случайный релиз Фазы 0, забытые `cref` (CS1574), golden-тесты (→ regression-golden). Остаются **известными**: порядок
`WM_EXITSIZEMOVE`/rect при ESC (MV-7), mixed-DPI дрожание ≤ 1 кадр (MV-8), Aero Snap в режиме T (G-6, MV-11), Alt/ESC при
неактивном окне (G-7), hover на крае (G-8, DO-052), курсоры (G-9, DO-053), один проверенный режим слоя (E или T), нет запасного
пути (G-11, лог `AttachFailed`), mouse-only (DO-054). Таблица ниже — исходная.

| Риск | Вер. | Влияние | Митигация |
|---|---|---|---|
| Сохранённый `False` в `user.config` — проверка идёт по Thumb-пути | сред. | ложный «OK» | MV-0 |
| WPF применяет suggested rect `WM_DPICHANGED` внутри цикла → дрожание (G-1) | сред. | UX на mixed-DPI | MV-8; fallback: оборвать жест + прервать цикл (G-2) |
| Aero Snap / вертикальный maximize в режиме T (G-6) | низ.–сред. | панель «прыгает» на всю высоту | DBLCLK глотаем; MV-11 → контрмера win32 |
| Alt/ESC не доходят до цикла при неактивном окне (G-7, R-8) | низ. | нет free-resize/отмены | MV-3/MV-7; `GetAsyncKeyState` seam |
| На машине пользователя проверяется только один режим слоя (E или T) | выс. | другой режим не проверен вручную | код режим-независим; MV-0 фиксирует режим, остаток — в ADR как known limitation |
| Нет запасного пути после Фазы 1 (G-11) | низ. | панель без ресайза | лог `AttachFailed`; откат — предыдущий rc |
| Забытые `cref` → CS1574 | выс. | красный 0-warnings гейт | список §1.3, DoD-6 grep |
| Golden-тесты теряют смысл после удаления legacy | сред. | ослабление регресс-сети | §5.3: оракул чистого конвейера |
| Hover гаснет на крае (G-8) | выс. | косметика | MV-12, фикс по запросу |
| Случайный релиз Фазы 0 | низ. | пользователи с `False` остаются на Thumb | Фаза 0 — не релиз; Фаза 1 сразу за гейтом |

**Нативное / не проверяемое вне Windows:** весь цикл `DefWindowProc` (G-1, G-2 эффект `WM_CANCELMODE`, G-5 активация,
G-6 snap, G-7 клавиатура, G-8, G-9, ADR-049) — только MV-список; `dotnet test` покрывает декодирование сообщений и
политику контроллера через seam'ы.
