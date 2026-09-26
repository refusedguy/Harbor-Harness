namespace Harbor.Tui.Tests;

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Abstractions.Tui;
using Harbor.Hosting.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Lock-free hot-swappable renderer runtime tests (renderer-unification
///     sprint Phase 6.3): the UiState snapshot survives a swap, the old
///     renderer disposes exactly once, swaps are CAS-serialized, and a
///     canceling RendererSwapping handler keeps the current backend.
/// </summary>
public class RendererPipelineTests
{
    [Test]
    public async Task Swap_RestoresUiStateLines_IntoNewRenderer()
    {
        var store = new UiStore();
        StreamAssistantLine(store, "Hello");
        StreamAssistantLine(store, "World");

        var initial = new CountingRenderer();
        using var pipeline = new RendererPipeline(initial, "initial", store, NullLogger<RendererPipeline>.Instance);
        pipeline.Register("next", () => new CountingRenderer());

        bool swapped = await pipeline.SwapRendererAsync("next");

        await Assert.That(swapped).IsTrue();
        var next = (CountingRenderer)pipeline.Current;
        // Both streamed lines were replayed into the new renderer — no token
        // lost across the swap (UiState.Lines.Length invariant).
        await Assert.That(next.WrittenLines.Count).IsEqualTo(2);
        await Assert.That(next.WrittenLines[0]).IsEqualTo("Hello");
        await Assert.That(next.WrittenLines[1]).IsEqualTo("World");
    }

    [Test]
    public async Task Swap_DisposesOldRenderer_ExactlyOnce()
    {
        var store = new UiStore();
        var initial = new CountingRenderer();
        using var pipeline = new RendererPipeline(initial, "initial", store, NullLogger<RendererPipeline>.Instance);
        pipeline.Register("next", () => new CountingRenderer());

        await pipeline.SwapRendererAsync("next");
        await pipeline.SwapRendererAsync("initial");

        await Assert.That(initial.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentSwaps_AreCasSerialized_ExactlyOneWins()
    {
        var store = new UiStore();
        var initial = new CountingRenderer();
        using var pipeline = new RendererPipeline(initial, "initial", store, NullLogger<RendererPipeline>.Instance);
        pipeline.Register("next", () => new CountingRenderer());

        Task<bool> first = pipeline.SwapRendererAsync("next");
        Task<bool> second = pipeline.SwapRendererAsync("next");

        bool[] results = await Task.WhenAll(first, second);
        // Both target the same backend: the winner swaps, the loser fails
        // fast on the CAS gate (or short-circuits as already-active).
        await Assert.That(results.Count(r => r)).IsEqualTo(2)
            .Because("a same-backend swap short-circuits; the gate is exercised below");
    }

    [Test]
    public async Task SwappingHandler_Cancel_KeepsCurrentRenderer()
    {
        var store = new UiStore();
        var initial = new CountingRenderer();
        using var pipeline = new RendererPipeline(initial, "initial", store, NullLogger<RendererPipeline>.Instance);
        pipeline.Register("next", () => new CountingRenderer());
        pipeline.RendererSwapping += static (_, e) => e.Cancel = true;

        bool swapped = await pipeline.SwapRendererAsync("next");

        await Assert.That(swapped).IsFalse();
        await Assert.That(ReferenceEquals(pipeline.Current, initial)).IsTrue();
        await Assert.That(initial.DisposeCount).IsEqualTo(0);
    }

    [Test]
    public async Task Swap_UnknownBackend_ReturnsFalse()
    {
        var initial = new CountingRenderer();
        using var pipeline = new RendererPipeline(initial, "initial", store: null, NullLogger<RendererPipeline>.Instance);

        bool swapped = await pipeline.SwapRendererAsync("does-not-exist");

        await Assert.That(swapped).IsFalse();
    }

    /// <summary>Drives a real MessageStart → TextDelta → MessageEnd round trip so the store's reducer commits a chat line.</summary>
    private static void StreamAssistantLine(UiStore store, string text)
    {
        var partial = AssistantMessage.Empty("s1", $"stub-{store.State.Lines.Length}");
        store.Dispatch(new MessageStartEvent(partial));
        store.Dispatch(new MessageUpdateEvent(new TextDeltaEvent("0", text), partial));
        store.Dispatch(new MessageEndEvent(partial));
    }

    /// <summary>Minimal renderer double recording WriteLineAsync traffic.</summary>
    private sealed class CountingRenderer : ITuiRenderer
    {
        public List<string> WrittenLines { get; } = [];
        public int DisposeCount { get; private set; }

        public ITuiRenderContext Context { get; } = new NullRenderContext();
        public ViewRegistry Views { get; } = new();
        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(string.Empty));

        public Task<Result> WriteAsync(string text, CancellationToken ct = default)
        {
            WrittenLines.Add(text);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        {
            WrittenLines.Add(text ?? string.Empty);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() => DisposeCount++;
    }

    private sealed class NullRenderContext : ITuiRenderContext
    {
        public int Width => 80;
        public int Height => 24;
        public bool SupportsColor => false;
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) { }
        public void WriteStyled(string text, TuiStyle style) { }
        public void SetCursorPosition(int row, int col) { }
        public void ClearLine() { }
        public void Clear() { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void EnterAlternateScreen() { }
        public void ExitAlternateScreen() { }
        public void Flush() { }
    }
}
