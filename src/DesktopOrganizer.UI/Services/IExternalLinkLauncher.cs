namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Opens an external target — an <c>https://</c> URL or a local folder path — through the Windows shell.
/// Backs the About section's four "open X" actions (release-hardening-1.0.md §G2: view a release, the
/// project page, the issue tracker, and the logs folder), so each is a one-line, independently mockable
/// call instead of its own <c>Process.Start</c> call site.
/// </summary>
/// <remarks>
/// M4 (release-hardening review): split into <see cref="OpenUrl"/>/<see cref="OpenFolder"/> — a single
/// <c>Open(string target)</c> handed straight to <c>ShellExecute</c> could not distinguish "a URL this app
/// itself decided to open" from "arbitrary string content", so a <c>javascript:</c>/<c>file:</c> URL (or a
/// plain <c>http://</c> one) could reach <c>ShellExecute</c> unchecked. Every caller now states which kind
/// of target it has, and <see cref="OpenUrl"/> validates it structurally instead of trusting the caller.
/// </remarks>
public interface IExternalLinkLauncher
{
    /// <summary>
    /// Opens <paramref name="url"/> with the user's default browser via <c>ShellExecute</c>
    /// (<see cref="System.Diagnostics.ProcessStartInfo.UseShellExecute"/> <see langword="true"/>), but
    /// ONLY if it is a well-formed, absolute <c>https://</c> URL — <c>javascript:</c>, <c>file:</c>, a bare
    /// <c>http://</c>, or a malformed/quoted string are rejected (logged, not launched) rather than handed
    /// to the shell. Every other failure (no default browser registered, the user declined a UAC prompt,
    /// …) is likewise logged and swallowed — this is always a convenience action, never on a critical path.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    void OpenUrl(string url);

    /// <summary>
    /// Opens the local folder at <paramref name="path"/> in Explorer via <c>ShellExecute</c>. Failures
    /// (the folder was deleted, no permissions, …) are logged and swallowed — this is always a convenience
    /// action, never on a critical path.
    /// </summary>
    /// <param name="path">The folder path to open.</param>
    void OpenFolder(string path);
}
