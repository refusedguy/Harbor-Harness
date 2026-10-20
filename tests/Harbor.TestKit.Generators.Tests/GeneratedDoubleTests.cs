using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.TestKit.Generated;

namespace Harbor.TestKit.Generators.Tests;

/// <summary>
///     The half of the guard that runs against the doubles the generator
///     ACTUALLY emitted into <c>Harbor.TestKit</c> (issue #1769).
/// </summary>
/// <remarks>
///     <para>
///         If the generator did not run, or emitted something that does not
///         implement its interface, this file does not compile — the test
///         project would fail at the BUILD, not at a green assertion. That is
///         the point: the guard cannot pass against a generator that produced
///         nothing.
///     </para>
/// </remarks>
public class GeneratedDoubleTests
{
    [Test]
    public async Task GeneratedTool_ImplementsTheInterface_AndAnswersTheConfiguredValue()
    {
        var tool = new FakeTool
        {
            Name = ToolName.Create("generated_tool"),
            DisplayName = "Generated Tool",
            Description = "A double emitted by the generator.",
            ExecuteAsyncValue = ToolResult.Success("ok"),
        };

        ITool asInterface = tool;
        await Assert.That(asInterface.Name.Value).IsEqualTo("generated_tool");
        await Assert.That(asInterface.DisplayName).IsEqualTo("Generated Tool");

        ToolResult result = await asInterface.ExecuteAsync(JsonDocument.Parse("{}").RootElement, Context(), default);
        await Assert.That(result.Output).IsEqualTo("ok");

        // The recording surface: the call is in the log, with its arguments.
        await Assert.That(tool.RecordedCalls.Count).IsEqualTo(1);
        await Assert.That(tool.RecordedCalls[0].Member).IsEqualTo("ExecuteAsync");
    }

    [Test]
    public async Task GeneratedSessionStore_FailsLoudly_UntilTheMemberIsConfigured()
    {
        var store = new FakeSessionStore();

        // The default of a Result-returning member is a FAILURE, not a silent
        // success carrying a null value: `default(Result<T>)` would look like
        // success and hand the caller a null.
        Result<Session> unconfigured = await store.CreateAsync("/tmp", "code", "test", "model");
        await Assert.That(unconfigured.IsFailure).IsTrue();

        Session session = Session.Create("/tmp", "code", "test", "model");
        store.CreateAsyncResult = Result.Success(session);

        Result<Session> configured = await store.CreateAsync("/tmp", "code", "test", "model");
        await Assert.That(configured.IsSuccess).IsTrue();
        await Assert.That(configured.Value.Id).IsEqualTo(session.Id);
    }

    private static ToolContext Context() => new(
        "s1",
        "m1",
        "c1",
        "code",
        CancellationToken.None,
        [],
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));
}
