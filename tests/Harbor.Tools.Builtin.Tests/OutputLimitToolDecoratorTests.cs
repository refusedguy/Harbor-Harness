using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for the <see cref="ToolOutputLimit" /> seam and the
///     <see cref="OutputLimitToolDecorator" /> (#1587). Portable: the inner
///     tool is a scripted fake, so nothing here touches the filesystem, a
///     process or the network — the seam and the decorator are exercised
///     standalone, on any OS.
/// </summary>
/// <remarks>
///     Pinned:
///     <list type="bullet">
///       <item><see cref="ToolOutputLimit.Apply" /> passes a within-budget text through by reference (zero allocation);</item>
///       <item>over budget the head stays verbatim and the tail is the unified note with the dropped count;</item>
///       <item><see cref="ToolOutputLimit.TruncationNote" /> renders limit, unit and the optional hint in one shape;</item>
///       <item>the decorator forwards every <see cref="ITool" /> contract member to the inner tool;</item>
///       <item>a within-budget result travels back verbatim (same metadata, same error flag);</item>
///       <item>an over-budget result is cut, noted and logged — metadata/IsError preserved;</item>
///       <item><see cref="ITool.ValidateArguments" /> short-circuits through the decorator.</item>
///     </list>
/// </remarks>
public class OutputLimitToolDecoratorTests
{
    private const string Note = "\n\n… truncated at 200000 chars (50000 more chars dropped)";

    // ── seam: Apply ─────────────────────────────────────────────────────

    [Test]
    public async Task Apply_UnderBudget_ReturnsSameReference()
    {
        string output = "short";

        string applied = ToolOutputLimit.Apply(output, 100_000);

        await Assert.That(ReferenceEquals(applied, output)).IsTrue();
    }

    [Test]
    public async Task Apply_OverBudget_HeadVerbatim_UnifiedNote()
    {
        string output = new('a', 250_000);

        string applied = ToolOutputLimit.Apply(output, 200_000, "50000 more chars dropped");

        await Assert.That(applied).StartsWith(output[..200_000]);
        await Assert.That(applied)
            .EndsWith(Note);
        await Assert.That(applied.Length).IsEqualTo(200_000 + Note.Length);
    }

    // ── seam: TruncationNote ────────────────────────────────────────────

    [Test]
    public async Task TruncationNote_LimitAndUnit_OptionalHint()
    {
        await Assert.That(ToolOutputLimit.TruncationNote(200, "lines", "pass offset/limit to continue"))
            .IsEqualTo("\n\n… truncated at 200 lines (pass offset/limit to continue)");
        await Assert.That(ToolOutputLimit.TruncationNote(100_000, "chars"))
            .IsEqualTo("\n\n… truncated at 100000 chars");
    }

    // ── decorator: contract forwarding ─────────────────────────────────

    [Test]
    public async Task Decorator_ForwardsEveryContractMember_ToInner()
    {
        FakeLimitTool inner = new();
        OutputLimitToolDecorator decorator = new(inner, NullLogger.Instance);

        await Assert.That(decorator.Name.Value).IsEqualTo(inner.Name.Value);
        await Assert.That(decorator.DisplayName).IsEqualTo(inner.DisplayName);
        await Assert.That(decorator.Glyph).IsEqualTo(inner.Glyph);
        await Assert.That(decorator.Description).IsEqualTo(inner.Description);
        await Assert.That(decorator.ExecutionMode).IsEqualTo(inner.ExecutionMode);
        await Assert.That(decorator.SafetyProfile).IsEqualTo(inner.SafetyProfile);
        await Assert.That(decorator.PromptSnippet).IsEqualTo(inner.PromptSnippet);
        await Assert.That(decorator.PromptGuidelines).IsEqualTo(inner.PromptGuidelines);
        await Assert.That(ReferenceEquals(decorator.ParameterSchema, inner.ParameterSchema)).IsTrue();
    }

    // ── decorator: execution ───────────────────────────────────────────

    [Test]
    public async Task Decorator_UnderBudget_ResultPassesVerbatim()
    {
        ToolResult inner = ToolResult.Success("fine", new { exitCode = 0 });
        FakeLimitTool fake = new() { Result = inner };
        OutputLimitToolDecorator decorator = new(fake, NullLogger.Instance);

        ToolResult result = await decorator.ExecuteAsync(EmptyArgs(), TestToolContext.Create());

        await Assert.That(result.Output).IsEqualTo("fine");
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(ReferenceEquals(result.Metadata, inner.Metadata)).IsTrue();
        await Assert.That(fake.LastArgs.GetRawText()).IsEqualTo(EmptyArgs().GetRawText());
    }

    [Test]
    public async Task Decorator_OverBudget_CutNoted_MetadataPreserved()
    {
        ToolResult inner = ToolResult.Error(new string('e', 250_000), new { exitCode = 1 });
        FakeLimitTool fake = new() { Result = inner };
        CapturedLogger logger = new();
        OutputLimitToolDecorator decorator = new(fake, logger);

        ToolResult result = await decorator.ExecuteAsync(EmptyArgs(), TestToolContext.Create());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output.Length).IsGreaterThan(0);
        await Assert.That(result.Output.EndsWith(Note)).IsTrue();
        await Assert.That(ReferenceEquals(result.Metadata, inner.Metadata)).IsTrue();
        await Assert.That(logger.OutputLimitedFired).IsTrue();
    }

    // ── decorator: validation short-circuit ────────────────────────────

    [Test]
    public async Task Decorator_ValidateArguments_PropagatesFailure()
    {
        FakeLimitTool fake = new() { Validation = Result.Failure("bad args") };
        OutputLimitToolDecorator decorator = new(fake, NullLogger.Instance);

        Result validation = decorator.ValidateArguments(EmptyArgs());

        await Assert.That(validation.IsFailure).IsTrue();
        await Assert.That(validation.Error).IsEqualTo("bad args");
    }

    private static JsonElement EmptyArgs() =>
        JsonDocument.Parse("""{}""").RootElement;

    private sealed class FakeLimitTool : ITool
    {
        public ToolResult Result { get; set; } = ToolResult.Success(string.Empty);

        public Result Validation { get; set; } = global::CSharpFunctionalExtensions.Result.Success();

        public ToolName Name => ToolName.Create("fakelimit");

        public string DisplayName => "Fake Limit";
        public string Glyph => "🧪";
        public string Description => "Scripted inner tool for the decorator seam.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string? PromptSnippet => "fakelimit: scripted";
        public IReadOnlyList<string> PromptGuidelines { get; } = ["scripted"];

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Validation;

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken ct = default)
        {
            LastArgs = args;
            return Task.FromResult(Result);
        }

        public JsonElement LastArgs { get; private set; }
    }

    private sealed class CapturedLogger : ILogger
    {
        public bool OutputLimitedFired { get; private set; }
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            Entries.Add((logLevel, message));
            if (message.Contains("Output limited"))
                OutputLimitedFired = true;
        }
    }
}
