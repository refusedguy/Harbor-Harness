using Microsoft.Extensions.Logging;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The recording provider is a first-class sink: a test can say which
///     category wrote a line, at what level, with which scope and which
///     structured field, instead of scraping a console string.
/// </summary>
public class RecordingLoggerProviderTests
{
    [Test]
    public async Task Records_Category_Level_Event_Message_Field_And_Scopes()
    {
        var provider = new RecordingLoggerProvider();
        ILogger logger = provider.CreateLogger("probe.category");

        using (logger.BeginScope("outer"))
        using (logger.BeginScope("inner"))
        {
            logger.LogWarning(new EventId(7), "value {Value}", 42);
        }

        var entries = provider.EntriesFor("probe.category");
        await Assert.That(entries.Count).IsEqualTo(1);
        RecordedLogEntry entry = entries[0];
        await Assert.That(entry.Category).IsEqualTo("probe.category");
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entry.EventId).IsEqualTo(7);
        await Assert.That(entry.Message).IsEqualTo("value 42");
        await Assert.That(LogCapture.FieldOf(entry.State, "Value")).IsEqualTo("42")
            .Because("a source-generated log call keeps its named fields, so the assertion can read Attempt/Value instead of matching prose");
        await Assert.That(entry.Scopes.Count).IsEqualTo(2);
        await Assert.That(entry.Scopes[0]).IsEqualTo("outer");
        await Assert.That(entry.Scopes[1]).IsEqualTo("inner");
    }

    [Test]
    public async Task Filters_By_Category()
    {
        var provider = new RecordingLoggerProvider();
        provider.CreateLogger("a").LogInformation("from-a");
        provider.CreateLogger("b").LogError("from-b");

        await Assert.That(provider.Entries.Count).IsEqualTo(2);
        await Assert.That(provider.EntriesFor("a").Count).IsEqualTo(1);
        await Assert.That(provider.EntriesFor("a")[0].Message).IsEqualTo("from-a");
        await Assert.That(provider.EntriesFor("b")[0].Level).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task Scope_Is_Popped_When_Its_Lease_Is_Disposed()
    {
        var provider = new RecordingLoggerProvider();
        ILogger logger = provider.CreateLogger("scope.probe");

        using (logger.BeginScope("only-inside"))
        {
            logger.LogInformation("inside");
        }

        logger.LogInformation("outside");

        await Assert.That(provider.Entries.Count).IsEqualTo(2);
        await Assert.That(provider.Entries[0].Scopes.Count).IsEqualTo(1);
        await Assert.That(provider.Entries[0].Scopes[0]).IsEqualTo("only-inside");
        await Assert.That(provider.Entries[1].Scopes.Count).IsEqualTo(0)
            .Because("a scope that leaked past its lease would make every later line look like it came from the same operation");
    }
}
