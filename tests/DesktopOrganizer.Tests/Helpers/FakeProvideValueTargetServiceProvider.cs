using System.Windows.Markup;

namespace DesktopOrganizer.Tests.Helpers;

/// <summary>
/// A minimal, hand-rolled <see cref="IServiceProvider"/>/<see cref="IProvideValueTarget"/> double that
/// lets a test call a <see cref="MarkupExtension"/>'s <c>ProvideValue</c> implementation directly —
/// without going through the real XAML parser — while still controlling exactly what
/// <see cref="IProvideValueTarget.TargetObject"/>/<see cref="IProvideValueTarget.TargetProperty"/> report.
/// Used to pin the "Setter/template vs. real dependency property" return-value rule
/// (docs/PLANS/localization.md §2.4) at the unit level, for both real and non-dependency-property
/// targets.
/// </summary>
public sealed class FakeProvideValueTargetServiceProvider : IServiceProvider, IProvideValueTarget
{
    /// <summary>The object the extension under test should believe it applies to.</summary>
    public object? TargetObject { get; init; }

    /// <summary>The property (a <see cref="System.Windows.DependencyProperty"/>, a <see cref="System.Reflection.PropertyInfo"/>, etc.) the extension under test should believe it targets.</summary>
    public object? TargetProperty { get; init; }

    /// <inheritdoc />
    public object? GetService(Type serviceType) =>
        serviceType == typeof(IProvideValueTarget) ? this : null;
}
