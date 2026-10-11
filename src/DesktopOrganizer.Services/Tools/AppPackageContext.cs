using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="IAppPackageContext" />
internal sealed class AppPackageContext : IAppPackageContext
{
    private readonly INativeApi _nativeApi;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppPackageContext"/> class.
    /// </summary>
    /// <param name="nativeApi">Source of the cached package-identity probe.</param>
    public AppPackageContext(INativeApi nativeApi)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        _nativeApi = nativeApi;
    }

    /// <inheritdoc />
    public bool IsPackaged => _nativeApi.IsPackagedProcess;
}
