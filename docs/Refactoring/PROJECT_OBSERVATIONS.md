# Project Observations Supporting This Plan

These are concrete observations from the uploaded source tree, not generic recommendations.

1. `ChildWindow.xaml` contains eight transparent resize `Thumb`s in `ResizeOverlay` with `DragStarted/DragDelta/DragCompleted` handlers.
2. `PanelResizeController` owns raw width/height/right/bottom accumulators and grid sizing, but currently has no explicit `_isResizing` or lost-capture cleanup path.
3. Title-bar drag does have a `LostMouseCapture` path (`ChildWindow.xaml` -> `TitleBar_LostMouseCapture` -> `PanelDragMoveBehavior`).
4. `App.xaml.cs` owns `DesktopOrganizerSingleInstanceMutex` only as installer detection; `docs/REFACTORING_PLAN.md` explicitly notes that actual single-instance ownership is still open.
5. `AppSettings` is a thin adapter over `Properties.Settings.Default` and currently stores both scalar preferences and serialized structural values such as `Pages`, `LayoutSnapshots`, and `SystemIconsPanel`.
6. Existing refactoring docs already call out hardcoded colors in `MainWindow.xaml`, missing explicit `RaiseCanExecuteChanged`, and incomplete native-service unit coverage.
7. Existing architecture deliberately centralizes P/Invoke behind native abstractions. Any C/C++ introduction should preserve that boundary instead of scattering native calls into UI code.

This package deliberately extends those existing directions rather than replacing the project architecture wholesale.
