namespace DesktopOrganizer.Storage;

public interface IApplicationStateStore
{
    Task<ApplicationStateEnvelope> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ApplicationStateEnvelope state, CancellationToken cancellationToken);
}

public sealed record ApplicationStateEnvelope(
    int SchemaVersion,
    DateTimeOffset WrittenAtUtc,
    string AppVersion,
    object State);
