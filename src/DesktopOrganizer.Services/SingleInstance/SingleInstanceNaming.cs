using System.Security.Cryptography;
using System.Text;

namespace DesktopOrganizer.Services.SingleInstance;

/// <summary>
/// Names of the OS-level objects used by the single-instance handshake (ADR-023 D-1/D-4). Pure —
/// no object is created or opened here, only strings are computed.
/// </summary>
public static class SingleInstanceNaming
{
    /// <summary>
    /// Name of the detection mutex whose sole contract is "exists ⟺ the application process is
    /// alive". Consumed by <c>installer/DesktopOrganizer.iss</c> (<c>AppMutex=</c>) via Inno Setup's
    /// own mutex-existence check — Inno never waits on ownership, only on existence.
    /// </summary>
    /// <remarks>
    /// This value must never change, including adding an equivalent <c>Local\</c> prefix — same
    /// underlying object semantically, zero benefit for non-zero risk (ADR-023 D-1). Pinned by
    /// <c>InstallerMutexContractTests</c> (T-INS-01), which parses the <c>.iss</c> file and compares
    /// the literal there against this constant.
    /// </remarks>
    public const string InstallerDetectionMutexName = "DesktopOrganizerSingleInstanceMutex";

    /// <summary>
    /// Name of the guard mutex whose contract is "ownership ⟺ I am primary". Deliberately a
    /// different object from <see cref="InstallerDetectionMutexName"/> (ADR-023 D-1): the installer's
    /// contract is existence, the guard's contract is ownership — conflating them would forever
    /// couple guard renames/respecs to shipping a new installer.
    /// </summary>
    /// <param name="userKey">Per-user disambiguator — see <see cref="UserKeyHash(string)"/>.</param>
    /// <returns>
    /// A session-scoped (<c>Local\</c>) mutex name. Session-scoped because a plain mutex without a
    /// namespace prefix is already session-local on Windows; <c>Local\</c> makes that explicit. Two
    /// Windows sessions of the same user (Fast User Switching) each get their own guard mutex — the
    /// machine-global tie-break across sessions is the named pipe server, not this mutex
    /// (ADR-023 D-4).
    /// </returns>
    public static string GuardMutexName(string userKey) => $@"Local\DesktopOrganizer.SingleInstance.{userKey}";

    /// <summary>
    /// Name of the named pipe used to deliver <c>ActivationRequest</c>s to the current primary. Named
    /// pipes live in a machine-global namespace (unlike session-scoped mutexes), which is exactly why
    /// the pipe server (created with <c>maxNumberOfServerInstances: 1</c>) acts as the authoritative
    /// tie-breaker across sessions of the same user (ADR-023 D-4).
    /// </summary>
    /// <param name="userKey">Per-user disambiguator — see <see cref="UserKeyHash(string)"/>.</param>
    public static string ActivationPipeName(string userKey) => $"DesktopOrganizer.SingleInstance.{userKey}";

    /// <summary>
    /// Derives a short, non-reversible per-user key for use inside the object names above. The raw
    /// SID/user name is never embedded — pipe names are machine-globally visible to every session on
    /// the box, so leaking the raw SID into a well-known name would be an unnecessary disclosure.
    /// </summary>
    /// <param name="sidOrUserName">
    /// <c>WindowsIdentity.GetCurrent().User.Value</c> (preferred), or <c>Environment.UserName</c>
    /// upper-cased as a fallback when the SID is unavailable (ADR-023 D-1).
    /// </param>
    /// <returns>The first 16 lowercase hex characters of SHA-256(<paramref name="sidOrUserName"/>).</returns>
    public static string UserKeyHash(string sidOrUserName)
    {
        ArgumentNullException.ThrowIfNull(sidOrUserName);

        var bytes = Encoding.UTF8.GetBytes(sidOrUserName);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
