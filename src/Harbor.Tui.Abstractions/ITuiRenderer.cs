using CSharpFunctionalExtensions;
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
}
