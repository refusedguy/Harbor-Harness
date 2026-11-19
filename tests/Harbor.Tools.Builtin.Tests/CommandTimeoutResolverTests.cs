using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F177. <see cref="CommandTimeoutResolver" /> is a pure parser and
///     policy leaf. These tests pin its observable output without reading an
///     environment variable, touching a config store or starting a process.
/// </summary>
public class CommandTimeoutResolverTests
{
    [Test]
    public async Task BoundariesAreAppliedThroughBehaviour()
    {
        await Assert.That(CommandTimeoutResolver.Resolve(null)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(CommandTimeoutResolver.MinSeconds - 1)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(CommandTimeoutResolver.MaxSeconds + 1)).IsEqualTo(30);
    }

    [Test]
    public async Task Resolve_KeepsRequestsInsideTheRange()
    {
        await Assert.That(CommandTimeoutResolver.Resolve(CommandTimeoutResolver.MinSeconds)).IsEqualTo(1);
        await Assert.That(CommandTimeoutResolver.Resolve(30)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(CommandTimeoutResolver.MaxSeconds)).IsEqualTo(600);
    }

    [Test]
    public async Task Resolve_FallsBackToDefaultOutsideTheRange()
    {
        await Assert.That(CommandTimeoutResolver.Resolve(null)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(0)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(-1)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(601)).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.Resolve(int.MaxValue)).IsEqualTo(30);
    }

    [Test]
    public async Task TryParse_AcceptsBareSecondsAndUnitSuffixes()
    {
        await Assert.That(CommandTimeoutResolver.TryParse("30", out int bare)).IsTrue();
        await Assert.That(bare).IsEqualTo(30);

        await Assert.That(CommandTimeoutResolver.TryParse("15s", out int seconds)).IsTrue();
        await Assert.That(seconds).IsEqualTo(15);

        await Assert.That(CommandTimeoutResolver.TryParse("10sec", out int sec)).IsTrue();
        await Assert.That(sec).IsEqualTo(10);

        await Assert.That(CommandTimeoutResolver.TryParse("2m", out int minutes)).IsTrue();
        await Assert.That(minutes).IsEqualTo(120);

        await Assert.That(CommandTimeoutResolver.TryParse("5min", out int mins)).IsTrue();
        await Assert.That(mins).IsEqualTo(300);
    }

    [Test]
    public async Task TryParse_TrimsAndIgnoresSuffixCase()
    {
        await Assert.That(CommandTimeoutResolver.TryParse("  45S  ", out int upper)).IsTrue();
        await Assert.That(upper).IsEqualTo(45);

        await Assert.That(CommandTimeoutResolver.TryParse("2M", out int minutes)).IsTrue();
        await Assert.That(minutes).IsEqualTo(120);

        await Assert.That(CommandTimeoutResolver.TryParse(" 7 ", out int bare)).IsTrue();
        await Assert.That(bare).IsEqualTo(7);
    }

    [Test]
    public async Task TryParse_RejectsMalformedOrNonPositiveValues()
    {
        string[] invalid = [null!, "", "   ", "abc", "-5", "+5", "1.5", "0", "0s", "1x", "m", "2 h", "s"];
        foreach (string raw in invalid)
        {
            bool parsed = CommandTimeoutResolver.TryParse(raw, out int seconds);
            await Assert.That(parsed).IsFalse();
            await Assert.That(seconds).IsEqualTo(0);
        }
    }

    [Test]
    public async Task TryParse_RejectsValuesThatOverflowWholeSeconds()
    {
        await Assert.That(CommandTimeoutResolver.TryParse("2147483647m", out int seconds)).IsFalse();
        await Assert.That(seconds).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveConfigured_AppliesPolicyAfterParsing()
    {
        await Assert.That(CommandTimeoutResolver.ResolveConfigured("2m")).IsEqualTo(120);
        await Assert.That(CommandTimeoutResolver.ResolveConfigured("45")).IsEqualTo(45);
        await Assert.That(CommandTimeoutResolver.ResolveConfigured("601")).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.ResolveConfigured("0")).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.ResolveConfigured("garbage")).IsEqualTo(30);
        await Assert.That(CommandTimeoutResolver.ResolveConfigured(null)).IsEqualTo(30);
    }

    [Test]
    public async Task ToTimeSpan_UsesTheResolvedSeconds()
    {
        await Assert.That(CommandTimeoutResolver.ToTimeSpan(null)).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(CommandTimeoutResolver.ToTimeSpan(5)).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(CommandTimeoutResolver.ToTimeSpan(700)).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
