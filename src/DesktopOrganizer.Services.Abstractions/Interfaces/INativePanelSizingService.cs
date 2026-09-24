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
    None,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
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
/// <see langword="true"/>.
/// </remarks>
public interface INativePanelSizingClient
{
    /// <summary>
    /// <c>WM_NCHITTEST</c>. Возвращает грань/угол под курсором.
    /// </summary>
    /// <param name="screenPoint">Точка курсора в экранных пикселях (из <c>lParam</c> сообщения).</param>
    /// <param name="windowRect">Прямоугольник окна (<c>GetWindowRect</c>), экранные пиксели.</param>
    /// <param name="dpiScale">Текущий масштаб окна (1.0 при 96 DPI).</param>
    /// <returns>
    /// Грань/угол, либо <see cref="NativeSizingEdge.None"/> ⇒ <c>HTCLIENT</c> (WPF обрабатывает сам).
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
    /// Отмена по <c>ESC</c> не отличима от штатного завершения (R-8 плана): система в обоих случаях
    /// шлёт только это сообщение.
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
    /// источник правды о смене DPI — <c>IMonitorService.MonitorsChanged</c>.
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
}
