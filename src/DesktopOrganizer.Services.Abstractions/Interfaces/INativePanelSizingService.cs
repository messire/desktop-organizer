namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Rectangle in PHYSICAL screen pixels (never DIP) — see ADR-022 / Phase 2 plan D-3: pixels travel
/// across the native boundary, the single px↔DIP conversion happens in the UI layer.
/// </summary>
/// <param name="Left">Left edge, in physical screen pixels.</param>
/// <param name="Top">Top edge, in physical screen pixels.</param>
/// <param name="Right">Right edge (exclusive), in physical screen pixels.</param>
/// <param name="Bottom">Bottom edge (exclusive), in physical screen pixels.</param>
public readonly record struct NativeRectPx(int Left, int Top, int Right, int Bottom)
{
    /// <summary>
    /// Gets the width in physical pixels. Negative when the rectangle is inverted — callers that
    /// care must validate; no normalization happens here (a raw <c>RECT</c> is reported as-is).
    /// </summary>
    public int Width => Right - Left;

    /// <summary>
    /// Gets the height in physical pixels. Negative when the rectangle is inverted, see
    /// <see cref="Width"/>.
    /// </summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// Point in PHYSICAL screen pixels (never DIP) — see <see cref="NativeRectPx"/>.
/// </summary>
/// <param name="X">Horizontal screen coordinate, in physical pixels.</param>
/// <param name="Y">Vertical screen coordinate, in physical pixels.</param>
public readonly record struct NativePointPx(int X, int Y);

/// <summary>Грань/угол в терминах WM_NCHITTEST. Намеренно ОТДЕЛЬНЫЙ enum от UI.Interaction.ResizeEdge:
/// Abstractions не должен зависеть от UI. Маппинг — в UI (NativeSizingEdgeMap), покрыт T-MAP-05.</summary>
public enum NativeSizingEdge
{
    /// <summary>
    /// No edge/corner under the cursor. Returned by the client from <see cref="INativePanelSizingClient.HitTest"/>;
    /// the service then answers <c>HTCLIENT</c> itself for a point inside the window rectangle (a point
    /// outside it is left to <c>DefWindowProc</c>) — the window's own area never depends on
    /// <c>DefWindowProc</c> resolving it to <c>HTCLIENT</c>. Also used internally as the sentinel for an
    /// unrecognized <c>WM_SIZING</c> <c>wParam</c> and for "no active sizing cycle".
    /// </summary>
    None,

    /// <summary>The left edge.</summary>
    Left,

    /// <summary>The top edge.</summary>
    Top,

    /// <summary>The right edge.</summary>
    Right,

    /// <summary>The bottom edge.</summary>
    Bottom,

    /// <summary>The top-left corner.</summary>
    TopLeft,

    /// <summary>The top-right corner.</summary>
    TopRight,

    /// <summary>The bottom-left corner.</summary>
    BottomLeft,

    /// <summary>The bottom-right corner.</summary>
    BottomRight,
}

/// <summary>
/// Политика sizing'а. Реализуется в UI (<c>NativePanelResizeController</c>) — там живут
/// <c>ResizeGestureSession</c>/<c>ResizeGeometryCalculator</c>. Все прямоугольники — пиксели ЭКРАНА
/// (см. <see cref="NativeRectPx"/> и D-3 плана: перевод в DIP делает ровно одна точка в UI).
/// </summary>
/// <remarks>
/// Реализация вызывается из оконной процедуры панели, то есть синхронно внутри системного
/// sizing-цикла: работа должна быть короткой, а исключения — нежелательны (сервис их ловит и
/// продолжает без вмешательства в геометрию, но жест при этом теряет своё состояние).
/// Терминальное уведомление приходит ровно одно на цикл: либо <see cref="SizeMoveExited"/>,
/// либо <see cref="SizeMoveAborted"/>, и только если <see cref="SizeMoveEntered"/> вернул
/// <see langword="true"/>. Исключение — цикл, отменённый самим клиентом через
/// <see cref="INativePanelSizingService.CancelActiveSizing"/>: там терминального уведомления нет вовсе.
/// </remarks>
public interface INativePanelSizingClient
{
    /// <summary>
    /// Gets a value indicating whether resize hit-testing is enabled at all right now (the resize gate:
    /// off for a locked panel, a rolled-up panel and the default move-only mode). Read FIRST on every
    /// <c>WM_NCHITTEST</c>, before any native call — so it must be a cheap flag read.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> ⇒ the service skips the DPI query and <see cref="HitTest"/> entirely and
    /// answers <c>HTCLIENT</c> for a point inside the window. <see cref="HitTest"/>'s own semantics are
    /// unchanged: called while the gate is off it still reports <see cref="NativeSizingEdge.None"/>.
    /// </remarks>
    bool IsHitTestEnabled { get; }

    /// <summary>
    /// <c>WM_NCHITTEST</c>. Возвращает грань/угол под курсором.
    /// </summary>
    /// <param name="screenPoint">Точка курсора в экранных пикселях (из <c>lParam</c> сообщения).</param>
    /// <param name="windowRect">Прямоугольник окна (<c>GetWindowRect</c>), экранные пиксели.</param>
    /// <param name="dpiScale">Текущий масштаб окна (1.0 при 96 DPI).</param>
    /// <returns>
    /// Грань/угол, либо <see cref="NativeSizingEdge.None"/> ⇒ <c>HTCLIENT</c> (сервис отвечает им явно,
    /// клиентскую область дальше обрабатывает WPF).
    /// </returns>
    NativeSizingEdge HitTest(NativePointPx screenPoint, NativeRectPx windowRect, double dpiScale);

    /// <summary>
    /// <c>WM_ENTERSIZEMOVE</c> — системный sizing-цикл начался.
    /// </summary>
    /// <param name="edge">
    /// Грань, с которой начался жест (последний sizing-результат <see cref="HitTest"/>). Источником
    /// истины для каждого конкретного <c>WM_SIZING</c> остаётся его собственный <c>wParam</c>,
    /// приходящий в <see cref="ProposeBounds"/>.
    /// </param>
    /// <param name="windowRect">Стартовый прямоугольник окна, экранные пиксели.</param>
    /// <param name="dpiScale">Масштаб окна на момент старта жеста.</param>
    /// <returns>
    /// <see langword="false"/> ⇒ жест отклонён (tracker занят) — сервис не будет трогать последующие
    /// <c>WM_SIZING</c> этого цикла и не пришлёт терминального уведомления.
    /// </returns>
    bool SizeMoveEntered(NativeSizingEdge edge, NativeRectPx windowRect, double dpiScale);

    /// <summary>
    /// <c>WM_SIZING</c> — система предлагает новый прямоугольник.
    /// </summary>
    /// <param name="edge">
    /// Грань из <c>wParam</c> (<c>WMSZ_*</c>) этого сообщения — может расходиться с гранью из
    /// <see cref="SizeMoveEntered"/>; решение о расхождении принимает клиент.
    /// </param>
    /// <param name="proposed">Предлагаемый системой прямоугольник, пиксели.</param>
    /// <returns>
    /// Скорректированный прямоугольник (snap/clamp). Вернуть <paramref name="proposed"/> без изменений —
    /// значит «не вмешиваюсь».
    /// </returns>
    NativeRectPx ProposeBounds(NativeSizingEdge edge, NativeRectPx proposed);

    /// <summary>
    /// <c>WM_EXITSIZEMOVE</c> — цикл завершён штатно.
    /// </summary>
    /// <param name="finalRect">Итоговый прямоугольник окна (<c>GetWindowRect</c>), пиксели.</param>
    /// <remarks>
    /// Отмена по <c>ESC</c> приходит тем же сообщением (R-8 плана). Отличить её клиент может только по
    /// геометрии: система восстанавливает стартовый прямоугольник, поэтому <paramref name="finalRect"/>,
    /// равный стартовому из <see cref="SizeMoveEntered"/>, клиент трактует как отменённый жест (ADR-022
    /// phase 0-B, G-4).
    /// </remarks>
    void SizeMoveExited(NativeRectPx finalRect);

    /// <summary>
    /// <c>WM_CAPTURECHANGED</c> во время активного цикла — capture украли (Alt+Tab, Explorer restart,
    /// всплывшее меню). Геометрию сообщить нечем: окно в этот момент может быть уже недостоверно.
    /// </summary>
    void SizeMoveAborted();

    /// <summary>
    /// <c>WM_DPICHANGED</c> / <c>WM_DPICHANGED_AFTERPARENT</c>. См. D-6 плана — reparent-нутой в
    /// <c>WorkerW</c> панели при текущем (не Per-Monitor-V2) awareness может не прийти никогда;
    /// источник правды о смене DPI — <c>IMonitorService.MonitorsChanged</c>. Может прийти и посреди
    /// sizing-цикла (панель пересекает границу мониторов): жест при этом НЕ обрывается (ADR-022 phase 0-B,
    /// G-1) — сообщение не помечается обработанным, геометрию по-прежнему ведёт клиент.
    /// </summary>
    /// <param name="newScale">Новый масштаб (1.0 при 96 DPI).</param>
    /// <param name="suggestedRect">Предлагаемый системой прямоугольник, экранные пиксели.</param>
    void DpiChanged(double newScale, NativeRectPx suggestedRect);
}

/// <summary>
/// Ставит оконный хук на панель и переводит сообщения системного sizing-цикла в вызовы
/// <see cref="INativePanelSizingClient"/>. Сама геометрия/snap сервису неизвестны (D-2 плана).
/// </summary>
public interface INativePanelSizingService
{
    /// <summary>
    /// Ставит хук на HWND панели.
    /// </summary>
    /// <param name="hwnd">Окно панели (уже созданное — вызывать из <c>OnSourceInitialized</c> и позже).</param>
    /// <param name="client">Политика sizing'а.</param>
    /// <returns>
    /// Токен: <see cref="IDisposable.Dispose"/> снимает хук и идемпотентен. Безопасно вызывать
    /// повторно для одного HWND — второй <see cref="Attach"/> вернёт no-op токен и запишет warning
    /// (R-10 плана: хук обязан сниматься детерминированно, иначе делегат держит окно и контроллер).
    /// </returns>
    IDisposable Attach(IntPtr hwnd, INativePanelSizingClient client);

    /// <summary>
    /// Прерывает системный sizing-цикл, идущий сейчас для <paramref name="hwnd"/> (ADR-022 phase 0-B, G-2):
    /// нужен клиенту, который сам завершает жест посреди цикла (смена режима/страницы, shell recovery,
    /// закрытие панели — dispatcher-операции, выполняемые внутри вложенного цикла <c>DefWindowProc</c>).
    /// Без этого цикл продолжал бы ресайзить окно уже без снапа и без сохранения.
    /// </summary>
    /// <param name="hwnd">Окно панели, ранее переданное в <see cref="Attach"/>.</param>
    /// <returns>
    /// <see langword="true"/>, если для <paramref name="hwnd"/> шёл принятый клиентом цикл и запрос на отмену
    /// отправлен; иначе <see langword="false"/> (нет хука, нет цикла, повторный вызов). Идемпотентен, не бросает.
    /// </returns>
    /// <remarks>
    /// Цикл закрывается на стороне сервиса СРАЗУ и БЕЗ терминального уведомления клиенту: клиент сам
    /// инициировал отмену и сам завершает свой жест. Последующие <c>WM_SIZING</c>/<c>WM_EXITSIZEMOVE</c>
    /// этого цикла (система доставит их, пока цикл не размотается) клиенту не передаются. Вызывать только из
    /// UI-потока, которому принадлежит окно.
    /// </remarks>
    bool CancelActiveSizing(IntPtr hwnd);

    /// <summary>
    /// Reads the current window rectangle (<c>GetWindowRect</c>, physical SCREEN pixels) of
    /// <paramref name="hwnd"/> — lets the client compare it against a gesture's starting rectangle AFTER the
    /// gesture was ended and the system loop cancelled (the client has no direct native access: P/Invoke
    /// lives behind the service layer).
    /// </summary>
    /// <param name="hwnd">The panel window.</param>
    /// <param name="rect">Receives the rectangle when the call succeeds; <see langword="default"/> otherwise.</param>
    /// <returns><see langword="true"/> if the rectangle could be read; <see langword="false"/> otherwise (never throws).</returns>
    bool TryGetWindowRect(IntPtr hwnd, out NativeRectPx rect);
}
