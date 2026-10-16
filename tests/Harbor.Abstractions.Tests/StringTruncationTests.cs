using Harbor.Abstractions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #1566: the canonical truncation policy every former <c>Truncate</c> copy
///     now delegates to. Pins the two shapes (within-budget ellipsis vs.
///     beyond-budget drop-count marker) so a future policy tweak fails loudly.
/// </summary>
public class StringTruncationTests
{
    [Test]
    public async Task Truncate_ShortPassthrough()
    {
        await Assert.That("abc".Truncate(5)).IsEqualTo("abc");
        await Assert.That("abc".Truncate(3)).IsEqualTo("abc");
    }

    [Test]
    public async Task Truncate_Over_EllipsisWithinBudget()
    {
        string cut = "abcdef".Truncate(5);

        await Assert.That(cut).IsEqualTo("abcd…");
        await Assert.That(cut.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Truncate_MaxOne_EllipsisAlone()
    {
        await Assert.That("abcdef".Truncate(1)).IsEqualTo("…");
    }

    [Test]
    public async Task Truncate_NonPositive_Empty()
    {
        await Assert.That("abcdef".Truncate(0)).IsEqualTo(string.Empty);
        await Assert.That("abcdef".Truncate(-2)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Truncate_NullOrEmpty_Empty()
    {
        await Assert.That(((string?)null).Truncate(5)).IsEqualTo(string.Empty);
        await Assert.That(string.Empty.Truncate(5)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Truncate_CustomSuffix_WithinBudget()
    {
        string cut = "abcdef".Truncate(5, "...");

        await Assert.That(cut).IsEqualTo("ab...");
        await Assert.That(cut.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Truncate_EmptySuffix_HardClip()
    {
        await Assert.That("abcdef".Truncate(4, string.Empty)).IsEqualTo("abcd");
        await Assert.That("abc".Truncate(4, string.Empty)).IsEqualTo("abc");
        await Assert.That("abcdef".Truncate(0, string.Empty)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Truncate_SuffixLongerThanBudget_ClippedToBudget()
    {
        string cut = "abcdef".Truncate(2, "...");

        await Assert.That(cut).IsEqualTo("..");
        await Assert.That(cut.Length).IsEqualTo(2);
    }

    [Test]
    public async Task TruncateWithDropCount_Fits_Passthrough()
    {
        await Assert.That("abc".TruncateWithDropCount(200)).IsEqualTo("abc");
    }

    [Test]
    public async Task TruncateWithDropCount_DefaultSeparator_SubAgentShape()
    {
        string text = new('x', 10);

        await Assert.That(text.TruncateWithDropCount(6)).IsEqualTo(new string('x', 6) + "\n…[truncated 4 chars]");
    }

    [Test]
    public async Task TruncateWithDropCount_EmptySeparator_SupervisionShape()
    {
        string text = new('y', 10);

        await Assert.That(text.TruncateWithDropCount(6, string.Empty)).IsEqualTo(new string('y', 6) + "…[truncated 4 chars]");
    }

    [Test]
    public async Task TruncateWithDropCount_Detail_ProviderErrorShape()
    {
        string body = new('z', 1005);

        await Assert.That(body.TruncateWithDropCount(1000, detail: "showing first 1000"))
            .IsEqualTo(new string('z', 1000) + "\n…[truncated 5 chars; showing first 1000]");
    }
}
