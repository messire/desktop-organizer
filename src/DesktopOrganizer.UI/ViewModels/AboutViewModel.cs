using System.ComponentModel;
using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Services.Updates;
using DesktopOrganizer.UI.Commands;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Backing ViewModel for the Settings window's About section's "Updates"/"Links" cards
/// (release-hardening-1.0.md §G2): a manual "Check for updates" button plus three fixed external links.
/// Owned and constructed by DI as its own singleton (unlike <see cref="GlobalDefaultsViewModel"/>/
/// <see cref="SystemIconsSettingsViewModel"/>, which <see cref="MainWindowViewModel"/> constructs
/// internally) and exposed as <see cref="MainWindowViewModel.About"/> — <c>AboutSection.xaml</c> binds
/// against it via <c>{Binding About.X}</c>, the same convention <c>BackupSection.xaml</c> already uses
/// for <see cref="LayoutSnapshotsViewModel"/>.
/// </summary>
public class AboutViewModel : INotifyPropertyChanged
{
    private readonly IUpdateCheckService _updateCheckService;
    private readonly IExternalLinkLauncher _linkLauncher;
    private readonly ILocalizationService _localization;
    private readonly IAppPackageContext _packageContext;
    private readonly IAppLog? _appLog;

    private bool _isChecking;
    private string _statusText = string.Empty;
    private bool _hasReleaseLink;
    private string? _releaseUrl;
    private UpdateCheckResult? _lastResult;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Whether the manual update check exists in this build: <see langword="false"/> for a package-identity
    /// (Microsoft Store) build, where the Store itself delivers updates and the app must not make this
    /// network call — <c>AboutSection.xaml</c> hides the whole "Updates" card on it, and
    /// <see cref="CheckForUpdatesAsync"/> refuses to run (defense in depth).
    /// </summary>
    public bool IsUpdateCheckAvailable => !_packageContext.IsPackaged;

