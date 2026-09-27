namespace Harbor.Tui.RendererTests;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.RendererTests.Support;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     UiStore write-path regression for the unified AnsiPlain renderer
///     (issue #77, PR #146): every <see cref="AnsiPlainTuiRenderer.RenderAsync"/>
///     folds the event into the injected <see cref="UiStore"/> so the pipeline
///     can restore the snapshot across renderer swaps. These tests pin the
///     behavior the perf-gate memory ceiling pays for — if the dispatch is ever
///     removed, skipped, or reordered, they fail loudly instead of silently
///     dropping cross-swap restore.
/// </summary>
public class AnsiPlainUiStoreTests
{
    [Test]
    public async Task Render_StreamsTranscriptIntoSharedStore()
    {
        var store = new UiStore();
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, store);
        try
        {
            await renderer.InitializeAsync();
            var partial = AssistantMessage.Empty("s1", "stub-1");
            await renderer.RenderAsync(new MessageStartEvent(partial));
            await renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial));
            await renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", ", "), partial));
            await renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "world!"), partial));
            await renderer.RenderAsync(new MessageEndEvent(partial));
        }
        finally
        {
            renderer.Dispose();
        }

        // Dispatch happened: five folds bumped the monotonic revision.
        await Assert.That(store.State.Revision).IsEqualTo(5);
        // State converged: the deltas folded into one assistant transcript line.
        await Assert.That(store.State.Lines.Length).IsEqualTo(1);
        await Assert.That(store.State.Lines[0].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(store.State.Lines[0].Text).IsEqualTo("Hello, world!");
    }

    [Test]
    public async Task Render_ToolRoundTrip_LandsInSharedStore()
    {
        var store = new UiStore();
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, store);
        try
        {
            await renderer.InitializeAsync();
            var args = JsonSerializer.SerializeToElement(new { path = "README.md", limit = 10 });
            await renderer.RenderAsync(new ToolExecutionStartEvent("tc_1", "read", args));
            await renderer.RenderAsync(new ToolExecutionEndEvent("tc_1", ToolResult.Success("[0001] # Harbor"), IsError: false));
        }
        finally
        {
            renderer.Dispose();
        }

        await Assert.That(store.State.Revision).IsEqualTo(2);
        await Assert.That(store.State.Lines.Length).IsEqualTo(2);
        await Assert.That(store.State.Lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(store.State.Lines[0].ToolCallId).IsEqualTo("tc_1");
        await Assert.That(store.State.Lines[0].Text).Contains("read");
        await Assert.That(store.State.Lines[1].Role).IsEqualTo(ChatRole.ToolResult);
        await Assert.That(store.State.Lines[1].ToolCallId).IsEqualTo("tc_1");
        await Assert.That(store.State.Lines[1].Text).Contains("[0001] # Harbor");
    }

    [Test]
    public async Task Render_AnsiAndPlain_ConvergeToSameStoreState()
    {
        var ansiStore = new UiStore();
        var plainStore = new UiStore();
        using var ansiWriter = new StringWriter();
        using var plainWriter = new StringWriter();
        var ansiRenderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance, ansiWriter, ansiStore);
        var plainRenderer = new PlainTuiRenderer(plainWriter, plainStore);
        try
        {
            await ansiRenderer.InitializeAsync();
            await plainRenderer.InitializeAsync();
            foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
            {
                await ansiRenderer.RenderAsync(evt);
                await plainRenderer.RenderAsync(evt);
            }
        }
        finally
        {
            ansiRenderer.Dispose();
            plainRenderer.Dispose();
        }

        await Assert.That(plainStore.State.Revision).IsEqualTo(ansiStore.State.Revision);
        await Assert.That(plainStore.State.Lines.Length).IsEqualTo(ansiStore.State.Lines.Length);
        for (int i = 0; i < ansiStore.State.Lines.Length; i++)
        {
            await Assert.That(plainStore.State.Lines[i].Role).IsEqualTo(ansiStore.State.Lines[i].Role);
            await Assert.That(plainStore.State.Lines[i].Text).IsEqualTo(ansiStore.State.Lines[i].Text);
        }
    }
}
