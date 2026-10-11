using System.IO;
using System.Text;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Writes the bilingual <c>README.txt</c> into the storage root of the Microsoft Store channel on the very
/// first run, so a user who finds the unexpected <c>%USERPROFILE%\DesktopOrganizer</c> folder (or a
/// support engineer) learns what it is, that it holds the user's moved files, and that uninstalling or
/// resetting the app does not remove it.
/// </summary>
/// <remarks>
/// <para>
/// Not persisted state: nothing reads the file, it is not part of the schema or the backups, and deleting
/// it is harmless. It is written only <b>under package identity</b> (the Inno root is never touched), only
/// when the run is <see cref="StateLoadStatus.FirstRun"/> and the session is writable, and with
/// <see cref="FileMode.CreateNew"/> — an existing file (even a user-edited one) is never overwritten.
/// </para>
/// <para>
/// The text is a single embedded resource, not a resx entry: the file is written before the UI language
/// is chosen, so it carries English and Russian side by side.
/// </para>
/// </remarks>
internal static class StorageRootReadme
{
    /// <summary>File name inside the storage root.</summary>
    internal const string FileName = "README.txt";

    /// <summary>Manifest name of the embedded text (see <c>DesktopOrganizer.Services.csproj</c>).</summary>
    internal const string ResourceName = "DesktopOrganizer.Services.Storage.StorageRootReadme.txt";

    private const string LogSource = "StorageRootReadme";

    /// <summary>
    /// Decides whether the README should be written for this run.
    /// </summary>
    /// <param name="isPackaged">Whether the process runs with package identity.</param>
    /// <param name="status">How the application state was loaded this run.</param>
    /// <param name="isWritable">Whether the storage is writable this session.</param>
    /// <returns><see langword="true"/> only for a packaged, writable first run.</returns>
    internal static bool ShouldWrite(bool isPackaged, StateLoadStatus status, bool isWritable)
        => isPackaged && status == StateLoadStatus.FirstRun && isWritable;

    /// <summary>
    /// Returns the README text, English and Russian, with Windows line endings.
    /// </summary>
    /// <returns>The README text, or <see langword="null"/> when the embedded resource is missing.</returns>
    internal static string? ReadText()
    {
        using var stream = typeof(StorageRootReadme).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// Creates <c>README.txt</c> under <paramref name="root"/> unless it already exists. Never throws:
    /// an I/O or permission failure is logged as a warning and swallowed (the README is a courtesy).
    /// </summary>
    /// <param name="root">The storage root.</param>
    /// <param name="log">Log receiving the warning on failure.</param>
    /// <returns><see langword="true"/> when the file was created by this call.</returns>
    internal static bool TryWrite(string root, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var path = Path.Combine(root, FileName);

            // Never overwrite (not even a user-edited copy); also the cheap exit on a repeated call.
            if (File.Exists(path)) return false;

            var text = ReadText();
            if (text is null)
            {
                log.Warn(LogSource, "ResourceMissing", new KeyValuePair<string, object?>("resource", ResourceName));
                return false;
            }

            Directory.CreateDirectory(root);

            // CreateNew: a file that appeared since the check above makes this throw instead of being replaced.
            // UTF-8 with BOM so every Notepad version shows the Cyrillic text correctly.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(
                LogSource,
                "WriteFailed",
                new KeyValuePair<string, object?>("root", root),
                new KeyValuePair<string, object?>("error", ex.GetType().Name),
                new KeyValuePair<string, object?>("message", ex.Message));
            return false;
        }
    }
}
