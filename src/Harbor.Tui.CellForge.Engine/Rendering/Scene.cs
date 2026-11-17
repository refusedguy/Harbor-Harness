// #2353: display list (Scene) for the engine's rendering vocabulary. The idea
// comes from GPUI's scene.rs: record primitives once, replay a recorded range
// later instead of running the same paint code again. Here a primitive is one
// of ScreenBuffer's public paint calls, so the op stream is backend-neutral
// (Cell, Rune, CellStyle, Rect, CellDiffOption) and replay has exactly the
// semantics of a direct call. BCL-only, AOT-compatible, render-thread owned,
// same as ScreenBuffer and DiffEngine.

using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Which ScreenBuffer paint call an operation stands for.</summary>
public enum SceneOpKind : byte
{
    Fill,
    SetRune,
    SetText,
    SetStyleAt,
    SetDiffOption,
}

/// <summary>
/// One recorded paint operation. A value type on purpose: a display list holds
/// many of them and replay must not allocate per op. The fields are the
/// arguments of the corresponding <see cref="ScreenBuffer"/> method, kept as
/// values so replay can be verified against a directly painted buffer.
/// </summary>
public readonly struct SceneOp : IEquatable<SceneOp>
{
    private readonly int _rune;

    private SceneOp(
        SceneOpKind kind,
        int x,
        int y,
        Rect rect,
        Cell cell,
        int rune,
        in CellStyle style,
        string? text,
        CellDiffOption option,
        ushort forcedWidth)
    {
        Kind = kind;
        X = x;
        Y = y;
        Rect = rect;
        Cell = cell;
        _rune = rune;
        Style = style;
        Text = text;
        Option = option;
        ForcedWidth = forcedWidth;
    }

    /// <summary>Records <see cref="ScreenBuffer.Fill"/>.</summary>
    public static SceneOp Fill(in Rect rect, in Cell cell) =>
        new(SceneOpKind.Fill, 0, 0, rect, cell, 0, default, null, default, 0);

    /// <summary>Records <see cref="ScreenBuffer.SetRune"/>.</summary>
    public static SceneOp SetRune(int x, int y, Rune rune, in CellStyle style) =>
        new(SceneOpKind.SetRune, x, y, default, default, rune.Value, in style, null, default, 0);

    /// <summary>
    /// Records <see cref="ScreenBuffer.SetText"/>. The span is copied into a
    /// string because a display list outlives the caller's stack frame.
    /// </summary>
    public static SceneOp SetText(int x, int y, ReadOnlySpan<char> text, in CellStyle style) =>
        new(SceneOpKind.SetText, x, y, default, default, 0, in style, text.ToString(), default, 0);

    /// <summary>Records <see cref="ScreenBuffer.SetStyleAt"/>.</summary>
    public static SceneOp SetStyleAt(int x, int y, in CellStyle style) =>
        new(SceneOpKind.SetStyleAt, x, y, default, default, 0, in style, null, default, 0);

    /// <summary>Records <see cref="ScreenBuffer.SetDiffOption"/>.</summary>
    public static SceneOp SetDiffOption(int x, int y, CellDiffOption option, ushort forcedWidth) =>
        new(SceneOpKind.SetDiffOption, x, y, default, default, 0, default, null, option, forcedWidth);

    public SceneOpKind Kind { get; }
    public int X { get; }
    public int Y { get; }
    public Rect Rect { get; }
    public Cell Cell { get; }
    public Rune Value => new(_rune);
    public CellStyle Style { get; }
    public string? Text { get; }
    public CellDiffOption Option { get; }
    public ushort ForcedWidth { get; }

    public bool Equals(SceneOp other) =>
        Kind == other.Kind
        && X == other.X
        && Y == other.Y
        && Rect == other.Rect
        && Cell == other.Cell
        && _rune == other._rune
        && Style == other.Style
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Option == other.Option
        && ForcedWidth == other.ForcedWidth;

    public override bool Equals(object? obj) => obj is SceneOp other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((byte)Kind);
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Rect);
        hash.Add(Cell);
        hash.Add(_rune);
        hash.Add(Style);
        hash.Add(Text);
        hash.Add((byte)Option);
        hash.Add(ForcedWidth);
        return hash.ToHashCode();
    }

    public static bool operator ==(SceneOp left, SceneOp right) => left.Equals(right);
    public static bool operator !=(SceneOp left, SceneOp right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        SceneOpKind.Fill => $"Fill({Rect}, {Cell})",
        SceneOpKind.SetRune => $"SetRune({X},{Y}, {Value}, {Style})",
        SceneOpKind.SetText => $"SetText({X},{Y}, \"{Text}\", {Style})",
        SceneOpKind.SetStyleAt => $"SetStyleAt({X},{Y}, {Style})",
        _ => $"SetDiffOption({X},{Y}, {Option}, {ForcedWidth})",
    };
}

/// <summary>
/// Offset into a <see cref="Scene"/>. Returned by <see cref="Scene.Mark"/> so a
/// cached subtree can close its own recording with <see cref="Scene.Close"/>.
/// </summary>
public readonly record struct SceneMark(int Offset);

/// <summary>
/// Half-open run of operations in a <see cref="Scene"/>. Ranges are copied
/// between scenes by <see cref="Scene.Replay"/>, which is how a cached subtree
/// contributes a finished op sequence to a new frame without repainting.
/// </summary>
public readonly record struct SceneRange(int Start, int Count)
{
    public int End => Start + Count;
    public bool IsEmpty => Count == 0;

    internal void Validate(int totalOps, string paramName)
    {
        if ((uint)Start > (uint)totalOps || (uint)Count > (uint)(totalOps - Start))
        {
            throw new ArgumentOutOfRangeException(
                paramName, $"Range [{Start}, {End}) is outside a scene of {totalOps} operations.");
        }
    }
}

