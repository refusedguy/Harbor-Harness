using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;

using FakeAgentLoopBase = Harbor.TestKit.Generated.FakeAgentLoop; // #1769: generated double

namespace Harbor.Application.Tests.Fakes;

/// <summary>
///     The agent-loop double (issue #1769): the generated base carries the
///     interface, this subclass only carries the counter the tests read.
/// </summary>
/// <remarks>
///     <para>
///         This one stays in the project. It is not a copy of the kit's
///         generated <c>FakeAgentLoop</c> — it EXTENDS it with the one thing
///         the Application suite needs and the kit does not provide: a count
///         of how many times the loop was run. A double that a single project
///         needs stays with that project (#1825).
///     </para>
/// </remarks>
public sealed class FakeAgentLoop : FakeAgentLoopBase
{
    private int _runs;

    public FakeAgentLoop(Result? runOutcome = null) => RunAsyncResult = runOutcome ?? Result.Success();

    public int Runs => Volatile.Read(ref _runs);

    public override Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _runs);
        return Task.FromResult(RunAsyncResult);
    }
}
