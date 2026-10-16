using Harbor.Abstractions.Extensions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1553 batch 1: <see cref="Ensure" /> guards are behavior-identical to the
///     BCL helpers they replace (same exception type, message, and parameter name).
/// </summary>
public class EnsureTests
{
    [Test]
    public async Task NotNull_Null_ThrowsWithArgumentExpressionName()
    {
        string? value = null;
        var ex = Assert.Throws<ArgumentNullException>(() => Ensure.NotNull(value));
        await Assert.That(ex.ParamName).IsEqualTo(nameof(value));
    }

    [Test]
    public async Task NotNull_Null_MessageMatchesBclHelper()
    {
        string? value = null;
        var expected = Assert.Throws<ArgumentNullException>(() => ArgumentNullException.ThrowIfNull(value));
        var actual = Assert.Throws<ArgumentNullException>(() => Ensure.NotNull(value));
        await Assert.That(actual.Message).IsEqualTo(expected.Message);
    }

    [Test]
    public async Task NotNull_Value_ReturnsIt()
    {
        await Assert.That(Ensure.NotNull("x")).IsEqualTo("x");
    }

    [Test]
    public async Task NotBlank_Whitespace_ThrowsWithArgumentExpressionName()
    {
        string? value = "  ";
        var ex = Assert.Throws<ArgumentException>(() => Ensure.NotBlank(value));
        await Assert.That(ex.ParamName).IsEqualTo(nameof(value));
    }

    [Test]
    public async Task NotBlank_Null_MessageMatchesBclHelper()
    {
        string? value = null;
        var expected = Assert.Throws<ArgumentException>(() => ArgumentException.ThrowIfNullOrWhiteSpace(value));
        var actual = Assert.Throws<ArgumentException>(() => Ensure.NotBlank(value));
        await Assert.That(actual.Message).IsEqualTo(expected.Message);
    }

    [Test]
    public async Task NotEmpty_Empty_ThrowsWithArgumentExpressionName()
    {
        string? value = string.Empty;
        var ex = Assert.Throws<ArgumentException>(() => Ensure.NotEmpty(value));
        await Assert.That(ex.ParamName).IsEqualTo(nameof(value));
    }

    [Test]
    public async Task NotEmpty_Value_ReturnsIt()
    {
        await Assert.That(Ensure.NotEmpty("x")).IsEqualTo("x");
    }
}
