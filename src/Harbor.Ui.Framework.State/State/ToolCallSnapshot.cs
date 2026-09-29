using Harbor.Abstractions.Tools;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One tool invocation as the UI is meant to receive it: structured, complete,
///     and produced once by the state reducer (#680).
/// </summary>
/// <remarks>
///     <para>
///         This record exists because the structure used to be thrown away. The
///         reducer formatted a tool start into the display string
///         <c>"→ edit {…}"</c>, stored only that string on the transcript, and the
///         chat view-model parsed the string back to recover a name and an argument
///         preview — losing the diff payload on the way, so the branch that was
///         supposed to publish it never ran. Renderers READ this record; they do not
///         reconstruct it.
///     </para>
///     <para>
///         <see cref="Glyph" /> is the calling tool's own <see cref="ITool.Glyph" />
///         rather than a value a renderer looks up by name, which is what makes
///         adding a tool touch no UI code.
///     </para>
/// </remarks>
/// <param name="Id">Correlation id, shared with the transcript line's ToolCallId.</param>
/// <param name="ToolName">The tool's stable name.</param>
/// <param name="Glyph">The tool's declared glyph, falling back to <see cref="ToolGlyphs.Default" />.</param>
/// <param name="ArgsPreview">The arguments as the card shows them.</param>
/// <param name="Status">Lifecycle phase, in the shared <c>ToolCallState</c> vocabulary (#567).</param>
/// <param name="ResultPreview">The result as the card shows it.</param>
/// <param name="IsDiffTool">Whether this call carries a diff payload.</param>
/// <param name="DiffFilePath">The diffed file, when <paramref name="IsDiffTool" />.</param>
/// <param name="DiffPreview">The inline preview, when <paramref name="IsDiffTool" />.</param>
/// <param name="DiffFull">The full diff behind the expand path, when <paramref name="IsDiffTool" />.</param>
public sealed record ToolCallSnapshot(
    string Id,
    string ToolName,
    string Glyph,
    string ArgsPreview,
    ToolCallState Status,
    string ResultPreview,
    bool IsDiffTool,
    string? DiffFilePath = null,
    string? DiffPreview = null,
    string? DiffFull = null)
{
    /// <summary>
    ///     The placeholder published the moment the model NAMES a tool, before it
    ///     executes. No arguments yet, so a generic glyph and no diff payload: the
    ///     card shows a name and a spinner, which is all that is known. The
    ///     matching <c>ToolExecutionStartEvent</c> replaces this entry with the
    ///     full call (#680).
    /// </summary>
    public static ToolCallSnapshot Named(string id, string toolName) =>
        new(
            id,
            toolName,
            ToolGlyphs.Default,
            string.Empty,
            ToolCallState.Pending,
            string.Empty,
            IsDiffTool: false);

    /// <summary>
    ///     The full snapshot for a call that is executing. The diff payload is
    ///     derived here — once, in the state producer — rather than per renderer.
    /// </summary>
    public static ToolCallSnapshot Start(
        string id,
        string toolName,
        string? glyph,
        string argsJson)
    {
        // An absent/undefined argument payload arrives as an empty string from a
        // host that never populated one; JsonDocument.Parse("") would throw, and
        // the card must still render.
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            argsJson = "{}";
        }

        // Qualified: this record declares its own DiffPreview property, which would
        // otherwise shadow the static helper of the same name (CS0120).
        var (isDiffTool, filePath, preview, fullDiff)
            = State.DiffPreview.ExtractDiff(toolName, argsJson);

        return new ToolCallSnapshot(
            id,
            toolName,
            string.IsNullOrEmpty(glyph) ? ToolGlyphs.Default : glyph,
            string.IsNullOrEmpty(argsJson) || argsJson == "{}" ? string.Empty : argsJson,
            ToolCallState.Running,
            string.Empty,
            isDiffTool,
            filePath,
            preview,
            fullDiff);
    }
}
