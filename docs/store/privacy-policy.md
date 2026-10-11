# DesktopOrganizer — Privacy Policy

_Last updated: 2026-10-10. Applies to DesktopOrganizer 1.0 and later as distributed through the Microsoft Store._

> Черновик для публикации на публичной странице (AP2 в `docs/PLANS/store-first-publication.md`). Замените
> `<SUPPORT-EMAIL>` и `<PUBLIC-URL>` перед публикацией. Русская версия — ниже.

## Summary

DesktopOrganizer does not collect, store, transmit or sell any personal information. It has no accounts, no
telemetry, no analytics, no advertising and no cloud component. Everything the app does happens on your
computer.

## What the app does with your data

- **Panels and layout.** Your pages, panels and their settings are saved as JSON files in a folder inside your
  own Windows user profile (`%USERPROFILE%\DesktopOrganizer\State`). Nothing leaves your computer.
- **Files you drag into a panel.** When you drop a file or folder from the desktop into a panel, the app *moves*
  it into `%USERPROFILE%\DesktopOrganizer\PanelItems` so that it no longer shows on the desktop. Removing the
  item from the panel moves it back. These are your files; the app never reads their contents and never
  uploads them anywhere. The folder stays in place if you uninstall or reset the app.
- **Folder Portals.** A panel can show the contents of a folder you choose. The app only lists the folder and
  requests icons from Windows; it does not read or index file contents.
- **Diagnostics log.** The app keeps a small rotating log of errors and start/stop events in
  `%USERPROFILE%\DesktopOrganizer\Logs`, in English, with file paths and your Windows user name removed
  before anything is written. The log is created only on your computer and is never sent anywhere
  automatically. You decide whether to attach it to a support request.
- **Preferences.** Interface language and a few one-time hints are stored in the standard per-user .NET
  application settings location under `%LOCALAPPDATA%`.

## Network access

The Microsoft Store build of DesktopOrganizer makes no network connections. Updates are delivered by the
Microsoft Store itself.

## Permissions

DesktopOrganizer is a classic desktop (Win32) application packaged for the Microsoft Store and therefore runs
with the `runFullTrust` capability. It uses that access only to:

- place its panels in the desktop layer behind your other windows,
- detect mouse gestures on the desktop background (for example, a double-click to hide or show desktop icons,
  or a right-drag to create a panel) — it does not record what you type or click elsewhere,
- show and hide the standard desktop icons when you ask it to,
- show a tray icon and, if you enable it in Windows Settings, start when you sign in.

## Children

DesktopOrganizer is a general-purpose utility, is not directed at children and collects no data from anyone.

## Changes

If this policy changes, the new version is published at `<PUBLIC-URL>` with an updated date.

## Contact

Questions about privacy or the app: `<SUPPORT-EMAIL>`.

---

# DesktopOrganizer — Политика конфиденциальности

_Обновлено: 2026-10-10. Относится к DesktopOrganizer 1.0 и новее, распространяемым через Microsoft Store._

## Кратко

DesktopOrganizer не собирает, не хранит, не передаёт и не продаёт персональные данные. В приложении нет
учётных записей, телеметрии, аналитики, рекламы и облачной части. Всё, что делает приложение, происходит на
вашем компьютере.

## Что приложение делает с вашими данными

- **Панели и раскладка** сохраняются в JSON-файлах в папке внутри вашего профиля Windows
  (`%USERPROFILE%\DesktopOrganizer\State`). Ничего не покидает компьютер.
- **Файлы, перетащенные в панель,** *перемещаются* в `%USERPROFILE%\DesktopOrganizer\PanelItems`, чтобы не
  отображаться на рабочем столе. Удаление элемента из панели возвращает файл на место. Приложение не читает
  содержимое файлов и никуда их не отправляет. Папка остаётся на месте при удалении или сбросе приложения.
- **Папки-порталы.** Панель может показывать содержимое выбранной папки. Приложение только перечисляет файлы и
  запрашивает значки у Windows.
- **Журнал диагностики.** Небольшой ротируемый журнал ошибок и событий запуска/остановки хранится в
  `%USERPROFILE%\DesktopOrganizer\Logs` на английском языке; пути к файлам и имя пользователя Windows удаляются
  до записи. Журнал никуда не отправляется автоматически.
- **Настройки** (язык интерфейса, одноразовые подсказки) хранятся в стандартном расположении настроек .NET
  для пользователя под `%LOCALAPPDATA%`.

## Доступ в сеть

Сборка DesktopOrganizer для Microsoft Store не устанавливает сетевых соединений. Обновления доставляет
Microsoft Store.

## Разрешения

DesktopOrganizer — классическое настольное приложение (Win32), упакованное для Microsoft Store, поэтому оно
работает с возможностью `runFullTrust`. Этот доступ используется только для того, чтобы разместить панели в
слое рабочего стола, распознавать жесты мыши на фоне рабочего стола, скрывать и показывать значки рабочего
стола по вашей команде, показывать значок в трее и, если вы включите это в Параметрах Windows, запускаться
при входе в систему.

## Контакт

Вопросы о конфиденциальности и приложении: `<SUPPORT-EMAIL>`.
