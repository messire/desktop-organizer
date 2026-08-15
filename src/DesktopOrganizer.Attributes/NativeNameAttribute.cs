namespace DesktopOrganizer.Attributes;

/// <summary>
/// Custom attribute to store native name of enum value.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class NativeNameAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NativeNameAttribute"/> class.
    /// </summary>
    /// <param name="name">
    /// Enum native name — the exact Win32/shell constant this enum value maps to (for example
    /// <c>SHGFI_ICON</c>). The name is the attribute's only payload, so an absent one carries no
    /// information at all and is rejected outright rather than silently stored.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is <see langword="null"/> or empty. Deliberately
    /// <see cref="ArgumentException"/> (not <see cref="ArgumentNullException"/>) for both cases: the
    /// two inputs are the same single defect — "no native name given" — for the caller.
    /// </exception>
    public NativeNameAttribute(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Native name must not be null or empty.", nameof(name));
        }

        Name = name;
    }

    /// <summary>
    /// Native name of enum value.
    /// </summary>
    public string Name { get; }
}
