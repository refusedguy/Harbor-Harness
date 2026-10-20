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
///         Measured through the DOCUMENTED MINI-COPY of Harbor.TestKit's
///         <see cref="TestScalingGate" /> (#1652; Providers.Tests must not
///         reference TestKit — it drags Harbor.Application): GC-fenced legs
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

    /// <summary>Request builds per leg's unit (one build is sub-millisecond noise).</summary>
    private const int Iterations = 30;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{"path":"a.txt"}""");

    private static OpenAiCompatibleLlmClient CreateClient() =>
        new(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            new TestAuthResolver(),
            StubModelCatalog.Instance,
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
    private static (double Ms, long Allocated) Measure(OpenAiCompatibleLlmClient client, LlmRequest request, int units)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < units; i++)
        {
            BuildAndDispose(client, request);
        }

        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static TestScalingGateLeg AsLeg((double Ms, long Allocated) leg) => new(leg.Ms, leg.Allocated);

    [Test]
    [Timeout(120_000)]
    public async Task BuildRequest_DoubledToolCalls_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var client = CreateClient();
        var small = AssistantRequest(ToolCalls);
        var large = AssistantRequest(ToolCalls * 2);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(AsLeg(Measure(client, small, units))),
            measureLargeLeg: units => Task.FromResult(AsLeg(Measure(client, large, units))),
            initialUnits: Iterations,
            options: new TestScalingGateOptions
            {
                Label = "compat-build-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "build(2N)/build(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
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
