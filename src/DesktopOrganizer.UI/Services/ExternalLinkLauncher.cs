using System.ComponentModel;
using System.Diagnostics;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.UI.Services;

/// <inheritdoc cref="IExternalLinkLauncher"/>
public sealed class ExternalLinkLauncher : IExternalLinkLauncher
{
    private readonly IAppLog? _appLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalLinkLauncher"/> class.
    /// </summary>
    /// <param name="appLog">
    /// Where a failed launch is recorded; falls back to the ambient <see cref="AppLog.Current"/> when
    /// <see langword="null"/> (a test constructing this type directly rather than through DI).
    /// </param>
    public ExternalLinkLauncher(IAppLog? appLog = null) => _appLog = appLog;

    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc/>
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // M4 (release-hardening review): only ever hand ShellExecute a well-formed, absolute https://
        // URL — never trust the caller's string as-is. Rejects javascript:/file:/a bare http:// and any
        // malformed/quoted value (Uri.TryCreate itself rejects embedded quotes/whitespace that would
        // otherwise risk being interpreted as extra command-line content by the shell).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn(
                "ExternalLinkLauncher",
                "OpenUrlRejected",
                new KeyValuePair<string, object?>("url", url));
            return;
        }

        LaunchViaShell(uri.AbsoluteUri);
    }

    /// <inheritdoc/>
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        LaunchViaShell(path);
    }

    private void LaunchViaShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Error(
                "ExternalLinkLauncher",
                "OpenFailed",
                ex,
                new KeyValuePair<string, object?>("target", target));
        }
    }
}
