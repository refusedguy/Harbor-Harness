using System.Diagnostics;
using System.Diagnostics.Metrics;
using CSharpFunctionalExtensions;
using Harbor.Telemetry;
using Harbor.Telemetry.Fixtures;

namespace Harbor.CodeGen.Tests;

/// <summary>
///     The behavioural half of the guard for issue #1797: the decorator the
///     generator emitted really opens a span and really records the call's
///     metrics.
/// </summary>
/// <remarks>
///     <para>
///         Observed with plain <see cref="ActivityListener" /> and
///         <see cref="MeterListener" /> — no OTel SDK, the same harness
///         <c>tests/Harbor.Telemetry.Tests/DecoratorTelemetryTests.cs</c> uses.
///     </para>
///     <para>
///         The names asserted here are the contract the generator derives from
///         the fixture's markers: the span <c>fixture.do_work</c>, the counter
///         <c>fixture.calls</c>, the duration histogram
///         <c>fixture.duration.ms</c> and the status tag
///         <c>fixture.status</c>. A rename of any of them is a change to a
///         published name, and this file is where that change has to show up.
///     </para>
/// </remarks>
[NotInParallel("telemetry")]
public class ObservedDecoratorTests : IDisposable
{
    private readonly List<Activity> _stoppedSpans = [];
    private readonly List<(string Instrument, double Value, string? Status)> _metrics = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public ObservedDecoratorTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HarborTelemetrySources.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _stoppedSpans.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HarborTelemetrySources.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? status = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key.EndsWith(".status", StringComparison.Ordinal))
                {
                    status = tag.Value?.ToString();
                }
            }

            _metrics.Add((instrument.Name, value, status));
        });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    [Test]
    public async Task TheObservedSeam_OpensItsSpan_AndRecordsItsMetrics()
    {
        var decorated = new ObservedFixture(new StubFixture(), new ActivityTracer(), new MeterMetrics());

        Result result = await decorated.DoWorkAsync();

        await Assert.That(result.IsSuccess).IsTrue();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "fixture.do_work");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem("fixture.status")).IsEqualTo("ok");

        await Assert.That(_metrics.Any(m => m.Instrument == "fixture.calls" && m.Status == "ok")).IsTrue();
        await Assert.That(_metrics.Any(m => m.Instrument == "fixture.duration.ms" && m.Value >= 0)).IsTrue();
    }

    [Test]
    public async Task TheObservedSeam_AFailedCall_MarksTheSpan_AndCountsTheFailure()
    {
        var decorated = new ObservedFixture(new StubFixture(), new ActivityTracer(), new MeterMetrics());

        Result result = await decorated.FailAsync();

        await Assert.That(result.IsFailure).IsTrue();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "fixture.fail");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem("fixture.status")).IsEqualTo("failure");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);

        await Assert.That(_metrics.Any(m => m.Instrument == "fixture.calls" && m.Status == "failure")).IsTrue();
    }

    [Test]
    public async Task TheExcludedMember_IsForwarded_AndOpensNoSpan()
    {
        var decorated = new ObservedFixture(new StubFixture(), new ActivityTracer(), new MeterMetrics());

        Result<int> computed = decorated.Compute(42);

        await Assert.That(computed.IsSuccess).IsTrue();
        await Assert.That(computed.Value).IsEqualTo(42);

        // The seam EXCLUDES Compute, so the call is forwarded and measured by
        // nobody. An excluded member is an explicit decision, and the
        // generator honours it without a span.
        await Assert.That(_stoppedSpans.Any(a => a.DisplayName == "fixture.compute")).IsFalse();
    }

    [Test]
    public async Task TheMemberLevelMarker_NamesItsSpan_AndCapturesItsArguments()
    {
        var decorated = new ObservedFixtureWithArgs(new StubFixtureWithArgs(), new ActivityTracer(), new MeterMetrics());

        Result result = await decorated.RunAsync("hello", 3);

        await Assert.That(result.IsSuccess).IsTrue();

        Activity? span = _stoppedSpans.FirstOrDefault(a => a.DisplayName == "fixture.with_args");
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.GetTagItem("fixture_with_args.status")).IsEqualTo("ok");
        await Assert.That(span.GetTagItem("arg.label")).IsEqualTo("hello");
        await Assert.That(span.GetTagItem("arg.count")).IsEqualTo(3);
    }

    [Test]
    public async Task TheUnmarkedSeam_GetsNoDecorator()
    {
        // The negative control: the generator emits nothing for a seam that
        // carries no marker, so there is no decorator type to find.
        Type? generated = typeof(IPlainFixture).Assembly.GetType("Harbor.Telemetry.Fixtures.ObservedPlainFixture");

        await Assert.That(generated).IsNull();
    }

    private sealed class StubFixture : IFixture
    {
        public Task<Result> DoWorkAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> FailAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Failure("boom"));

        public Result<int> Compute(int seed) => Result.Success(seed);

        public string Name => "stub";
    }

    private sealed class StubFixtureWithArgs : IFixtureWithArgs
    {
        public Task<Result> RunAsync(string label, int count, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }
}
