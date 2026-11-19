// #2680 F092. Behaviour tests for the pure shell.env aggregation leaf:
// ordered precedence, per-call overrides, detached results and fail-closed
// validation. Nothing here launches a process or touches product wiring.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class ShellEnvironmentAggregatorTests
{
    private static ShellEnvironmentContribution Contribution(
        string source,
        params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in variables)
            map[name] = value;
        return new ShellEnvironmentContribution(source, map);
    }

    [Test]
    public async Task Aggregate_NoInputs_EmptySuccess()
    {
        var result = ShellEnvironmentAggregator.Aggregate(null);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Aggregate_OneContribution_VariablesPresent()
    {
        var result = ShellEnvironmentAggregator.Aggregate(
            [Contribution("plugin", ("HARBOR_FOO", "bar"), ("HARBOR_NUM", "42"))]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value["HARBOR_FOO"]).IsEqualTo("bar");
        await Assert.That(result.Value["HARBOR_NUM"]).IsEqualTo("42");
    }

    [Test]
    public async Task Aggregate_LaterContribution_Wins()
    {
        var result = ShellEnvironmentAggregator.Aggregate([
            Contribution("first", ("HARBOR_MODE", "one"), ("HARBOR_A", "a")),
            Contribution("second", ("HARBOR_MODE", "two"))
        ]);

        await Assert.That(result.Value["HARBOR_MODE"]).IsEqualTo("two");
        await Assert.That(result.Value["HARBOR_A"]).IsEqualTo("a");
    }

    [Test]
    public async Task Aggregate_CallOverrides_WinOverEveryContribution()
    {
        var result = ShellEnvironmentAggregator.Aggregate(
            [Contribution("plugin", ("HARBOR_MODE", "plugin"))],
            new Dictionary<string, string> { ["HARBOR_MODE"] = "explicit", ["HARBOR_NEW"] = "fresh" });

        await Assert.That(result.Value["HARBOR_MODE"]).IsEqualTo("explicit");
        await Assert.That(result.Value["HARBOR_NEW"]).IsEqualTo("fresh");
    }

    [Test]
    public async Task Aggregate_KeysAreCaseSensitive()
    {
        var result = ShellEnvironmentAggregator.Aggregate([
            Contribution("first", ("HARBOR_KEY", "upper")),
            Contribution("second", ("harbor_key", "lower"))
        ]);

        await Assert.That(result.Value.Count).IsEqualTo(2);
        await Assert.That(result.Value["HARBOR_KEY"]).IsEqualTo("upper");
        await Assert.That(result.Value["harbor_key"]).IsEqualTo("lower");
    }

    [Test]
    public async Task Aggregate_ResultIsDetachedFromInputs()
    {
        var source = new Dictionary<string, string> { ["HARBOR_FOO"] = "before" };
        var result = ShellEnvironmentAggregator.Aggregate([new("plugin", source)]);

        source["HARBOR_FOO"] = "after";
        await Assert.That(result.Value["HARBOR_FOO"]).IsEqualTo("before");
    }

    [Test]
    public async Task IsValidName_AcceptsPosixIdentifiers()
    {
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HARBOR_MODE")).IsTrue();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("_private")).IsTrue();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("A1_b2")).IsTrue();
    }

    [Test]
    public async Task IsValidName_RejectsMalformedNames()
    {
        await Assert.That(ShellEnvironmentAggregator.IsValidName(null)).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("1LEADING")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HAS=EQUALS")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HAS SPACE")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HAS-DASH")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HAS.DOT")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName("HARBOR_Е")).IsFalse();
        await Assert.That(ShellEnvironmentAggregator.IsValidName(new string('A', ShellEnvironmentAggregator.MaxNameLength + 1))).IsFalse();
    }

    [Test]
    public async Task Aggregate_InvalidName_FailsClosedWithSource()
    {
        var result = ShellEnvironmentAggregator.Aggregate([Contribution("bad-plugin", ("1BAD", "x"))]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("bad-plugin");
        await Assert.That(result.Error).Contains("invalid variable name");
    }

    [Test]
    public async Task Aggregate_InvalidOverrideName_FailsClosed()
    {
        var result = ShellEnvironmentAggregator.Aggregate(
            null,
            new Dictionary<string, string> { ["NO-DASH"] = "x" });

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(ShellEnvironmentAggregator.CallSource);
    }

    [Test]
    public async Task Aggregate_TooLongValue_FailsClosed()
    {
        string longValue = new('x', ShellEnvironmentAggregator.MaxValueLength + 1);
        var result = ShellEnvironmentAggregator.Aggregate([Contribution("plugin", ("HARBOR_BIG", longValue))]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invalid value");
    }

    [Test]
    public async Task Aggregate_NullValue_FailsClosed()
    {
        var source = new Dictionary<string, string> { ["HARBOR_NULL"] = null! };
        var result = ShellEnvironmentAggregator.Aggregate([new("plugin", source)]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invalid value");
    }

    [Test]
    public async Task Aggregate_TooManySources_FailsClosed()
    {
        var sources = new List<ShellEnvironmentContribution>(ShellEnvironmentAggregator.MaxSources + 1);
        for (int i = 0; i <= ShellEnvironmentAggregator.MaxSources; i++)
            sources.Add(Contribution($"p{i}", ("HARBOR_X", "v")));

        var result = ShellEnvironmentAggregator.Aggregate(sources);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Too many");
    }

    [Test]
    public async Task Aggregate_TooManyVariablesInSource_FailsClosed()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i <= ShellEnvironmentAggregator.MaxVariablesPerSource; i++)
            map[$"HARBOR_V{i}"] = "v";

        var result = ShellEnvironmentAggregator.Aggregate([new("plugin", map)]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("exceeds");
    }

    [Test]
    public async Task Aggregate_BlankSource_FailsClosed()
    {
        var result = ShellEnvironmentAggregator.Aggregate(
            [new ShellEnvironmentContribution("   ", new Dictionary<string, string>())]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no source name");
    }
}