    /// <summary>
    /// Whether a check is currently in flight — disables <see cref="CheckForUpdatesCommand"/>'s button
    /// while <see langword="true"/> (<c>AboutSection.xaml</c> binds its <c>IsEnabled</c> directly to the
    /// negation, rather than relying solely on WPF's own lazy <c>CanExecute</c> re-query timing).
    /// </summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (_isChecking == value) return;
            _isChecking = value;
            OnPropertyChanged(nameof(IsChecking));
        }
    }

    /// <summary>
    /// The inline result text shown under the "Check for updates" button — empty until the first check
    /// completes. Re-resolved for the current language whenever it changes (<see cref="OnCultureChanged"/>),
    /// same as <see cref="MainWindowViewModel.LanguageOptions"/>' own hot-switch refresh.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Whether <see cref="OpenReleaseCommand"/> currently has somewhere to go — <see langword="true"/> only right after an <see cref="UpdateCheckStatus.UpdateAvailable"/> result.</summary>
    public bool HasReleaseLink
    {
        get => _hasReleaseLink;
        private set
        {
            if (_hasReleaseLink == value) return;
            _hasReleaseLink = value;
            OnPropertyChanged(nameof(HasReleaseLink));
        }
    }

    /// <summary>Command: runs <see cref="IUpdateCheckService.CheckForUpdatesAsync"/> and updates <see cref="StatusText"/>/<see cref="HasReleaseLink"/>.</summary>
    public ICommand CheckForUpdatesCommand { get; }

    /// <summary>Command: opens the release page from the most recent <see cref="UpdateCheckStatus.UpdateAvailable"/> result (see <see cref="HasReleaseLink"/>).</summary>
    public ICommand OpenReleaseCommand { get; }

    /// <summary>Command: opens <see cref="UpdateEndpoints.ProjectPageUrl"/> — the About section's "Project page" link.</summary>
    public ICommand OpenProjectPageCommand { get; }

    /// <summary>Command: opens <see cref="UpdateEndpoints.SupportUrl"/> — the About section's "Report a problem" link.</summary>
    public ICommand ReportProblemCommand { get; }

    /// <summary>Command: opens <see cref="LogsFolderPath"/> (creating it first if it doesn't exist yet) — the About section's "Open logs folder" link.</summary>
    public ICommand OpenLogsFolderCommand { get; }

    /// <summary>The per-user diagnostics log folder (<c>Logs</c> under the storage root: Inno channel <c>%LOCALAPPDATA%\DesktopOrganizer</c>, Store channel <c>%USERPROFILE%\DesktopOrganizer</c>) — same path <c>Services/Configuration/DiConfig.cs</c>'s <c>JsonLinesDiagnosticsLog</c> writes to.</summary>
    public string LogsFolderPath { get; } = AppStorageRoot.LogsDirectory;

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <summary>
    /// Initializes a new instance of the <see cref="AboutViewModel"/> class.
    /// </summary>
    /// <param name="updateCheckService">Backs <see cref="CheckForUpdatesCommand"/>.</param>
    /// <param name="linkLauncher">Opens every external URL/folder this ViewModel's commands point at.</param>
    /// <param name="localization">Localizes <see cref="StatusText"/> and reacts to a hot language switch.</param>
    /// <param name="packageContext">Decides <see cref="IsUpdateCheckAvailable"/> (no manual update check under package identity).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public AboutViewModel(
        IUpdateCheckService updateCheckService,
        IExternalLinkLauncher linkLauncher,
        ILocalizationService localization,
        IAppPackageContext packageContext,
        IAppLog? appLog = null)
    {
        _updateCheckService = updateCheckService;
        _linkLauncher = linkLauncher;
        _localization = localization;
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _appLog = appLog;

        CheckForUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync());
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => HasReleaseLink);
        OpenProjectPageCommand = new RelayCommand(() => _linkLauncher.OpenUrl(UpdateEndpoints.ProjectPageUrl));
        ReportProblemCommand = new RelayCommand(() => _linkLauncher.OpenUrl(UpdateEndpoints.SupportUrl));
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);

        // Never unsubscribed: this ViewModel is a DI singleton for the app's lifetime, same rationale as
        // MainWindowViewModel's own CultureChanged subscription for LanguageOptions.
        _localization.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// The actual check, run fire-and-forget by <see cref="CheckForUpdatesCommand"/>. <c>internal</c>
    /// (rather than <see langword="private"/>) purely as a test seam — <c>AboutViewModelTests</c> awaits
    /// this directly, with a controllable <see cref="TaskCompletionSource{TResult}"/>-backed
    /// <see cref="IUpdateCheckService"/> mock, to assert <see cref="IsChecking"/>'s true-then-false
    /// sequencing deterministically instead of polling after firing the command.
    /// </summary>
    internal async Task CheckForUpdatesAsync()
    {
        // Package identity: the Store delivers updates — never reach the network from here.
        if (!IsUpdateCheckAvailable || IsChecking) return;

        IsChecking = true;
        HasReleaseLink = false;
        _releaseUrl = null;
        StatusText = _localization.GetString("Settings_About_Update_Checking");

        try
        {
            var result = await _updateCheckService.CheckForUpdatesAsync().ConfigureAwait(true);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            // IUpdateCheckService implementations must never throw (see its own remarks) — this is
            // defense in depth only, so a misbehaving future implementation degrades to a shown error
            // instead of an unobserved exception.
            Log.Error("AboutViewModel", "CheckForUpdatesFailed", ex);
            ApplyResult(UpdateCheckResult.Failed(UpdateCheckFailureKind.Unexpected));
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyResult(UpdateCheckResult result)
    {
        _lastResult = result;
        RefreshStatusText();
    }

    /// <summary>
    /// Re-resolves <see cref="StatusText"/>/<see cref="HasReleaseLink"/>/<see cref="_releaseUrl"/> from
    /// <see cref="_lastResult"/> for whatever <see cref="ILocalizationService.CurrentCulture"/> is right
    /// now — called both right after a check completes and again on every later hot language switch.
    /// </summary>
    private void RefreshStatusText()
    {
        if (_lastResult is not { } result)
            return;

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                HasReleaseLink = false;
                _releaseUrl = null;
                StatusText = _localization.GetString("Settings_About_Update_UpToDate");
                break;

            case UpdateCheckStatus.UpdateAvailable:
                _releaseUrl = result.ReleaseUrl;
                HasReleaseLink = _releaseUrl is not null;
                StatusText = _localization.Format("Settings_About_Update_Available_Format", result.LatestVersion);
                break;

            case UpdateCheckStatus.NoReleases:
                HasReleaseLink = false;
                _releaseUrl = null;
                StatusText = _localization.GetString("Settings_About_Update_NoReleases");
                break;

            case UpdateCheckStatus.Failed:
            default:
                HasReleaseLink = false;
                _releaseUrl = null;
                StatusText = _localization.Format("Settings_About_Update_Failed_Format", FailureReasonText(result.FailureKind));
                break;
        }
    }

    private string FailureReasonText(UpdateCheckFailureKind kind) => kind switch
    {
        UpdateCheckFailureKind.Network => _localization.GetString("Settings_About_Update_Failed_Network"),
        UpdateCheckFailureKind.Timeout => _localization.GetString("Settings_About_Update_Failed_Timeout"),
        UpdateCheckFailureKind.RateLimited => _localization.GetString("Settings_About_Update_Failed_RateLimited"),
        UpdateCheckFailureKind.InvalidResponse => _localization.GetString("Settings_About_Update_Failed_InvalidResponse"),
        _ => _localization.GetString("Settings_About_Update_Failed_Unexpected"),
    };

    private void OpenRelease()
    {
        if (_releaseUrl is not null) _linkLauncher.OpenUrl(_releaseUrl);
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogsFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("AboutViewModel", "CreateLogsFolderFailed", ex);
        }

        _linkLauncher.OpenFolder(LogsFolderPath);
    }

    private void OnCultureChanged(object? sender, CultureChangedEventArgs e) => RefreshStatusText();

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
