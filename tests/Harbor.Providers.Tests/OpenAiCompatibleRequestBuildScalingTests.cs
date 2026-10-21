using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #1100 (one slice): <c>OpenAiCompatibleLlmClient.WriteMessage</c>
///     ran two <c>OfType</c> iterator chains (+ a <c>Select</c>) over every
///     assistant message on the request-build path. The serializer now uses
///     two allocation-free indexed <c>for</c> loops over the same block list.
///     <para>
///         The gate is relative, not absolute: building a request with twice
///         the tool calls must cost at most 3x the time and allocations of
///         the single-size build, measured in the same run
///         (<c>t(2N) &lt;= 3*t(N)</c>). A quadratic regression lands at 4x
///         and cannot hide under it; runner speed cancels in the ratio.
///         Measured through the SHARED Harbor.TestKit's
///         <see cref="TestScalingGate" /> (#1652; since #1889 this project
///         references the shared file instead of keeping a local copy — the
///         TestKit core no longer drags Harbor.Application into the slice
///         this project measures): GC-fenced legs
///         scaled toward ~100 ms of real work, medians over five rounds, and
///         a x1.05 noise band on the time branch only — the #1643
///         3.27-vs-3.0 flake came out of a 7.9 ms best-of-5 leg.
///         Linux-only (GC accounting varies by OS).
///     </para>
/// </summary>
[NotInParallel("compat-scale")]
public class OpenAiCompatibleRequestBuildScalingTests
{
    /// <summary>Tool calls in the small request; the large one doubles it.</summary>
    private const int ToolCalls = 200;

    /// <summary>
    ///     Tool calls in the request the gate MEASURES at - twice
    ///     <see cref="ToolCalls" />, because a per-build cost that grows with
    ///     the tool-call count must land in the measurement instead of hiding
    ///     behind the smaller request.
    /// </summary>
    private const int MeasuredToolCalls = ToolCalls * 2;

    /// <summary>
    ///     Request builds per measured leg - the FIXED amount of work. 500
    ///     builds is about 90 ms of real work on the CI runner.
    /// </summary>
    private const int BuildsPerLeg = 500;

    /// <summary>
    ///     Allocation budget for ONE request build over the 400-tool-call
    ///     request, in bytes. Machine-independent, so it is compared as
    ///     measured, with no band. Pinned from the CI measurement of the ratio
    ///     gate this replaced (run 39423151864): 230 291 408 B over 1 013
    ///     builds = 227 288 B per build. The budget leaves ~1.41x headroom.
    /// </summary>
    private const long MaxBytesPerBuild = 320_000;

    /// <summary>
    ///     Time budget for ONE request build, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 182.752 ms over 1 013 builds = 180 us per
    ///     build against a probe that costs 0.000380 ms/unit, i.e. ~453 probe
    ///     units per build. The budget leaves ~15x headroom, because the
    ///     probe's OWN measured cost moved 5.3x between two CI runs of the
    ///     same tree (0.000380 ms/unit in run 39423151864, 0.000072 ms/unit in
    ///     run 39436405496) - the BYTE budget is the branch that can fail on
    ///     any runner.
    /// </summary>
    private const double MaxProbeUnitsPerBuild = 6_800.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{"path":"a.txt"}""");

    private static OpenAiCompatibleLlmClient CreateClient() =>
        new(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new TestAuthResolver(),
            FakeModelCatalog.Instance,
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

    private static LlmRequest AssistantRequest(int toolCalls) =>
        new(
            Model: "kilocode/kilo-auto/free",
            Messages: [AssistantMessage(toolCalls)],
            SystemPrompt: "sys",
            Tools: []);

    private static LlmAssistantMessage AssistantMessage(int toolCalls)
    {
        var blocks = new LlmContentBlock[toolCalls + 1];
        blocks[0] = new LlmTextBlock("hello");
        JsonElement args = ArgsDoc.RootElement;
        for (int i = 0; i < toolCalls; i++)
        {
            blocks[i + 1] = new LlmToolCallBlock($"tc_{i}", "read", args);
        }

        return new LlmAssistantMessage(blocks);
    }

    private static void BuildAndDispose(OpenAiCompatibleLlmClient client, LlmRequest request)
    {
        using var msg = client.BuildRequest(request, "test-key");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> builds of the same
    ///     request, timed and allocation-counter'd as one window.
    /// </summary>
    private static PerfGateLeg Measure(OpenAiCompatibleLlmClient client, LlmRequest request, int units)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < units; i++)
        {
            BuildAndDispose(client, request);
        }

        sw.Stop();
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>build(2N)/build(N) &lt;= 3</c> for time and allocations
    ///     over 200 against 400 tool calls, decided by the shared
    ///     TestScalingGate (#1652). It is now:
    ///     <code>
    ///     bytes per build   &lt;= 320 000    (machine-independent, HARD)
    ///     probe units/build &lt;= 6 800      (machine-scaled time, SOFT)
    ///     </code>
    ///     measured over a FIXED amount of work (500 builds of the 400-tool-call
    ///     request - the LARGER one, so a per-build cost that grows with the
    ///     tool-call count lands in the measurement) on the SAME runner in the
    ///     SAME run. The allocation branch is machine-independent and is
    ///     asserted hard; the time branch is in PROBE UNITS and stands down
    ///     when the rounds do not reproduce each other. The report prints on
    ///     every run, pass or fail.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task BuildRequest_CostPerBuild_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var client = CreateClient();
        var large = AssistantRequest(MeasuredToolCalls);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: builds => Task.FromResult(Measure(client, large, builds)),
            units: BuildsPerLeg,
            options: new PerfGateOptions
            {
                Label = "compat-build-cost",
                MaxBytesPerUnit = MaxBytesPerBuild,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerBuild,
            });

        await result.AssertWithinBudgetsAsync("openai-compatible request build per build");
    }

    [Test]
    public async Task BuildRequest_AssistantTurn_KeepsFirstTextAndAllToolCalls()
    {
        var client = CreateClient();
        using var msg = client.BuildRequest(AssistantRequest(3), "test-key");
        string body = await msg.Content!.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        JsonElement assistant = doc.RootElement.GetProperty("messages")[1];
        await Assert.That(assistant.GetProperty("role").GetString()).IsEqualTo("assistant");
        await Assert.That(assistant.GetProperty("content").GetString()).IsEqualTo("hello");

        JsonElement[] calls = [.. assistant.GetProperty("tool_calls").EnumerateArray()];
        await Assert.That(calls.Length).IsEqualTo(3);
        await Assert.That(calls[0].GetProperty("id").GetString()).IsEqualTo("tc_0");
        await Assert.That(calls[0].GetProperty("function").GetProperty("name").GetString()).IsEqualTo("read");
    }
}
