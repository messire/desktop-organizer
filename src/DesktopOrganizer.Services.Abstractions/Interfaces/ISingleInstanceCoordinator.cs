namespace DesktopOrganizer.Services.Abstractions;

/// <summary>Role a process ends up in after the single-instance handshake (ADR-023).</summary>
public enum SingleInstanceRole
{
    /// <summary>
    /// The process holds the single-instance guard. It normally also serves activation requests from
    /// other processes, but starts in a degraded mode — guard held, no activation server — when the
    /// pipe server failed to start (<c>serverStarted == false</c>, see
    /// <c>SingleInstanceStartupDecisionTable</c>).
    /// </summary>
    Primary,

    /// <summary>The process lost the handshake to an existing primary and relays activation to it instead.</summary>
    Secondary,
}

/// <summary>Что должна сделать точка входа процесса по итогам рукопожатия.</summary>
public enum SingleInstanceStartupAction
{
    /// <summary>The process became Primary and must continue its normal startup.</summary>
    RunAsPrimary,

    /// <summary>The process successfully activated the existing Primary and must exit immediately.</summary>
    ExitAfterActivation,

    /// <summary>
    /// The process could not activate the existing Primary and must exit. Covers a protocol/transport
    /// failure (<see cref="ActivationSendResult.Timeout"/>, <see cref="ActivationSendResult.ProtocolError"/>,
    /// <see cref="ActivationSendResult.Faulted"/>), the primary shutting down
    /// (<see cref="ActivationSendResult.PrimaryShuttingDown"/>), or the retry budget being exhausted
    /// while no listener was ever found (<see cref="ActivationSendResult.NoListener"/>).
    /// </summary>
    ExitActivationFailed,

    /// <summary>The process detected the existing Primary belongs to another Windows session (Fast User Switching) and must exit.</summary>
    ExitOtherSession,
}

/// <summary>Результат попытки отправить <see cref="ActivationRequest"/> текущему primary.</summary>
public enum ActivationSendResult
{
    /// <summary>The request was delivered to and acknowledged by the primary process.</summary>
    Delivered,

    /// <summary>The primary process is shutting down and refused the request.</summary>
    PrimaryShuttingDown,

    /// <summary>The primary process belongs to a different Windows session (Fast User Switching).</summary>
    OtherSession,

    /// <summary>
    /// Could not connect within the timeout (no listener, or the single pipe instance is busy) —
    /// transient, retried by the handshake policy.
    /// </summary>
    NoListener,

    /// <summary>
    /// Connected, but the write or read that followed did not complete within the configured budget.
    /// A connect-time timeout is reported as <see cref="NoListener"/> instead.
    /// </summary>
    Timeout,

    /// <summary>
    /// The primary process responded with a message the protocol could not parse, or with a
    /// correctly-parsed but unsupported response code.
    /// </summary>
    ProtocolError,

    /// <summary>
    /// An unexpected exception occurred while sending the request, or the connection was dropped
    /// before a full response line arrived.
    /// </summary>
    Faulted,
}

/// <summary>v1: ровно одна команда. Расширение — новая версия протокола (ADR-023 D-5).</summary>
public enum ActivationCommand
{
    /// <summary>Requests that the primary process bring its Settings window to the foreground.</summary>
    ActivateSettings,
}

/// <summary>Запрос активации, отправляемый secondary-процессом текущему primary по именованному каналу.</summary>
/// <param name="ProtocolVersion">Версия протокола сообщения (см. ADR-023 D-5, <c>DO/1</c>).</param>
/// <param name="Command">Команда — в v1 всегда <see cref="ActivationCommand.ActivateSettings"/>.</param>
/// <param name="SessionId">
/// <c>Process.GetCurrentProcess().SessionId</c> отправителя (без P/Invoke) — позволяет primary
/// отличить «другая сессия Windows» (FUS, ADR-023 D-4) от обычного повторного запуска.
/// </param>
public readonly record struct ActivationRequest(int ProtocolVersion, ActivationCommand Command, int SessionId);

/// <summary>Итог <see cref="ISingleInstanceCoordinator.Start"/> — что делать точке входа процесса дальше.</summary>
/// <param name="Action">Действие, которое обязана выполнить точка входа.</param>
/// <param name="Role">Роль, в которой процесс завершил рукопожатие.</param>
/// <param name="SendResult">
/// Результат последней попытки отправки активации, если она предпринималась; <see langword="null"/>,
/// если процесс стал primary без единой попытки отправки (mutex был свободен сразу).
/// </param>
/// <param name="Attempts">Число выполненных попыток рукопожатия (см. ADR-023 D-6).</param>
/// <param name="ElapsedMs">Суммарное время рукопожатия в миллисекундах.</param>
public sealed record SingleInstanceStartupDecision(
    SingleInstanceStartupAction Action,
    SingleInstanceRole Role,
    ActivationSendResult? SendResult,
    int Attempts,
    long ElapsedMs);

/// <summary>
/// Оркестрирует рукопожатие single-instance (mutex + именованный канал активации, ADR-023). Реализация
/// живёт в <c>DesktopOrganizer.Services</c> (нативные ресурсы ОС за интерфейсом, как
/// <c>ISystemShellService</c>/<c>IMonitorService</c>); эта абстракция не знает про mutex/pipe.
/// </summary>
public interface ISingleInstanceCoordinator : IDisposable
{
    /// <summary>
    /// Выполняет всё рукопожатие (bounded, ≤ бюджета политики). Никогда не бросает.
    /// Вызывается ровно один раз, с главного STA-потока.
    /// </summary>
    SingleInstanceStartupDecision Start();

    /// <summary>
    /// Поднимает pipe-сервер. Идемпотентно. Только для Primary; при <c>ERROR_PIPE_BUSY</c> возвращает
    /// <see langword="false"/> — вызывающий обязан деградировать до Secondary (ADR-023 D-4).
    /// </summary>
    bool TryBeginServing();

    /// <summary>
    /// Поступившие команды активации. Может подняться на фоновом потоке ДО того, как существует
    /// <c>Application.Current</c> — см. <c>ActivationRequestRelay</c>.
    /// </summary>
    event EventHandler<ActivationRequest>? ActivationRequested;

    /// <summary>Переводит сервер в режим ответа <c>SHUTTING_DOWN</c>. Идемпотентно, не блокирует.</summary>
    void EnterShutdown();

    /// <summary>
    /// Explicit, synchronous last step of shutdown for a process that (at least momentarily) held the
    /// guard: stops the activation server (bounded, ≤2s), releases the guard mutex, and — for a
    /// process that actually was Primary — writes the <c>SingleInstanceShutdownSequence</c>
    /// diagnostics event (ADR-023 D-9, phase-3 plan §4).
    /// </summary>
    /// <remarks>
    /// Must be called by the host (see <c>App.OnExit</c>) <i>before</i> disposing the DI container that
    /// owns <see cref="IDiagnosticsLog"/> — resolving that log from inside <see cref="IDisposable.Dispose"/> itself
    /// is not safe, because <see cref="IDisposable.Dispose"/> can run automatically as part of that very same
    /// container's own disposal chain, by which point the container is already marked disposed and any
    /// further resolution throws <see cref="ObjectDisposedException"/> (C-2). Idempotent: a second call
    /// is a no-op. Safe to skip entirely for a process that never became Primary — <see cref="IDisposable.Dispose"/>
    /// performs the equivalent (diagnostics-free) teardown on its own if this was never called.
    /// </remarks>
    void CompleteShutdown();
}
