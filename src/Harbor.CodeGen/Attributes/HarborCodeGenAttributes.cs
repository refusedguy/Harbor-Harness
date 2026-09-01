// Harbor.CodeGen attribute surface. This file is NOT compiled into the
// generator project — it is linked (shared-source) into every project that
// consumes a Harbor.CodeGen generator, so the attributes exist at runtime
// in the consuming assembly while the generator matches them by name
// (fully-qualified metadata name, no assembly reference required).

namespace Harbor.CodeGen;

/// <summary>
///     Marks an enum as a terminal escape-code vocabulary. The
///     <c>EscapeCodeGenerator</c> emits a zero-allocation
///     <c>EscapeCodes</c> static class (precomputed
///     <see cref="System.ReadOnlySpan{T}" /> tables and stack-only CSI/SGR
///     formatters) next to the annotated enum.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class TerminalEscapeAttribute : Attribute
{
}

/// <summary>
///     Declares the class as a TUI renderer backend. The
///     <c>RendererAdapterGenerator</c> emits a partial-class companion with
///     the <c>BackendId</c> constant and the generated frame-boundary table
///     entry. Opt-in per backend; unknown backend ids get empty boundaries.
/// </summary>
/// <param name="backend">
///     Stable backend id (e.g. <c>ansi</c>, <c>plain</c>, <c>cellforge</c>,
///     <c>nickconsoleex</c>).
/// </param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TuiRendererAttribute : Attribute
{
    public TuiRendererAttribute(string backend)
    {
        Backend = backend;
    }

    /// <summary>Stable backend id.</summary>
    public string Backend { get; }

    /// <summary>
    ///     Whether the backend owns the terminal cursor across a frame
    ///     (hide on frame entry, show on frame exit). Screen-based backends
    ///     set <c>true</c>; line/markup backends leave it <c>false</c>.
    /// </summary>
    public bool CursorFrameBoundary { get; set; }
}

/// <summary>
///     Marks an enum as a mascot mood with a frame bank. The
///     <c>MoodFrameGenerator</c> emits a dispatch class mapping each listed
///     mood to its frame bank (naming convention: <c>{Mood}Frames</c>) and
///     its tick period, replacing the hand-written switch.
/// </summary>
/// <param name="moods">Moods that participate in the frame dispatch.</param>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class MoodFrameAttribute : Attribute
{
    public MoodFrameAttribute(params object[] moods)
    {
        Moods = moods;
    }

    /// <summary>The moods covered by the generated dispatch table.</summary>
    public object[] Moods { get; }

    /// <summary>
    ///     Name of the static class holding the frame banks as
    ///     <c>{Mood}Frames</c> fields. Empty means the fields live at the
    ///     enum's own namespace level without a container prefix.
    /// </summary>
    public string? BankContainer { get; set; }

    /// <summary>
    ///     Tick period for the <c>Sleeping</c> mood (it advances one frame
    ///     per N ticks instead of every tick). 0 disables the special case.
    /// </summary>
    public int SleepPeriodTicks { get; set; }
}
