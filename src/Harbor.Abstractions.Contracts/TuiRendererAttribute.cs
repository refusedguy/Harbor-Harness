using System;

namespace Harbor.Abstractions.Contracts;

/// <summary>
///     Marks a concrete <see cref="Harbor.Terminal.Abstractions.ITuiRenderer" /> implementation for the
///     <see cref="Harbor.CodeGen.RendererAdapterGenerator" />. The generator produces a
///     partial class with backend metadata (id, frame-boundary constants) that the
///     host frame loop consumes to specialise output per backend.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TuiRendererAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the backend kind identifying the output strategy
    ///     (e.g. "ansi", "plain", "cellforge", "nickconsoleex").
    /// </summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the fully-qualified name of the context class that
    ///     implements <see cref="Harbor.Terminal.Abstractions.ITuiRenderContext" />.
    ///     Defaults to the annotated class's name + <c>"RenderContext"</c>.
    /// </summary>
    public string? ContextType { get; set; }

    /// <summary>
    ///     Gets or sets the fully-qualified name of the helper method used
    ///     to format tool arguments for display. If null, raw JSON is used.
    /// </summary>
    public string? ArgsFormatter { get; set; }
}
