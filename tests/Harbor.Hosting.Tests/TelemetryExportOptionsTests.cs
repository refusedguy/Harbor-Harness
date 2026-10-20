using Harbor.Abstractions.Env;
using Harbor.Telemetry;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #1741/#1742: the export configuration of the visible telemetry sink.
///     <para>
///         The rule this suite pins is the one the owner asked for: telemetry
///         is INSTRUMENTED by default and VISIBLE only on request. The default
///         composition therefore produces no sink at all, and the environment
///         is what turns it on — for one run, without touching the host.
///     </para>
/// </remarks>
[NotInParallel("hosting")]
public class TelemetryExportOptionsTests
{
    private readonly CapturingLogger _logger = new();

    /// <summary>A throwaway Harbor home — the resolver only joins paths, it writes nothing.</summary>
    private static readonly string HarborDir = Path.Combine(Path.GetTempPath(), "harbor-telemetry-test");

    /// <summary>
    ///     The default composition registers no sink: a normal run writes
    ///     nothing, and a test run stays quiet.
    /// </summary>
    [Test]
    public async Task DefaultComposition_RegistersNoSink()
    {
        var options = new HarborComposeOptions { HarborDir = HarborDir };

        TelemetryExportOptions? resolved = TelemetryModule.ResolveExportOptions(
            options,
            _logger,
            _ => null);

        await Assert.That(resolved).IsNull();
    }

    /// <summary>The environment turns the sink on, and the environment wins over the option.</summary>
    [Test]
    public async Task Environment_TurnsTheSinkOn_AndWins()
    {
        var options = new HarborComposeOptions
        {
            HarborDir = HarborDir,
            TelemetryExport = TelemetryExportTarget.None,
        };

        TelemetryExportOptions? resolved = TelemetryModule.ResolveExportOptions(
            options,
            _logger,
            name => name switch
            {
                HarborEnv.TelemetryExport => "console",
                HarborEnv.TelemetryExportMaxRecordBytes => "512",
                _ => null,
            });

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Target).IsEqualTo(TelemetryExportTarget.Console);
        await Assert.That(resolved!.MaxRecordBytes).IsEqualTo(512);
    }

    /// <summary>The file target gets a path — the one under the Harbor home when none is given.</summary>
    [Test]
    public async Task FileTarget_GetsAPathUnderTheHarborHome()
    {
        var options = new HarborComposeOptions { HarborDir = HarborDir };

        TelemetryExportOptions? resolved = TelemetryModule.ResolveExportOptions(
            options,
            _logger,
            name => name == HarborEnv.TelemetryExport ? "file" : null);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Target).IsEqualTo(TelemetryExportTarget.File);
        await Assert.That(resolved!.FilePath).IsNotNull();
        await Assert.That(resolved!.FilePath!).Contains("telemetry");
    }

    /// <summary>
    ///     An unreadable target turns the sink OFF and says so: a typo in an
    ///     environment variable must not silently produce (or silently hide)
    ///     telemetry.
    /// </summary>
    [Test]
    public async Task AnUnknownTarget_TurnsTheSinkOff_AndIsReported()
    {
        var options = new HarborComposeOptions { HarborDir = HarborDir };

        TelemetryExportOptions? resolved = TelemetryModule.ResolveExportOptions(
            options,
            _logger,
            name => name == HarborEnv.TelemetryExport ? "carrier-pigeon" : null);

        await Assert.That(resolved).IsNull();
        await Assert.That(_logger.Messages.Any(m => m.Contains(HarborEnv.TelemetryExport))).IsTrue();
    }

    /// <summary>A bound that is not a positive integer falls back instead of disabling the sink.</summary>
    [Test]
    public async Task AnUnreadableBound_FallsBackToTheDefault()
    {
        var options = new HarborComposeOptions { HarborDir = HarborDir };

        TelemetryExportOptions? resolved = TelemetryModule.ResolveExportOptions(
            options,
            _logger,
            name => name switch
            {
                HarborEnv.TelemetryExport => "console",
                HarborEnv.TelemetryExportMaxBufferBytes => "not-a-number",
                _ => null,
            });

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.MaxBufferBytes).IsEqualTo(TelemetryExportOptions.DefaultMaxBufferBytes);
        await Assert.That(_logger.Messages.Any(m => m.Contains(HarborEnv.TelemetryExportMaxBufferBytes))).IsTrue();
    }

    /// <summary>Minimal logger that keeps the rendered lines for assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
