using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #1101, partial: the diff extractor must answer "no diff" with one
///     absent value instead of a tuple of three nulls and a flag that can
///     disagree with them. These tests drive the public extractor and the
///     snapshot factory that consumes it, so they see what a card receives.
/// </summary>
public class DiffPayloadTests
{
    private const string EditArgs =
        """{"file_path":"src/app.cs","oldString":"var a = 1;","newString":"var a = 2;"}""";

    [Test]
    public async Task ExtractDiff_NonDiffTool_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("read", """{"file_path":"src/app.cs"}""");

        await Assert.That(payload.HasValue).IsFalse()
            .Because("a non-diff tool has no diff payload, whatever its arguments name");
    }

    [Test]
    public async Task ExtractDiff_AbsentArgs_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("edit", null!);

        await Assert.That(payload.HasValue).IsFalse()
            .Because("absent arguments are absence, not a JSON document that must be parsed");
    }

    [Test]
    public async Task ExtractDiff_BlankArgs_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("edit", "   ");

        await Assert.That(payload.HasValue).IsFalse()
            .Because("blank arguments carry no payload, so the extractor must not invent an object");
    }

    [Test]
    public async Task ExtractDiff_MalformedJson_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("edit", "{not json");

        await Assert.That(payload.HasValue).IsFalse()
            .Because("malformed arguments degrade to 'not a diff call' instead of throwing");
    }

    [Test]
    public async Task ExtractDiff_NonObjectJson_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("write", "[1,2,3]");

        await Assert.That(payload.HasValue).IsFalse()
            .Because("an array is not an arguments object and carries no file to diff");
    }

    [Test]
    public async Task ExtractDiff_DiffToolWithoutPayload_ReturnsNoPayload()
    {
        var payload = DiffPreview.ExtractDiff("edit", """{"file_path":"src/app.cs"}""");

        await Assert.That(payload.HasValue).IsFalse()
            .Because("an edit with no oldString/newString has no diff to show");
    }

    [Test]
    public async Task ExtractDiff_ValidEdit_CarriesAllThreeFields()
    {
        var payload = DiffPreview.ExtractDiff("edit", EditArgs);

        await Assert.That(payload.HasValue).IsTrue();
        await Assert.That(payload.Value.FilePath).IsEqualTo("src/app.cs");
        await Assert.That(payload.Value.Preview).Contains("- var a = 1;");
        await Assert.That(payload.Value.FullDiff).Contains("+ var a = 2;");
    }

    [Test]
    public async Task Start_AbsentArgs_KeepsTheCardDiffFree()
    {
        ToolCallSnapshot card = ToolCallSnapshot.Start("tc_0", "edit", null, null!);

        await Assert.That(card.IsDiffTool).IsFalse();
        await Assert.That(card.ArgsPreview).IsEqualTo(string.Empty);
        await Assert.That(card.DiffFilePath).IsNull();
        await Assert.That(card.DiffPreview).IsNull();
        await Assert.That(card.DiffFull).IsNull();
    }

    [Test]
    public async Task Start_FailedPayload_KeepsTheCardDiffFree()
    {
        ToolCallSnapshot card = ToolCallSnapshot.Start("tc_1", "edit", null, "{not json");

        await Assert.That(card.IsDiffTool).IsFalse();
        await Assert.That(card.DiffFilePath).IsNull();
        await Assert.That(card.DiffPreview).IsNull();
        await Assert.That(card.DiffFull).IsNull();
    }

    [Test]
    public async Task Start_SuccessfulPayload_PublishesTheDiff()
    {
        ToolCallSnapshot card = ToolCallSnapshot.Start("tc_2", "edit", "✎", EditArgs);

        await Assert.That(card.IsDiffTool).IsTrue();
        await Assert.That(card.DiffFilePath).IsEqualTo("src/app.cs");
        await Assert.That(card.DiffPreview).IsNotNull();
        await Assert.That(card.DiffFull).IsNotNull();
    }
}
