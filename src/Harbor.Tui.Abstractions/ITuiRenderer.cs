using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.Renderers;
namespace Harbor.Tui.Abstractions;
/// <summary>
///     Strategy interface for TUI rendering (Strategy pattern, GOF).
///     The renderer manages the screen layout and dispatches events to views.
///     Allows swapping TUI implementations (Terminal.Gui v2, custom ANSI, etc.)
/// </summary>
public interface ITuiRenderer : IDisposable
{
    /// <summary>Render context for views to write to.</summary>
    public ITuiRenderContext Context { get; }

    /// <summary>View registry.</summary>
    public ViewRegistry Views { get; }

    /// <summary>View model registry.</summary>
    public ViewModelRegistry ViewModels { get; }

    /// <summary>Initialize the renderer.</summary>
    public Task<Result> InitializeAsync(CancellationToken ct = default);

    /// <summary>Render an agent event through all bound views.</summary>
    public Task RenderAsync(AgentEvent @event, CancellationToken ct = default);

    /// <summary>Read a line of input.</summary>
    public Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default);

    /// <summary>Write text directly.</summary>
    public Task<Result> WriteAsync(string text, CancellationToken ct = default);

    /// <summary>Write a line.</summary>
    public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default);

    /// <summary>Clear the screen.</summary>
    public Task<Result> ClearAsync(CancellationToken ct = default);

    /// <summary>
    ///     Run the interactive REPL loop. The default implementation drives a line-buffered
    ///     prompt → agent → render cycle. Full-screen renderers MAY override this to own the
    ///     input/screen lifecycle (alternate screen, live layout, inline editing).
    /// </summary>
    /// <param name="agent">The initialized agent to drive.</param>
    /// <param name="host">The service provider hosting the app (for slash commands).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code (0 = ok).</returns>
    public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default)
        => Task.FromResult(0);
}

/// <summary>
///     Marker interface for renderers that own the full interactive lifecycle (screen, input
///     loop). When a renderer implements this, <c>Program.cs</c> delegates the REPL to it instead
///     of running the default line-buffered loop.
/// </summary>
public interface IInteractiveTuiRenderer : ITuiRenderer
{
    /// <summary>
    ///     Provide the slash-command handler. The host wires this so the interactive renderer can
    ///     dispatch <c>/command</c> input without referencing Core directly.
    /// </summary>
    /// <param name="handler">Async handler taking the raw <c>/command</c> text.</param>
    public void SetSlashHandler(Func<string, Task> handler);
}
