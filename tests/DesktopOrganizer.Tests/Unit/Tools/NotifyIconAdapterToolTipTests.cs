using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="NotifyIconAdapter.TruncateToolTip"/>: WinForms <c>NotifyIcon.Text</c> throws
/// <see cref="ArgumentOutOfRangeException"/> for more than 127 characters, so the adapter cuts the text to fit.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.Services")]
public class NotifyIconAdapterToolTipTests
{
    /// <summary>The limit is 127 characters.</summary>
    [Fact]
    public void MaxToolTipLength_Is127()
    {
        Assert.Equal(127, NotifyIconAdapter.MaxToolTipLength);
    }

    /// <summary>A text that fits is returned unchanged (also exactly at the limit).</summary>
    /// <param name="text">A text within the limit.</param>
    [Theory]
    [InlineData("")]
    [InlineData("DesktopOrganizer")]
    public void TruncateToolTip_ShortText_IsReturnedUnchanged(string text)
    {
        Assert.Equal(text, NotifyIconAdapter.TruncateToolTip(text));
        var atLimit = new string('x', NotifyIconAdapter.MaxToolTipLength);
        Assert.Equal(atLimit, NotifyIconAdapter.TruncateToolTip(atLimit));
    }

    /// <summary>A longer text is cut to exactly 127 characters.</summary>
    [Fact]
    public void TruncateToolTip_LongText_IsCutTo127()
    {
        var result = NotifyIconAdapter.TruncateToolTip(new string('y', 300));

        Assert.Equal(127, result.Length);
    }

    /// <summary>The cut never leaves half of a surrogate pair behind.</summary>
    [Fact]
    public void TruncateToolTip_CutInsideSurrogatePair_DropsTheHighSurrogate()
    {
        var text = new string('a', 126) + "\U0001F600" + "tail";

        var result = NotifyIconAdapter.TruncateToolTip(text);

        Assert.Equal(126, result.Length);
        Assert.DoesNotContain(result, char.IsSurrogate);
    }

    /// <summary>A null text (loose caller) is treated as empty instead of throwing.</summary>
    [Fact]
    public void TruncateToolTip_Null_IsEmpty()
    {
        Assert.Equal(string.Empty, NotifyIconAdapter.TruncateToolTip(null));
    }
}
