using DesktopOrganizer.Attributes;

namespace DesktopOrganizer.Tests.Unit.Attributes;

/// <summary>
/// Unit tests for <see cref="NativeNameAttribute" />.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Attributes))]
public class NativeNameAttributeTests
{
    /// <summary>
    /// Ensures that the constructor correctly assigns the name.
    /// </summary>
    [Fact]
    public void Constructor_AssignsName()
    {
        // Arrange
        const string expectedName = "TestName";

        // Act
        var attribute = new NativeNameAttribute(expectedName);

        // Assert
        Assert.Equal(expectedName, attribute.Name);
    }

    /// <summary>
    /// Ensures that the Name property is read-only and immutable.
    /// </summary>
    [Fact]
    public void Name_IsReadOnly()
    {
        // Arrange
        const string initialName = "InitialName";
        var attribute = new NativeNameAttribute(initialName);

        // Act
        var retrievedName = attribute.Name;

        // Assert
        Assert.Equal(initialName, retrievedName);
    }

    /// <summary>
    /// Ensures that the constructor throws an exception when the name is null or empty.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_ThrowsException_WhenNameIsNullOrEmpty(string invalidName) =>
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new NativeNameAttribute(invalidName));
}
