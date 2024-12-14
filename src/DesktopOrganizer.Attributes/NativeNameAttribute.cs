namespace DesktopOrganizer.Attributes;

/// <summary>
/// Custom attribute to store native name of enum value.
/// </summary>
/// <param name="name">Enum native name.</param>
[AttributeUsage(AttributeTargets.Field)]
public class NativeNameAttribute(string name) : Attribute
{
    /// <summary>
    /// Native name of enum value.
    /// </summary>
    public string Name { get; } = name;
}
