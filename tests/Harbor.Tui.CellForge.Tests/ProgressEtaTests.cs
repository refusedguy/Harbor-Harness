using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #2690, idea 6: pure ETA model. No rendering or global palette here, so these
// tests need no serialization and use a hand-driven TimeProvider for determinism.
public class ProgressEtaTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private static ProgressEta Create(out ManualClock clock)
    {
        clock = new ManualClock(Start);
        return new ProgressEta(clock);
    }

    [Test]
    public async Task Eta_WithoutSamples_HasNoEstimate()
    {
        ProgressEta eta = Create(out _);

        await Assert.That(eta.SampleCount).IsEqualTo(0);
        await Assert.That(eta.Speed).IsNull();
        await Assert.That(eta.SecondsRemaining).IsNull();
        await Assert.That(eta.Suffix).IsNull();
    }

    [Test]
    public async Task Eta_FirstSample_StillHasNoEstimate()
    {
        ProgressEta eta = Create(out _);

        eta.AddSample(0.25);

        await Assert.That(eta.SampleCount).IsEqualTo(1);
        await Assert.That(eta.Speed).IsNull();
        await Assert.That(eta.SecondsRemaining).IsNull();
    }

    [Test]
    public async Task Eta_SteadyProgress_EstimatesFromTheWindow()
    {
        ProgressEta eta = Create(out ManualClock clock);

        eta.AddSample(0.25);
        clock.Advance(TimeSpan.FromSeconds(5));
        eta.AddSample(0.5);

        await Assert.That(eta.Speed).IsNotNull();
        await Assert.That(Math.Abs(eta.Speed!.Value - 0.05) < 1e-9).IsTrue();
        await Assert.That(Math.Abs(eta.SecondsRemaining!.Value - 10) < 1e-6).IsTrue();
        await Assert.That(eta.Suffix).IsEqualTo("10s");
    }

    [Test]
    public async Task Eta_ExplicitTimestamp_RecordsThatInstant()
    {
        ProgressEta eta = Create(out _);

        eta.AddSample(Start, 0.25);
        eta.AddSample(Start + TimeSpan.FromSeconds(4), 0.5);

        await Assert.That(Math.Abs(eta.Speed!.Value - 0.0625) < 1e-9).IsTrue();
        await Assert.That(Math.Abs(eta.SecondsRemaining!.Value - 8) < 1e-6).IsTrue();
    }

    [Test]
    public async Task Eta_StalledProgress_HasNoEstimate()
    {
        ProgressEta eta = Create(out ManualClock clock);

        eta.AddSample(0.3);
        clock.Advance(TimeSpan.FromSeconds(5));
        eta.AddSample(0.3);

        await Assert.That(eta.Speed).IsNull();
        await Assert.That(eta.SecondsRemaining).IsNull();
        await Assert.That(eta.Suffix).IsNull();
    }

    [Test]
    public async Task Eta_Regression_ClearsTheWindow()
    {
        ProgressEta eta = Create(out ManualClock clock);

        eta.AddSample(0.75);
        clock.Advance(TimeSpan.FromSeconds(2));
        eta.AddSample(0.25);
        clock.Advance(TimeSpan.FromSeconds(2));
        eta.AddSample(0.5);

        await Assert.That(eta.SampleCount).IsEqualTo(2);
        await Assert.That(Math.Abs(eta.Speed!.Value - 0.125) < 1e-9).IsTrue();
        await Assert.That(Math.Abs(eta.SecondsRemaining!.Value - 4) < 1e-6).IsTrue();
    }

    [Test]
    public async Task Eta_NonAdvancingClock_ReplacesTheNewestSample()
    {
        ProgressEta eta = Create(out _);

        eta.AddSample(0.25);
        eta.AddSample(0.5);

        await Assert.That(eta.SampleCount).IsEqualTo(1);
        await Assert.That(eta.Speed).IsNull();
        await Assert.That(eta.SecondsRemaining).IsNull();
    }

    [Test]
    public async Task Eta_HistoryIsCapped_AndUsesOnlyTheWindow()
    {
        ProgressEta eta = Create(out ManualClock clock);

        for (int i = 0; i < 150; i++)
        {
            eta.AddSample((i + 1) / 256.0);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        await Assert.That(eta.SampleCount).IsEqualTo(ProgressEta.DefaultMaxSamples);
        await Assert.That(Math.Abs(eta.Speed!.Value - (1.0 / 256)) < 1e-12).IsTrue();
        await Assert.That(Math.Abs(eta.SecondsRemaining!.Value - 106) < 1e-6).IsTrue();
        await Assert.That(eta.Suffix).IsEqualTo("1m 46s");
    }

    [Test]
    public async Task Eta_Completion_HasZeroRemainingAndNoSuffix()
    {
        ProgressEta eta = Create(out ManualClock clock);

        eta.AddSample(0.5);
        clock.Advance(TimeSpan.FromSeconds(1));
        eta.AddSample(1);

        await Assert.That(eta.SecondsRemaining).IsEqualTo(0);
        await Assert.That(eta.Suffix).IsNull();
    }

    [Test]
    public async Task Eta_Reset_ForgetsEverything()
    {
        ProgressEta eta = Create(out ManualClock clock);
        eta.AddSample(0.25);
        clock.Advance(TimeSpan.FromSeconds(5));
        eta.AddSample(0.5);

        eta.Reset();

        await Assert.That(eta.SampleCount).IsEqualTo(0);
        await Assert.That(eta.Speed).IsNull();
        await Assert.That(eta.SecondsRemaining).IsNull();
    }

    [Test]
    public async Task LabelText_AppendsSuffix_ButRespectsHiddenLabel()
    {
        var state = new GenericProgressState(0.5);

        await Assert.That(GenericProgressBar.LabelText(state)).IsEqualTo("50%");
        await Assert.That(GenericProgressBar.LabelText(state, "10s")).IsEqualTo("50% 10s");
        await Assert.That(GenericProgressBar.LabelText(state, null)).IsEqualTo("50%");
        await Assert.That(GenericProgressBar.LabelText(new GenericProgressState(0.5, ""), "10s")).IsEqualTo("");
        await Assert.That(GenericProgressBar.LabelText(new GenericProgressState(0.5, "Downloading"), "10s")).IsEqualTo("Downloading 10s");
    }

    [Test]
    public async Task Constructor_RejectsTooSmallAWindow()
    {
        await Assert.That(() => new ProgressEta(new ManualClock(Start), 1)).Throws<ArgumentOutOfRangeException>();
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
