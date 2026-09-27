using System.Windows;
using DesktopOrganizer.UI.Behaviors;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="ListReorderGestureController{TItem}.ResolveDragData"/> — the pure
/// <see cref="DataObject"/> resolver backing <see cref="ListReorderGestureController{TItem}.OnPreviewMouseMove"/>
/// (M2, release-hardening 1.0 review). Uses <c>string</c> as <c>TItem</c> purely for a trivial candidate
/// type; the method itself never inspects it beyond forwarding it to the supplied delegates.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ListReorderGestureControllerResolveDragDataTests
{
    private const string DataFormat = "DesktopOrganizer.PanelItemReorder";
    private const string Candidate = "candidate-path";

    /// <summary>
    /// When no <c>createDataObject</c> delegate was supplied at all, falls back to the default
    /// <c>new DataObject(dataFormat, createPayload(candidate))</c> — the pre-ADR-036 behavior, unchanged
    /// by M2.
    /// </summary>
    [Fact]
    public void ResolveDragData_NoCreateDataObjectSupplied_BuildsDefaultDataObject()
    {
        var data = ListReorderGestureController<string>.ResolveDragData(
            Candidate,
            createDataObject: null,
            createPayload: c => c,
            DataFormat);

        Assert.NotNull(data);
        Assert.True(data!.GetDataPresent(DataFormat));
        Assert.Equal(Candidate, data.GetData(DataFormat));
    }

    /// <summary>
    /// When <c>createDataObject</c> is supplied and returns a real <see cref="DataObject"/>, that instance
    /// is used verbatim — <c>createPayload</c>/the default format are never consulted.
    /// </summary>
    [Fact]
    public void ResolveDragData_CreateDataObjectSuppliedAndReturnsData_UsesItVerbatim()
    {
        var expected = new DataObject("Custom.Format", "custom-payload");

        var data = ListReorderGestureController<string>.ResolveDragData(
            Candidate,
            createDataObject: _ => expected,
            createPayload: _ => throw new InvalidOperationException("createPayload must not run when createDataObject is supplied."),
            DataFormat);

        Assert.Same(expected, data);
    }

    /// <summary>
    /// M2 regression: a SUPPLIED <c>createDataObject</c> that itself returns <see langword="null"/> (e.g.
    /// a read-only source with nothing to drag out) must resolve to <see langword="null"/> — aborting the
    /// gesture — rather than silently falling back to the default <see cref="DataObject"/>. A prior
    /// `createDataObject?.Invoke(candidate) ?? new DataObject(...)` implementation made this contract
    /// (documented on <see cref="ListReorderGestureController{TItem}"/>'s constructor) dead code: <c>??</c>
    /// cannot distinguish "no delegate" from "delegate returned null".
    /// </summary>
    [Fact]
    public void ResolveDragData_CreateDataObjectSuppliedAndReturnsNull_ResolvesToNull_DoesNotFallBackToDefault()
    {
        var data = ListReorderGestureController<string>.ResolveDragData(
            Candidate,
            createDataObject: _ => null,
            createPayload: _ => throw new InvalidOperationException("createPayload must not run when createDataObject is supplied."),
            DataFormat);

        Assert.Null(data);
    }
}
