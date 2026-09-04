using System.Reflection;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Covers the private <c>CellForgeTuiRenderer.ProjectStateIntoWidgets</c>
/// projection: every test drives a real <see cref="UiStore"/> fold through
/// <c>renderer.RenderAsync</c> with real <see cref="AgentEvent"/>s and asserts
/// on freshly injected VMs. Fresh VMs in the ctor are required — the base
/// <c>RenderAsync</c> fan-out would otherwise apply token deltas twice
/// (absolute projection + incremental VM update). <c>InitializeAsync</c> is
/// required: the projection rides the store's <c>Changed</c> subscription.
/// </summary>
public class ProjectionCoverageTests
{
    private sealed class Harness : IDisposable
    {
        public RecordingBackend Backend { get; } = new();
        public StatusBarViewModel Status { get; } = new();
        public ChatHistoryViewModel Chat { get; } = new();
        public CellForgeTuiRenderer Renderer { get; }

        public Harness()
        {
            Renderer = new CellForgeTuiRenderer(
                NullLogger<CellForgeTuiRenderer>.Instance, Backend, Status, Chat);
        }

        public void Dispose() => Renderer.Dispose();
    }

    private static async Task<Harness> CreateAsync()
    {
        var harness = new Harness();
        var init = await harness.Renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        return harness;
    }

    private static AssistantMessage Partial(string session = "s1") =>
        AssistantMessage.Empty(session, "test-model");

    private static void BindChrome(Harness harness, string model, string provider, string agent)
    {
        var field = typeof(CellForgeTuiRenderer).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = (UiStore)field.GetValue(harness.Renderer)!;
        store.BindSession(model, provider, agent);
    }

    [Test]
    public async Task Status_Running_AfterAgentStart()
    {
        using var harness = await CreateAsync();
        await harness.Renderer.RenderAsync(new AgentStartEvent("s1", []));
        await Assert.That(harness.Status.Status).IsEqualTo("running");
    }

