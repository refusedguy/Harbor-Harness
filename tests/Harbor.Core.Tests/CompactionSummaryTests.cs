using System.Text.Json;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     goose-features #59: the compaction LLM's answer must be parsed into the
///     ten-field structured summary leniently, and a response that is not a
///     usable JSON object must keep its raw text verbatim.
/// </summary>
public sealed class CompactionSummaryTests
{
    [Test]
    public async Task Parse_FullStructuredSummary_PopulatesAllFields()
    {
        const string response = """
            {
              "user_intent": ["ship the feature", "keep tests green"],
              "technical_concepts": ["AOT", "source generation"],
              "files": [{"path": "src/Foo.cs", "summary": "rewrote the reader", "key_code": "TryParse(...)"}],
              "errors_and_fixes": ["CS1234 -> added using"],
              "problem_solving": ["split the codec"],
              "user_messages": ["please do it"],
              "pending_tasks": ["run CI"],
              "current_work": "waiting on review",
              "next_step": "merge"
            }
            """;

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsTrue();
        await Assert.That(summary.IsEmpty).IsFalse();
        await Assert.That(summary.UserIntent.Count).IsEqualTo(2);
        await Assert.That(summary.UserIntent[0]).IsEqualTo("ship the feature");
        await Assert.That(summary.TechnicalConcepts.Count).IsEqualTo(2);
        await Assert.That(summary.Files.Count).IsEqualTo(1);
        await Assert.That(summary.Files[0].Path).IsEqualTo("src/Foo.cs");
        await Assert.That(summary.Files[0].Summary).IsEqualTo("rewrote the reader");
        await Assert.That(summary.Files[0].KeyCode).IsEqualTo("TryParse(...)");
        await Assert.That(summary.ErrorsAndFixes.Count).IsEqualTo(1);
        await Assert.That(summary.ProblemSolving.Count).IsEqualTo(1);
        await Assert.That(summary.UserMessages.Count).IsEqualTo(1);
        await Assert.That(summary.PendingTasks.Count).IsEqualTo(1);
        await Assert.That(summary.CurrentWork).IsEqualTo("waiting on review");
        await Assert.That(summary.NextStep).IsEqualTo("merge");
    }

    [Test]
    public async Task Parse_EnrichedSchema_StringifiesObjectsAndKeepsUnknownFields()
    {
        // A model that enriches the schema must not lose the whole summary:
        // objects/arrays where a string was asked for are stringified and the
        // unknown top-level field is kept in Extra.
        const string response = """
            {
              "user_intent": {"goal": "ship", "why": "deadline"},
              "errors_and_fixes": [{"error": "boom", "fix": "retry"}],
              "current_work": ["step one", "step two"],
              "next_step": 42,
              "brand_new_field": {"kept": true}
            }
            """;

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsTrue();
        await Assert.That(summary.UserIntent.Count).IsEqualTo(1);
        await Assert.That(summary.UserIntent[0]).Contains("goal: ship");
        await Assert.That(summary.UserIntent[0]).Contains("why: deadline");
        await Assert.That(summary.ErrorsAndFixes[0]).Contains("error: boom");
        await Assert.That(summary.CurrentWork).IsEqualTo("step one; step two");
        await Assert.That(summary.NextStep).IsEqualTo("42");
        await Assert.That(summary.Extra.ContainsKey("brand_new_field")).IsTrue();
        await Assert.That(summary.Extra["brand_new_field"].GetProperty("kept").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Parse_FilesAsPlainStrings_BecomesPathOnlyActivities()
    {
        const string response = """
            {"files": ["src/A.cs", "src/B.cs"], "next_step": "continue"}
            """;

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.Files.Count).IsEqualTo(2);
        await Assert.That(summary.Files[0].Path).IsEqualTo("src/A.cs");
        await Assert.That(summary.Files[0].Summary).IsEqualTo(string.Empty);
        await Assert.That(summary.Files[0].KeyCode).IsNull();
    }

    [Test]
    public async Task Parse_BlankFieldsOnly_FallsBackToRawText()
    {
        const string response = """
            {"user_intent": ["  ", null], "files": [{"path": ""}], "current_work": "   "}
            """;

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsFalse();
        await Assert.That(summary.IsEmpty).IsTrue();
        await Assert.That(summary.RawText).IsEqualTo(response);
        await Assert.That(summary.Render()).IsEqualTo(response);
    }

    [Test]
    public async Task Parse_FencedJsonBlock_UsesTheItem60Extractor()
    {
        const string response = "Here is the summary:\n```json\n{\"user_intent\": [\"ship it\"]}\n```\nDone.";

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsTrue();
        await Assert.That(summary.UserIntent[0]).IsEqualTo("ship it");
    }

    [Test]
    public async Task Parse_AfterAnalysisTerminator_PicksTheRealAnswer()
    {
        const string response = "<analysis>thinking</analysis>\n{\"next_step\": \"merge now\"}";

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsTrue();
        await Assert.That(summary.NextStep).IsEqualTo("merge now");
    }

    [Test]
    public async Task Parse_TruncatedJson_KeepsRawTextUnchanged()
    {
        // No repair: inventing closing braces would drop the continuation-critical
        // tail that the raw-text fallback still preserves.
        const string response = "```json\n{\"next_step\": \"keep\", \"files\": [";

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsFalse();
        await Assert.That(summary.RawText).IsEqualTo(response);
        await Assert.That(summary.Render()).IsEqualTo(response);
    }

    [Test]
    public async Task Parse_PlainProse_FallsBackToRawText()
    {
        const string response = "The work is mostly done, just finish the tests and merge.";

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsFalse();
        await Assert.That(summary.Render()).IsEqualTo(response);
    }

    [Test]
    public async Task Parse_CamelCaseFields_AreAccepted()
    {
        const string response = "{\"userIntent\": [\"ship\"], \"nextStep\": \"merge\"}";

        CompactionSummary summary = CompactionSummary.Parse(response);

        await Assert.That(summary.IsStructured).IsTrue();
        await Assert.That(summary.UserIntent[0]).IsEqualTo("ship");
        await Assert.That(summary.NextStep).IsEqualTo("merge");
    }

    [Test]
    public async Task Render_Structured_SectionsInGooseOrder()
    {
        const string response = """
            {"user_intent": ["ship"], "files": [{"path": "a.cs", "summary": "edited"}], "next_step": "merge"}
            """;

        string rendered = CompactionSummary.Parse(response).Render();

        int intent = rendered.IndexOf("## User intent", StringComparison.Ordinal);
        int files = rendered.IndexOf("## Files", StringComparison.Ordinal);
        int next = rendered.IndexOf("## Next step", StringComparison.Ordinal);
        await Assert.That(intent).IsGreaterThan(-1);
        await Assert.That(files).IsGreaterThan(intent);
        await Assert.That(next).IsGreaterThan(files);
        await Assert.That(rendered).Contains("`a.cs`");
    }

    [Test]
    public async Task ToJson_UsesSourceGeneratedContext()
    {
        CompactionSummary summary = CompactionSummary.Parse("{\"user_intent\": [\"ship\"]}");

        string json = summary.ToJson();

        using JsonDocument doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("userIntent")[0].GetString()).IsEqualTo("ship");
        await Assert.That(doc.RootElement.GetProperty("isStructured").GetBoolean()).IsTrue();
    }
}
