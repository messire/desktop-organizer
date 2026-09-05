# ADR-002: MainWindow как Settings/Config окно

**Статус:** Accepted  
**Дата:** 2026-06-16

---

## Контекст

Сейчас `MainWindow` дублирует логику `ChildWindow` — это тоже панель с файлами, которая отображается на рабочем столе. Это создаёт несколько проблем:

- Роль `MainWindow` архитектурно неоднозначна
- `BaseViewModel.SaveFileList()` и `LoadSavedFiles()` пишут в единое поле `Properties.Settings.Default.SavedFiles`, конфликтуя с JSON-механизмом `WindowManager.SaveWindowsState()`
- Нет места для глобальных настроек приложения (цвета, шрифты, поведение)

После реализации ADR-001 на рабочем столе будут только `ChildWindow`. `MainWindow` должна иметь чёткую и отдельную роль.

---

## Решение

`MainWindow` становится **Settings/Config окном**. Запускается только через системный трей, не отображается при старте приложения, не является панелью файлов.

### Ответственность MainWindow

- Управление панелями: список всех `ChildWindow`, добавить / удалить / переименовать
- Глобальные настройки приложения: цвет фона панелей по умолчанию, шрифт, размер иконок, автозапуск с Windows
- Настройки конкретной панели: выбрать панель из списка → изменить заголовок, цвет, вид (list/wrap)

### Что убрать из MainWindow

- `FileItems`, `SelectedFile` — никаких файлов в Settings окне
- `SwitchViewCommand` — не нужен
- Drag & Drop файлов

### Что убрать из BaseViewModel

`SaveFileList()` и `LoadSavedFiles()` работают только через `Properties.Settings.Default.SavedFiles` и несовместимы с per-window JSON через `WindowManager`. Оба метода удалить из `BaseViewModel`. Сохранение файлов — исключительно ответственность `WindowManager.SaveWindowsState()`.

### Изменения в архитектуре

```
Трей иконка
  ├── "Настройки" → открыть MainWindow (Settings)
  └── "Выход"    → Shutdown

MainWindow (Settings)
  ├── Список панелей (ChildWindowModel[])
  │     ├── Добавить панель
  │     ├── Удалить панель  
  │     └── Выбрать → настроить
  └── Глобальные настройки
        ├── Дефолтный цвет фона
        ├── Дефолтный шрифт / размер
        └── Автозапуск с Windows

ChildWindow (панель на рабочем столе)
  └── Только отображение файлов + drag & drop
```

### Жизненный цикл

- При запуске приложения: `MainWindow` **не показывается**. Только трей + загрузка `ChildWindow` из сохранённого состояния.
- `TrayIconManager.OnShowMainWindowRequested` → показывает `MainWindow`
- `MainWindow.OnClosing` → `Hide()` вместо реального закрытия (приложение живёт в трее)

---

## Последствия

- `MainWindowViewModel` перестаёт наследоваться от `BaseViewModel` (там нет файлов)
- `BaseViewModel` используется только `ChildWindowViewModel`
- `MainWindowModel` можно удалить или переименовать в `AppSettingsModel`
- `WindowManager` остаётся единственным местом сохранения состояния панелей