    [Test]
    public async Task Status_Idle_AfterAgentEnd()
    {
        using var harness = await CreateAsync();
        await harness.Renderer.RenderAsync(new AgentStartEvent("s1", []));
        await harness.Renderer.RenderAsync(new AgentEndEvent([]));
        await Assert.That(harness.Status.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task Status_Error_AfterAgentError()
    {
        using var harness = await CreateAsync();
        await harness.Renderer.RenderAsync(new AgentErrorEvent("boom"));
        await Assert.That(harness.Status.Status).IsEqualTo("error");
    }

    [Test]
    public async Task Status_Compacting_AfterCompactionStarted()
    {
        using var harness = await CreateAsync();
        await harness.Renderer.RenderAsync(new CompactionStartedEvent("s1"));
        await Assert.That(harness.Status.Status).IsEqualTo("compacting");
    }

    [Test]
    public async Task Model_Provider_Agent_Projected_Verbatim()
    {
        using var harness = await CreateAsync();
        BindChrome(harness, "m-1", "prov-9", "code");
        await Assert.That(harness.Status.Model).IsEqualTo("m-1");
        await Assert.That(harness.Status.Provider).IsEqualTo("prov-9");
        await Assert.That(harness.Status.Agent).IsEqualTo("code");
    }

    [Test]
    public async Task Tokens_And_Cost_FromStepFinish()
    {
        using var harness = await CreateAsync();
        var partial = Partial();
        await harness.Renderer.RenderAsync(new MessageStartEvent(partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", new Usage(1000, 500)), partial));
        await Assert.That(harness.Status.TokensIn).IsEqualTo(1000);
        await Assert.That(harness.Status.TokensOut).IsEqualTo(500);
        await Assert.That(harness.Status.Cost).IsEqualTo(0.0105m);
    }

    [Test]
    public async Task StreamingText_Projects_TextDeltas()
    {
        using var harness = await CreateAsync();
        var partial = Partial();
        await harness.Renderer.RenderAsync(new MessageStartEvent(partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("t1", "Hello "), partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("t1", "World"), partial));
        await Assert.That(harness.Chat.IsStreaming).IsTrue();
        await Assert.That(harness.Chat.StreamingText).IsEqualTo("Hello World");
    }

    [Test]
    public async Task ThinkingText_And_Flag_Project_ThinkBuffer()
    {
        using var harness = await CreateAsync();
        var partial = Partial();
        await harness.Renderer.RenderAsync(new MessageStartEvent(partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("h1", "quiet-plan"), partial));
        await Assert.That(harness.Chat.ThinkingText).IsEqualTo("quiet-plan");
        await Assert.That(harness.Chat.IsThinking).IsTrue();
    }

    [Test]
    public async Task MessageEnd_Resets_Streaming_Buffers()
    {
        using var harness = await CreateAsync();
        var partial = Partial();
        await harness.Renderer.RenderAsync(new MessageStartEvent(partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("t1", "draft"), partial));
        await harness.Renderer.RenderAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("h1", "musing"), partial));
        await harness.Renderer.RenderAsync(new MessageEndEvent(partial));
        await Assert.That(harness.Chat.IsStreaming).IsFalse();
        await Assert.That(harness.Chat.IsThinking).IsFalse();
        await Assert.That(harness.Chat.StreamingText).IsEqualTo(string.Empty);
        await Assert.That(harness.Chat.ThinkingText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Projection_Mirrors_UiState_FieldForField()
    {
        using var harness = await CreateAsync();
        BindChrome(harness, "m-9", "prov-9", "code");

        var partial = Partial();
        AgentEvent[] stream =
        [
            new AgentStartEvent("s1", []),
            new MessageStartEvent(partial),
            new MessageUpdateEvent(new TextDeltaEvent("t1", "mirror-me"), partial),
            new MessageUpdateEvent(new ThinkingDeltaEvent("h1", "deep-thought"), partial),
            new MessageUpdateEvent(new StepFinishEvent(0, "stop", new Usage(2000, 1000)), partial),
        ];
        foreach (var evt in stream)
        {
            await harness.Renderer.RenderAsync(evt);
        }

        var expected = new UiStore();
        expected.BindSession("m-9", "prov-9", "code");
        foreach (var evt in stream)
        {
            expected.Dispatch(evt);
        }

        var want = expected.State;
        await Assert.That(harness.Status.Status).IsEqualTo(want.Status);
        await Assert.That(harness.Status.Model).IsEqualTo(want.Model);
        await Assert.That(harness.Status.Provider).IsEqualTo(want.Provider);
        await Assert.That(harness.Status.Agent).IsEqualTo(want.AgentName);
        await Assert.That(harness.Status.TokensIn).IsEqualTo((int)want.Cost.TokensIn);
        await Assert.That(harness.Status.TokensOut).IsEqualTo((int)want.Cost.TokensOut);
        await Assert.That(harness.Status.Cost).IsEqualTo(want.Cost.CostUsd);
        await Assert.That(harness.Chat.IsStreaming).IsEqualTo(want.IsStreaming);
        await Assert.That(harness.Chat.StreamingText).IsEqualTo(want.Active.TextBuffer);
        await Assert.That(harness.Chat.ThinkingText).IsEqualTo(want.Active.ThinkBuffer);
        await Assert.That(harness.Chat.IsThinking).IsEqualTo(want.Active.ThinkBuffer.Length != 0);
    }

    [Test]
    public async Task ProdConstruction_ResolvesViewModels_ViaRegistry()
    {
        var renderer = new CellForgeTuiRenderer(NullLogger<CellForgeTuiRenderer>.Instance);
        await Assert.That(renderer.ViewModels.Get<StatusBarViewModel>("status-bar")).IsNotNull();
        await Assert.That(renderer.ViewModels.Get<ChatHistoryViewModel>("chat-history")).IsNotNull();
    }

    [Test]
    public async Task IsStreaming_Set_OnStart_Cleared_OnEnd()
    {
        using var harness = await CreateAsync();
        var partial = Partial();
        await harness.Renderer.RenderAsync(new MessageStartEvent(partial));
        await Assert.That(harness.Chat.IsStreaming).IsTrue();
        await Assert.That(harness.Chat.StreamingText).IsEqualTo(string.Empty);
        await harness.Renderer.RenderAsync(new MessageEndEvent(partial));
        await Assert.That(harness.Chat.IsStreaming).IsFalse();
    }

    [Test]
    public async Task Status_Running_AfterCompactionCompleted()
    {
        using var harness = await CreateAsync();
        await harness.Renderer.RenderAsync(new CompactionStartedEvent("s1"));
        await harness.Renderer.RenderAsync(new CompactionCompletedEvent(
            "s1", "summary", PrunedMessageCount: 5, TokensSaved: 100, TimeSpan.FromSeconds(1)));
        await Assert.That(harness.Status.Status).IsEqualTo("running");
    }
}
