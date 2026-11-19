// #2680 F130. Behaviour tests for the cost-alert threshold primitive: these
// pin the observable parse result, the comparison and the canonical rendering,
// without reading config, starting a process or touching the TUI.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class CostAlertThresholdTests
{
    [Test]
    public async Task ParsesBareAmount()
    {
        var threshold = CostAlertThreshold.Parse("5");

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.IsEnabled).IsTrue();
        await Assert.That(threshold.AmountUsd).IsEqualTo(5m);
    }

    [Test]
    public async Task ParsesCurrencySymbolAndDecimals()
    {
        var threshold = CostAlertThreshold.Parse("$5.50");

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.AmountUsd).IsEqualTo(5.50m);
        await Assert.That(threshold.IsEnabled).IsTrue();
    }

    [Test]
    public async Task ParsesLeadingDecimalPointAndTrimsWhitespace()
    {
        var threshold = CostAlertThreshold.Parse("  .50  ");

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.AmountUsd).IsEqualTo(0.50m);
    }

    [Test]
    [Arguments("off")]
    [Arguments("OFF")]
    [Arguments(" None ")]
    [Arguments("disabled")]
    public async Task ParsesDisabledWordsCaseInsensitively(string input)
    {
        var threshold = CostAlertThreshold.Parse(input);

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.IsEnabled).IsFalse();
        await Assert.That(threshold.AmountUsd).IsEqualTo(0m);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("+5")]
    [Arguments("1,000")]
    [Arguments("1.234")]
    [Arguments("1e3")]
    [Arguments("abc")]
    [Arguments("$")]
    [Arguments("1000001")]
    public async Task RejectsInvalidInput(string? input)
    {
        await Assert.That(CostAlertThreshold.Parse(input)).IsNull();
    }

    [Test]
    public async Task DisabledThresholdNeverFires()
    {
        var threshold = CostAlertThreshold.Disabled;

        await Assert.That(threshold.IsExceeded(0m)).IsFalse();
        await Assert.That(threshold.IsExceeded(1_000_000m)).IsFalse();
    }

    [Test]
    public async Task EnabledThresholdFiresOnlyStrictlyAboveAmount()
    {
        var threshold = CostAlertThreshold.Parse("5.00");

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.IsExceeded(4.99m)).IsFalse();
        await Assert.That(threshold.IsExceeded(5.00m)).IsFalse();
        await Assert.That(threshold.IsExceeded(5.01m)).IsTrue();
    }

    [Test]
    public async Task RendersCanonicalArgumentAndRoundTrips()
    {
        var threshold = CostAlertThreshold.Parse("$5.5");

        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.ToString()).IsEqualTo("$5.50");
        await Assert.That(CostAlertThreshold.Parse(threshold.ToString())).IsEqualTo(threshold);
        await Assert.That(CostAlertThreshold.Disabled.ToString()).IsEqualTo("off");
    }

    [Test]
    public async Task TryParseReportsSuccessAndFailure()
    {
        bool ok = CostAlertThreshold.TryParse("2.25", out var parsed);
        await Assert.That(ok).IsTrue();
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.AmountUsd).IsEqualTo(2.25m);

        bool bad = CostAlertThreshold.TryParse("nope", out var failed);
        await Assert.That(bad).IsFalse();
        await Assert.That(failed).IsNull();
    }
}
