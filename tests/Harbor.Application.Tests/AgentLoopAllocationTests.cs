using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
/// Allocation-budget coverage for the agent-turn hot path (#186).
/// The internal <c>StreamingCoalescer</c> (incl. <c>TryParseArgs</c>) is
/// exercised through the public <c>AgentLoop</c> seam with a
/// <c>ScriptedLlmClient</c> — no <c>InternalsVisibleTo</c> (same approach as
/// <c>StreamingCoalescerPoolTests</c>). Each measured run uses a pre-built
/// loop + session so per-turn cost is isolated (no cross-run history growth
/// inside measurement). Bounds are generous, CI-safe tripwires in the
/// <c>SpanParserTests</c> tradition: per-turn averages are reported for
/// BENCHMARKS.md, hard failures only on pathological growth.
/// </summary>
/// <remarks>
/// <para>
///     <b>#741 — the measurement, and the direction it is compared in.</b>
///     This class used to sample <c>GC.GetAllocatedBytesForCurrentThread()</c>
///     on both sides of <c>await loop.RunAsync(...)</c>. That counter is
///     per-thread, and the continuation resumes on whatever thread the scheduler
///     picks, whose counter started near zero — so the two samples came from
///     different counters and the delta was the gap between them:
///     <c>text-only turn avg = -815859 B</c>.
/// </para>
/// <para>
///     It did not fail, because the tripwire compared the result from <b>above</b>
///     only (<c>best &lt;= ceiling</c>), and every negative number satisfies every
///     ceiling. The gate was therefore off while rendering green. Two changes
///     follow from that, and both are load-bearing:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///             The window is measured with
///             <see cref="AllocationProbe.MeasureProcessAsync" /> — one
///             monotonic counter, immune to a hop. This is the methodology the
///             whole repository now shares (<c>JsonlUnboundedAllocationTests</c>
///             reached the same conclusion independently, via #661).
///         </description>
///     </item>
///     <item>
///         <description>
///             The budget is bounded from <b>below</b> as well as above, so a
///             measurement that could not be taken fails loudly instead of
///             sailing under a ceiling. That is the half that was missing, and it
///             is what stops a negative from ever passing again.
///         </description>
///     </item>
/// </list>
/// <para>
///     <see cref="NotInParallelAttribute" /> is keyless and that is part of the
///     measurement, not a concern about noise: a process-wide counter bills every
///     allocation in the process for the duration of the window, and TUnit runs
///     classes in parallel by default. A named key such as
///     <c>[NotInParallel("alloc-tripwire")]</c> would serialise only against
///     classes carrying that same key, and this assembly holds classes that carry
///     no attribute at all. Min-of-3 rounds keeps the signal against a residual
///     GC/JIT wobble.
/// </para>
/// </remarks>
[NotInParallel]
public class AgentLoopAllocationTests
{
    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-alloc-loop-tests", "code", "test", "test-model"),
        []);

    private static AgentLoop NewTextOnlyLoop() => TestLoops.Create(
        new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "hi"),
                new StepFinishEvent(0, "stop", new Usage(1, 1)),
            },
        ]),
        new FakeToolRegistry());

    private static AgentLoop NewToolCallLoop() => TestLoops.Create(
        new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new ToolCallStartEvent("call-1", "counter"),
                new ToolCallDeltaEvent("call-1", """{"n":7}"""),
                new StepFinishEvent(0, "stop", new Usage(4, 2)),
            },
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "finished"),
                new StepFinishEvent(1, "stop", new Usage(1, 1)),
            },
        ]),
        new FakeToolRegistry(new CountingTool()));

    /// <summary>
    ///     Runs every pre-built loop once and returns the total allocation of the
    ///     least noisy of three rounds, in bytes.
    /// </summary>
    /// <remarks>
    ///     Min-of-3 because a single GC or JIT wobble must not fail the gate, and
    ///     process-wide because the body awaits (see the class remarks for why
    ///     that distinction was the whole bug).
    /// </remarks>
    private static async Task<long> MinOf3Async(AgentLoop[] loops, TestSessionContext[] sessions, AgentDefinition agent)
    {
        const int rounds = 3;
        long best = long.MaxValue;

        for (int round = 0; round < rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocated = await AllocationProbe.MeasureProcessAsync(async () =>
            {
                for (int i = 0; i < loops.Length; i++)
                {
                    _ = await loops[i].RunAsync(sessions[i], agent);
                }
            });

            if (allocated < best)
            {
                best = allocated;
            }
        }

        return best;
    }

    [Test]
    public async Task TextOnly_Turn_StaysBounded()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies 4x across OS runtimes.
        var warm = await NewTextOnlyLoop().RunAsync(NewSession(), TestAgents.AllowAll());
        await Assert.That(warm.IsSuccess).IsTrue();

        const int turns = 10;
        var loops = new AgentLoop[turns];
        var sessions = new TestSessionContext[turns];
        for (int i = 0; i < turns; i++)
        {
            loops[i] = NewTextOnlyLoop();
            sessions[i] = NewSession();
        }

        var agent = TestAgents.AllowAll();
        long best = await MinOf3Async(loops, sessions, agent);

        Console.WriteLine(
            $"agentloop-alloc: text-only turn avg = {(double)best / turns:F0} B over {turns} turns (min of 3)");

        // LOWER BOUND, first. #741's missing half: a measurement that could not
        // be taken is not a small turn. The probe already refuses to return a
        // backwards delta, so this is the belt to that braces — and it is the
        // assertion whose absence is what let -815859 B pass as green.
        await Assert.That(best).IsGreaterThanOrEqualTo(0)
            .Because(
                "a negative measurement means the two samples did not come from the same counter, so " +
                "the number describes nothing. It is not a cheap turn, and a ceiling-only check would " +
                "have accepted it (#741)");

        // Tripwire, not a pin: process-wide accounting varies across OS runtimes,
        // and the budgets were observed on Linux CI. Catches 2x local blowups.
        await Assert.That(best).IsLessThanOrEqualTo(turns * 512L * 1_024L)
            .Because(
                "ceiling only. It is meaningful now only because the floor above is asserted too: a " +
                "ceiling on its own is satisfied by every negative number");
    }

    [Test]
    public async Task ToolCall_Turn_StaysBounded()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).
        // Exercises StreamingCoalescer start/delta/materialize (TryParseArgs)
        // plus permission check, tool dispatch and result append.
        var warm = await NewToolCallLoop().RunAsync(NewSession(), TestAgents.AllowAll());
        await Assert.That(warm.IsSuccess).IsTrue();

        const int runs = 5;
        var loops = new AgentLoop[runs];
        var sessions = new TestSessionContext[runs];
        for (int i = 0; i < runs; i++)
        {
            loops[i] = NewToolCallLoop();
            sessions[i] = NewSession();
        }

        var agent = TestAgents.AllowAll();
        long best = await MinOf3Async(loops, sessions, agent);

        Console.WriteLine($"agentloop-alloc: tool-call turn avg = {(double)best / runs:F0} B over {runs} runs (min of 3)");

        // The floor again, for the same reason as above — see TextOnly.
        await Assert.That(best).IsGreaterThanOrEqualTo(0)
            .Because(
                "a negative measurement describes nothing and must not pass a budget (#741)");

        // Tripwire: observed max ~1.67MB/turn (macOS) vs ~437KB (linux).
        await Assert.That(best).IsLessThanOrEqualTo(runs * 2560L * 1_024L)
            .Because("ceiling only; the floor above is what makes it a gate rather than a formality");
    }
}
