using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
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
///         Best-of-5 rounds, linux-only (GC accounting varies by OS).
///     </para>
/// </summary>
[NotInParallel("compat-scale")]
public class OpenAiCompatibleRequestBuildScalingTests
{
    /// <summary>Tool calls in the small request; the large one doubles it.</summary>
    private const int ToolCalls = 200;

    /// <summary>Request builds per measured leg (one build is sub-millisecond noise).</summary>
    private const int Iterations = 30;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    /// <summary>Rounds per leg, best-of (a descheduled or collected leg only inflates).</summary>
    private const int Rounds = 5;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{"path":"a.txt"}""");

    private static OpenAiCompatibleLlmClient CreateClient() =>
        new(
            new HttpClient(),
            new ProviderConfig { Id = "stub", BaseUrl = "http://stub" },
            StubAuthResolver.Instance,
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

    private static (double Ms, long Allocated) Measure(OpenAiCompatibleLlmClient client, LlmRequest request)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
        {
            BuildAndDispose(client, request);
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(120_000)]
    public async Task BuildRequest_DoubledToolCalls_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var client = CreateClient();
        var small = AssistantRequest(ToolCalls);
        var large = AssistantRequest(ToolCalls * 2);

        // Warm up (tiered JIT over the serializer) before any measurement.
        BuildAndDispose(client, small);
        BuildAndDispose(client, large);

        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            (double tSmall, long aSmall) = Measure(client, small);
            (double tLarge, long aLarge) = Measure(client, large);
            msSmall = Math.Min(msSmall, tSmall);
            msLarge = Math.Min(msLarge, tLarge);
            allocSmall = Math.Min(allocSmall, aSmall);
            allocLarge = Math.Min(allocLarge, aLarge);
        }

        double timeRatio = msLarge / msSmall;
        double allocRatio = (double)allocLarge / allocSmall;

        // Printed, not only asserted, so the deciding ratio is readable next
        // to the test instead of existing only in a failure message. The
        // absolute columns are informational and runner-dependent.
        Console.WriteLine(
            $"compat-build-scaling: {ToolCalls} calls x{Iterations} = {msSmall:F3} ms / {allocSmall} B, "
            + $"{ToolCalls * 2} calls x{Iterations} = {msLarge:F3} ms / {allocLarge} B "
            + $"(time x{timeRatio:F2}, alloc x{allocRatio:F2}, limit x{GrowthLimit:F2}, best of {Rounds}; "
            + "the absolute columns are informational and runner-dependent)");

        // Non-vacuity: both ratios divide by the small leg, so a zero there
        // means "measured nothing" — the #591 shape.
        await Assert.That(msSmall).IsGreaterThan(0.0)
            .Because($"the small-leg baseline must be a real measurement, got {msSmall:F3} ms.");
        await Assert.That(allocSmall).IsGreaterThan(0L)
            .Because($"the small-leg allocation baseline must be a real measurement, got {allocSmall} B.");

        await Assert.That(timeRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"build(2N)/build(N) = {timeRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{msLarge:F3} ms for {ToolCalls * 2} tool calls versus {msSmall:F3} ms for {ToolCalls}. "
                + "Linear is 2.00, so this bound separates linear from quadratic without depending on "
                + "how fast this runner is.");
        await Assert.That(allocRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"alloc(2N)/alloc(N) = {allocRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{allocLarge} B versus {allocSmall} B. "
                + "Allocations do not move with the runner's speed at all, so this ratio holds on every machine.");
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
