# ADR-008: Shell COM — STA-поток и маршалинг PIDL-массивов

**Статус:** Accepted  
**Дата:** 2026-06-29

---

## Контекст

`SystemShellService` показывает нативное контекстное меню Shell через `IShellFolder.GetUIObjectOf` и
`IContextMenu` / `TrackPopupMenuEx`. В процессе реализации был воспроизведён краш
`System.AccessViolationException` при правом клике по иконкам в `SystemIconsPanelWindow`.

Анализ показал две независимые первопричины:

1. **Неверный маршалинг PIDL-массивов.** `IShellFolder.GetUIObjectOf` и `GetAttributesOf` объявляли
   параметр `apidl` как `IntPtr[]` без атрибута `[MarshalAs(UnmanagedType.LPArray)]`. CLR не формировал
   contiguous pointer array: Shell разыменовывал произвольную память → `AccessViolationException`.

2. **Вызов Shell COM не с STA-потока.** COM-интерфейсы Shell (`IShellFolder`, `IContextMenu`,
   `TrackPopupMenuEx`) требуют STA (Single-Threaded Apartment). WPF UI-поток является STA, но при
   попытке вызвать shell-операцию с фонового или MTA-потока поведение неопределено и может
   воспроизводить тот же класс ошибок.

---

## Решение

### 1. Обязательный `[MarshalAs(UnmanagedType.LPArray)]` для PIDL-массивов

В `NativeApi.cs` все P/Invoke-объявления, принимающие массив PIDL (`IntPtr[]`), должны иметь
атрибут `[In, MarshalAs(UnmanagedType.LPArray)]`:

```csharp
// IShellFolder.GetUIObjectOf
void GetUIObjectOf(
    IntPtr hwndOwner,
    uint cidl,
    [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
    ref Guid riid,
    IntPtr rgfReserved,
    out IntPtr ppv);

// IShellFolder.GetAttributesOf
void GetAttributesOf(
    uint cidl,
    [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
    ref uint rgfInOut);
```

Без этого атрибута CLR маршалирует управляемый массив как указатель на GC-heap объект, а не как
contiguous C-array указателей. Shell-API разыменовывает данные как массив PIDL-указателей → AV.

### 2. STA-контракт для всех shell COM-вызовов

**Инвариант:** все вызовы `IShellFolder`, `IContextMenu`, `TrackPopupMenuEx` и сопутствующих
Shell-API должны выполняться только на STA-потоке (UI-поток WPF).

Конкретные меры:

- `ShowShellContextMenu` в начале выполняет `Debug.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)`.
- Внешние вызывающие (ViewModel → Command → обработчик события) не должны маршалировать вызов
  на `Task.Run` или `ThreadPool`.
- При необходимости вызова из фонового потока — использовать `Dispatcher.Invoke` / `Dispatcher.InvokeAsync`.

### 3. Проверка HRESULT от `QueryContextMenu`

`IContextMenu.QueryContextMenu` может вернуть ошибку, если Shell не смог построить меню
(например, PIDL нарушен или объект недоступен). Возвращаемое значение проверяется:
отрицательный HRESULT → выход без показа меню, без исключения.

### 4. Управление временем жизни `childPidl`

`SHBindToParent` возвращает `childPidl` как указатель **внутрь** родительского PIDL-буфера.
Освобождать `childPidl` через `CoTaskMemFree` нельзя — это двойное освобождение части родительского
буфера. Освобождается только `parentPidl`. Это задокументировано комментарием в коде.

---

## Отклонённые альтернативы

### A: Выделенный STA-поток для Shell-операций

Создать один фоновый STA-поток (аналог COM STA apartment) и маршалировать все shell-вызовы на него.

**Отклонено:** усложняет архитектуру без ощутимой выгоды. WPF UI-поток уже является STA и
является правильным местом для shell-операций. Контекстное меню должно появляться на UI-потоке
в любом случае (оно создаёт HWND и гоняет message loop через `TrackPopupMenuEx`).

### B: `Marshal.GetFunctionPointerForDelegate` вместо `[MarshalAs]`

Передавать PIDL-массив через нативный буфер, сформированный вручную.

**Отклонено:** значительно больше кода, повышает риск утечек памяти. `[MarshalAs(UnmanagedType.LPArray)]`
покрывает задачу штатным механизмом CLR.

---

## Последствия

### Положительные

- `AccessViolationException` при правом клике устранён.
- Контракт «shell COM — только STA» явно задокументирован и охраняется `Debug.Assert`.
- 3 регресс-теста в `SystemShellServiceTests` фиксируют поведение и предотвращают возврат ошибки.

### Отрицательные / риски

- `Debug.Assert` срабатывает только в Debug-сборке. В Release нарушение контракта проявится
  только на Windows — необходима ручная проверка при рефакторинге кода вызова.
- Нативный слой (`IShellFolder`, `IContextMenu`) по-прежнему непроверяем вне Windows —
  регресс-тесты покрывают только managed-обёртку и мок-уровень.

---

## Amendment 2026-09-30 ([ADR-046](ADR-046-background-icon-loading.md))

Отклонённая альтернатива A выше («Выделенный STA-поток для Shell-операций») **принята — но только для
read-only извлечения иконок**: `IShellItemImageFactory`/`SHGetFileInfo`/системный image list
(`IconProvider`/`IconHandler`) теперь вызываются на `StaWorkerThread` — выделенном фоновом STA-потоке с
собственным `Dispatcher.Run()`-pump'ом, а не на UI-потоке. Причина пересмотра: контекстное меню (для
которого альтернатива A была отклонена) обязано создавать HWND и гонять `TrackPopupMenuEx` на том же
потоке, что показывает окно, — здесь же извлечение иконки не связано с UI напрямую, и цена
многосекундного зависания UI-потока на недоступной сетевой шаре (см. ADR-039 «Known follow-up») стала
неприемлемой. Показ контекстного меню (`ShowShellContextMenu`, этот ADR и ADR-040) остаётся на UI-потоке
без изменений.

## Manual verify (Win10 / Win11)

1. ПКМ по иконке в `SystemIconsPanelWindow` показывает нативное Shell-контекстное меню без краша.
2. ПКМ работает при быстрых повторных кликах (нет утечки COM-объектов или двойного освобождения).
3. ПКМ по нескольким разным иконкам последовательно не деградирует (утечка PIDL отсутствует).
4. Попытка вызвать контекстное меню с фонового потока (если такой путь кода когда-либо появится)
   должна быть заблокирована Assert ещё до обращения к COM.
