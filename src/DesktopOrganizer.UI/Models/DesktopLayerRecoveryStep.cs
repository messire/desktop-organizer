namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The decision a <see cref="DesktopLayerRecoveryPolicy"/> hands back after a recovery attempt.
/// </summary>
public enum DesktopLayerRecoveryStep
{
    /// <summary>The attempt belonged to an older generation (a newer restart signal arrived); drop its result.</summary>
    Superseded,

    /// <summary>Nothing left to improve — recovery for this generation is complete.</summary>
    Completed,

    /// <summary>Schedule another attempt of the same generation after <see cref="DesktopLayerRecoveryPolicy.RetryInterval"/>.</summary>
    Retry,

    /// <summary>
    /// The bounded retry budget is spent and the desktop layer still is not there. On Windows 11 24H2+
    /// this is the normal outcome (panels are top-level by design there) and must not be logged as an error.
    /// </summary>
    Exhausted,
}