/// <summary>
/// Ordered display list of paint operations. Record a frame into a Scene and
/// replay it onto a <see cref="ScreenBuffer"/> (or through
/// <see cref="DiffEngine.FlushScene"/>). Replaying a range recorded earlier
/// replaces re-running the paint code that produced it, so a cached subtree
/// costs one op copy per frame instead of a full Paint pass.
/// </summary>
/// <remarks>
/// A Scene is a complete frame description: <see cref="DiffEngine.FlushScene"/>
/// blanks its scratch buffer before replay. Applying a Scene to a persistent
/// buffer that already holds content is also valid, but the caller owns the
/// initial state (call <see cref="ScreenBuffer.BlankAll"/> first for a
/// full-frame semantic). Render-thread owned, not thread-safe.
/// </remarks>
public sealed class Scene
{
    private readonly List<SceneOp> _ops;

    public Scene() : this(64)
    {
    }

    public Scene(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ops = new List<SceneOp>(capacity);
    }

    public int Count => _ops.Count;

    public SceneOp this[int index] => _ops[index];

    /// <summary>The recorded operations, in paint order.</summary>
    public IReadOnlyList<SceneOp> Operations => _ops;

    // ── Recording ──────────────────────────────────────────────────────────

    /// <summary>Records a fill with the same clipping and wide-cell rules as the buffer.</summary>
    public void Fill(in Rect rect, in Cell cell) => _ops.Add(SceneOp.Fill(in rect, in cell));

    /// <summary>Records a rune placement.</summary>
    public void SetRune(int x, int y, Rune rune, in CellStyle style) =>
        _ops.Add(SceneOp.SetRune(x, y, rune, in style));

    /// <summary>Records a text run. The text is copied into the op.</summary>
    public void SetText(int x, int y, ReadOnlySpan<char> text, in CellStyle style) =>
        _ops.Add(SceneOp.SetText(x, y, text, in style));

    /// <summary>Records a style-only change.</summary>
    public void SetStyleAt(int x, int y, in CellStyle style) =>
        _ops.Add(SceneOp.SetStyleAt(x, y, in style));

    /// <summary>Records a diff directive.</summary>
    public void SetDiffOption(int x, int y, CellDiffOption option, ushort forcedWidth = 0) =>
        _ops.Add(SceneOp.SetDiffOption(x, y, option, forcedWidth));

    /// <summary>Marks the current end of the list. Paired with <see cref="Close"/>.</summary>
    public SceneMark Mark() => new(_ops.Count);

    /// <summary>Closes a mark into a range covering everything recorded since it.</summary>
    public SceneRange Close(SceneMark mark)
    {
        if ((uint)mark.Offset > (uint)_ops.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(mark));
        }

        return new SceneRange(mark.Offset, _ops.Count - mark.Offset);
    }

    /// <summary>
    /// Records a subtree into this scene and returns the range it produced.
    /// A caller that keeps the source scene can replay that range on later
    /// frames instead of calling <paramref name="paint"/> again.
    /// </summary>
    public SceneRange Record(Action<Scene> paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        var mark = Mark();
        paint(this);
        return Close(mark);
    }

    /// <summary>Drops every recorded operation. Capacity is kept for reuse.</summary>
    public void Clear() => _ops.Clear();

    // ── Replay ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies <paramref name="range"/> from <paramref name="source"/> into this
    /// scene and returns the range of the copy. This is the range replay
    /// between frames: the source's subtree was painted once, the new frame
    /// only appends its ready-made operations.
    /// </summary>
    public SceneRange Replay(Scene source, SceneRange range)
    {
        ArgumentNullException.ThrowIfNull(source);
        range.Validate(source._ops.Count, nameof(range));

        int start = _ops.Count;
        for (int i = 0; i < range.Count; i++)
        {
            _ops.Add(source._ops[range.Start + i]);
        }

        return new SceneRange(start, range.Count);
    }

    /// <summary>Applies the whole display list to <paramref name="buffer"/>.</summary>
    public void ReplayTo(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < _ops.Count; i++)
        {
            Apply(buffer, _ops[i]);
        }
    }

    /// <summary>Applies only the selected range to <paramref name="buffer"/>.</summary>
    public void ReplayTo(ScreenBuffer buffer, SceneRange range)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        range.Validate(_ops.Count, nameof(range));
        for (int i = 0; i < range.Count; i++)
        {
            Apply(buffer, _ops[range.Start + i]);
        }
    }

    private static void Apply(ScreenBuffer buffer, in SceneOp op)
    {
        switch (op.Kind)
        {
            case SceneOpKind.Fill:
                buffer.Fill(op.Rect, op.Cell);
                break;
            case SceneOpKind.SetRune:
                buffer.SetRune(op.X, op.Y, op.Value, op.Style);
                break;
            case SceneOpKind.SetText:
                buffer.SetText(op.X, op.Y, op.Text.AsSpan(), op.Style);
                break;
            case SceneOpKind.SetStyleAt:
                buffer.SetStyleAt(op.X, op.Y, op.Style);
                break;
            case SceneOpKind.SetDiffOption:
                buffer.SetDiffOption(op.X, op.Y, op.Option, op.ForcedWidth);
                break;
        }
    }
}
