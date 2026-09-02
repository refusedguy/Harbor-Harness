namespace Harbor.Tui.AnsiPlain;

using Harbor.Tui.AnsiPlain.EscapeCodes;
using Microsoft.Extensions.Logging;

/// <summary>
///     ANSI mode of the unified renderer — full color/style support on a real
///     terminal. Thin subclass of <see cref="AnsiPlainTuiRenderer"/> selecting
///     <see cref="AnsiEscapeStrategy"/>; all render logic lives in the base.
/// </summary>
/// <remarks>
///     <para>
///         <b>Inheritance is intentional:</b> this class is unsealed so that
///         specialized renderers (e.g. <c>SixelTuiRenderer</c> in
///         <c>Harbor.Tui.Sixel</c>) can extend it and reuse the streaming
///         token feed while adding image-emission hooks. We chose inheritance
///         over composition here because the Sixel renderer needs to override
///         <see cref="BaseTuiRenderer.RenderAsync"/> with minimal logic —
///         calling <c>base.RenderAsync</c> for the common case and only
///         intercepting <c>ToolExecutionEndEvent</c> payloads that may carry
///         image bytes.
///     </para>
/// </remarks>
[Harbor.CodeGen.TuiRenderer("ansi")]
public partial class AnsiTuiRenderer : AnsiPlainTuiRenderer
{
    public AnsiTuiRenderer(ILogger<AnsiTuiRenderer> logger)
        : base(Console.Out, ownsWriter: false, AnsiEscapeStrategy.Instance, logger)
    {
    }

    /// <summary>
    ///     ANSI mode over a caller-supplied writer (golden-frame tests, string
    ///     capture). Production code keeps using <see cref="Console.Out"/>.
    /// </summary>
    public AnsiTuiRenderer(ILogger<AnsiTuiRenderer> logger, TextWriter writer)
        : base(writer, ownsWriter: false, AnsiEscapeStrategy.Instance, logger)
    {
    }

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Context.HideCursor();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }
}
