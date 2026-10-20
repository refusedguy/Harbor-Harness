using Microsoft.Extensions.Logging;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The capturing loggers. The properties worth pinning are the ones the
///     hand-written copies differed on: what gets recorded, what the level
///     filter does, and that a factory's loggers all land in one place.
/// </summary>
public class CapturingLoggerTests
{
    [Test]
    public async Task Records_Everything_By_Default()
    {
        var logger = new CapturingLogger();

        logger.LogInformation("hello {Name}", "world");
        logger.LogWarning("careful");

        await Assert.That(logger.Entries.Count).IsEqualTo(2);
        await Assert.That(logger.Messages[0]).IsEqualTo("hello world");
        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
    }

    [Test]
    public async Task Honours_The_Recording_Level_Filter()
    {
        var logger = new CapturingLogger(LogLevel.Warning);

        logger.LogInformation("noise");
        logger.LogWarning("signal");

        await Assert.That(logger.Entries.Count).IsEqualTo(1)
            .Because("the filter is a RECORDING filter: the entries below the level are not kept, so a test that counts them counts what it asked for");
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Is_Enabled_For_Every_Level_So_Production_Code_Takes_Its_Logging_Branch()
    {
        var logger = new CapturingLogger();

        await Assert.That(logger.IsEnabled(LogLevel.Trace)).IsTrue()
            .Because("a logger that said 'no' would let the test pass without the code under test ever composing the message it was supposed to log");
    }

    [Test]
    public async Task Keeps_The_Event_Id_So_Entries_Can_Be_Addressed()
    {
        var logger = new CapturingLogger();

        logger.LogInformation(new EventId(41), "targeted");
        logger.LogInformation(new EventId(42), "other");

        await Assert.That(logger.MessagesFor(41)).IsEqualTo("targeted")
            .Because("one of the hand-written copies was indexed by event id, and that question has no answer without it");
    }

    [Test]
    public async Task Exposes_Warnings_And_Errors_Separately()
    {
        var logger = new CapturingLogger();

        logger.LogWarning("w");
        logger.LogError("e");

        // The projections are "at this level and above", which is what the
        // hand-written copies that filtered by level exposed: the logger
        // recorded from its floor upwards, and the accessor returned
        // everything from the named level upwards.
        await Assert.That(logger.Warnings.Count).IsEqualTo(2);
        await Assert.That(logger.Errors.Count).IsEqualTo(1);
        await Assert.That(logger.Warnings[0]).IsEqualTo("w");
        await Assert.That(logger.Errors[0]).IsEqualTo("e");
    }

    [Test]
    public async Task The_Generic_Flavour_Behaves_The_Same()
    {
        var logger = new CapturingLogger<CapturingLoggerTests>();

        logger.LogInformation("typed");

        await Assert.That(logger.Messages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_Factory_Shares_One_Capture_Across_Its_Loggers()
    {
        var factory = new CapturingLoggerFactory();

        ILogger first = factory.CreateLogger("a");
        ILogger second = factory.CreateLogger("b");
        first.LogInformation("one");
        second.LogWarning("two");

        await Assert.That(factory.Entries.Count).IsEqualTo(2)
            .Because("the factory exists so that a whole composition root's logging can be asserted at once — two separate captures would defeat it");
    }

    [Test]
    public async Task Keeps_The_Structured_State_Of_Each_Entry()
    {
        var logger = new CapturingLogger();

        logger.LogInformation("block {BlockType} from {Provider}", "tool_call", "stub");

        await Assert.That(LogCapture.FieldOf(logger.Capture.RawEntries[0].State, "BlockType")).IsEqualTo("tool_call")
            .Because("a source-generated log call hands the logger a list of named fields, and flattening them away at capture time would lose them for good");
    }
}
