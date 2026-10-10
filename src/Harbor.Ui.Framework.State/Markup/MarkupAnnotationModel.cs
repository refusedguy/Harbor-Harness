using System.Collections.Immutable;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Screenshot-markup primitive kind (KILLER_FEATURES §2.7 Feature 14,
///     issue #400 slice 1/2). The freehand stroke the Orca overlay offers is
///     deliberately absent: a cell grid cannot carry it, and the keyboard
///     placement this slice promises needs primitives with two endpoints.
/// </summary>
public enum MarkupKind
{
    /// <summary>Directed segment: <c>From</c> tail, <c>To</c> head.</summary>
    Arrow,

    /// <summary>Box corners <c>From</c> (one corner) and <c>To</c> (opposite).</summary>
    Rectangle,

    /// <summary>Label anchored at <c>From</c>; <c>To</c> is unused.</summary>
    Text,
}

/// <summary>
///     A point in unit space. Both components are clamped to <c>[0..1]</c> on
///     construction, so the model is resolution-independent by construction:
///     the same annotation re-projects correctly at any zoom or terminal
///     resize without storing a single pixel or cell coordinate.
/// </summary>
public readonly record struct NormalizedPoint(double X, double Y)
{
    /// <summary>Creates a point, clamping both components into <c>[0..1]</c>.</summary>
    public static NormalizedPoint Create(double x, double y) => new(Clamp01(x), Clamp01(y));

    /// <summary>Returns this point shifted by (<paramref name="dx"/>, <paramref name="dy"/>), re-clamped.</summary>
    public NormalizedPoint Shift(double dx, double dy) => Create(X + dx, Y + dy);

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}

/// <summary>
///     One immutable annotation primitive. Coordinates are normalized (see
///     <see cref="NormalizedPoint" />); <see cref="Weight" /> is the stroke
///     width in pixels for <see cref="MarkupKind.Arrow" /> /
///     <see cref="MarkupKind.Rectangle" /> and the glyph scale for
///     <see cref="MarkupKind.Text" />. <see cref="Text" /> is meaningful only
///     for <see cref="MarkupKind.Text" />.
/// </summary>
public sealed record MarkupAnnotation(
    int Id,
    MarkupKind Kind,
    NormalizedPoint From,
    NormalizedPoint To,
    int Weight,
    string Text)
{
    /// <summary>Smallest stroke width / glyph scale.</summary>
    public const int MinWeight = 1;

    /// <summary>Largest stroke width / glyph scale.</summary>
    public const int MaxWeight = 5;

    /// <summary>Creates an annotation, clamping <paramref name="weight" /> into range.</summary>
    public static MarkupAnnotation Create(int id, MarkupKind kind, NormalizedPoint from, NormalizedPoint to, int weight = 2, string text = "")
        => new(id, kind, from, kind == MarkupKind.Text ? from : to, Math.Clamp(weight, MinWeight, MaxWeight), text ?? string.Empty);
}

/// <summary>One undo/redo frame: the content the stack restores verbatim.</summary>
/// <param name="Items">Annotations, oldest first.</param>
/// <param name="SelectedId">Selection at the time, or null.</param>
/// <param name="NextId">Id allocator at the time, so undo restores the sequence exactly.</param>
public readonly record struct MarkupUndoFrame(
    ImmutableArray<MarkupAnnotation> Items,
    int? SelectedId,
    int NextId);

/// <summary>
///     Pure annotation model (issue #400 slice 1/2): immutable value objects
///     plus a bounded undo/redo stack. No dependency on CellForge or
///     <c>Harbor.Application</c> — this file sees only BCL,
///     <see cref="ImmutableArray{T}" /> and the <see cref="Rect" /> it
///     projects into.
///     <para>
///         Undo covers content operations only (<c>Add</c> / <c>Delete</c> /
///         <c>Move</c> / <c>Resize</c> / drag checkpoint): selection moves never
///         push a frame, so undoing never yanks the cursor away. Undo and redo
///         are exact inverses — a frame restores items, selection AND the id
///         allocator, so redo replays the same ids.
///     </para>
///     <para>
///         The stacks are bounded by <see cref="MaxUndoDepth" />: pushing past
///         the cap drops the oldest frame. A drag (<c>Checkpoint</c> + N×
///         <c>NudgeSelected</c>) costs exactly one frame no matter how many
///         motion events it spans.
///     </para>
/// </summary>
public sealed record MarkupAnnotationModel(
    ImmutableArray<MarkupAnnotation> Items,
    int? SelectedId,
    int NextId,
    ImmutableArray<MarkupUndoFrame> Undo,
    ImmutableArray<MarkupUndoFrame> Redo)
{
    /// <summary>
    ///     Undo/redo depth cap. Pushing past it drops the oldest frame, so a
    ///     long session cannot grow the stacks without bound. Applies to both
    ///     stacks; 50 frames cover a real markup pass without pinning history.
    /// </summary>
    public const int MaxUndoDepth = 50;

    /// <summary>Empty model: no annotations, allocator at 1, empty stacks.</summary>
    public static readonly MarkupAnnotationModel Empty = new(
        ImmutableArray<MarkupAnnotation>.Empty, null, 1,
        ImmutableArray<MarkupUndoFrame>.Empty, ImmutableArray<MarkupUndoFrame>.Empty);

    /// <summary>Currently selected annotation, or null.</summary>
    public MarkupAnnotation? Selected
    {
        get
        {
            if (SelectedId is not { } id)
            {
                return null;
            }

            foreach (MarkupAnnotation item in Items)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }

            return null;
        }
    }

    /// <summary>
    ///     Projects a normalized point into cell space: <c>(0,0)</c> is the
    ///     rect origin, <c>(1,1)</c> its last cell. Pure and allocation-free,
    ///     so paint code and paint tests share the one mapping and a resize
    ///     re-derives every cell from the same model.
    /// </summary>
    public static (int Col, int Row) ProjectToCells(NormalizedPoint point, Rect rect)
    {
        int col = rect.X + (int)Math.Round(point.X * (rect.Width - 1), MidpointRounding.AwayFromZero);
        int row = rect.Y + (int)Math.Round(point.Y * (rect.Height - 1), MidpointRounding.AwayFromZero);
        return (Math.Clamp(col, rect.X, Math.Max(rect.X, rect.Right - 1)),
            Math.Clamp(row, rect.Y, Math.Max(rect.Y, rect.Bottom - 1)));
    }

    /// <summary>Adds a primitive and selects it. Pushes undo, clears redo.</summary>
    public MarkupAnnotationModel Add(MarkupKind kind, NormalizedPoint from, NormalizedPoint to, int weight = 2, string text = "")
    {
        var annotation = MarkupAnnotation.Create(NextId, kind, from, to, weight, text);
        return PushUndo() with
        {
            Items = Items.Add(annotation),
            SelectedId = annotation.Id,
            NextId = NextId + 1,
            Redo = ImmutableArray<MarkupUndoFrame>.Empty,
        };
    }

    /// <summary>Deletes <paramref name="id" /> (or the selection when null). No-op when unknown. Pushes undo, clears redo.</summary>
    public MarkupAnnotationModel Delete(int? id = null)
    {
        int target = id ?? SelectedId ?? -1;
        int index = -1;
        for (int i = 0; i < Items.Length; i++)
        {
            if (Items[i].Id == target)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return this;
        }

        var next = PushUndo() with
        {
            Items = Items.RemoveAt(index),
            Redo = ImmutableArray<MarkupUndoFrame>.Empty,
        };

        // Keep a live selection: the neighbour that slid into the hole, else null.
        int? selected = null;
        if (next.Items.Length > 0)
        {
            selected = next.Items[Math.Min(index, next.Items.Length - 1)].Id;
        }

        return next with { SelectedId = selected };
    }

    /// <summary>Selects <paramref name="id" /> (null clears). Unknown ids clear. Never touches undo.</summary>
    public MarkupAnnotationModel Select(int? id)
    {
        if (id is { } wanted)
        {
            bool known = false;
            foreach (MarkupAnnotation item in Items)
            {
                if (item.Id == wanted)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                id = null;
            }
        }

        return id == SelectedId ? this : this with { SelectedId = id };
    }

    /// <summary>Cycles the selection through the items in order (null → first, last → first). Empty model is a no-op. Never touches undo.</summary>
    public MarkupAnnotationModel SelectNext()
    {
        if (Items.Length == 0)
        {
            return this;
        }

        if (SelectedId is not { } id)
        {
            return this with { SelectedId = Items[0].Id };
        }

        for (int i = 0; i < Items.Length; i++)
        {
            if (Items[i].Id == id)
            {
                return this with { SelectedId = Items[(i + 1) % Items.Length].Id };
            }
        }

        return this with { SelectedId = Items[0].Id };
    }

    /// <summary>Moves the selection by (<paramref name="dx"/>, <paramref name="dy"/>) in unit space. Pushes undo, clears redo. No-op without a selection.</summary>
    public MarkupAnnotationModel MoveSelected(double dx, double dy)
    {
        if (Selected is not { } selected)
        {
            return this;
        }

        return ReplaceSelected(selected with { From = selected.From.Shift(dx, dy), To = selected.To.Shift(dx, dy) });
    }

    /// <summary>
    ///     Transient drag move: same shift as <see cref="MoveSelected" /> but
    ///     pushes NO undo frame and still clears redo (the drag branched the
    ///     history). The drag start records the single frame via
    ///     <see cref="Checkpoint" />, so the whole gesture undoes in one step.
    /// </summary>
    public MarkupAnnotationModel NudgeSelected(double dx, double dy)
    {
        if (Selected is not { } selected)
        {
            return this;
        }

        var moved = selected with { From = selected.From.Shift(dx, dy), To = selected.To.Shift(dx, dy) };
        return ReplaceSelectedNoUndo(moved);
    }

    /// <summary>
    ///     Resizes the selection by shifting its <c>To</c> endpoint (arrow head,
    ///     rectangle corner). Text has no second endpoint, so resizing it is a
    ///     documented no-op. Pushes undo, clears redo.
    /// </summary>
    public MarkupAnnotationModel ResizeSelected(double dx, double dy)
    {
        if (Selected is not { } selected || selected.Kind == MarkupKind.Text)
        {
            return this;
        }

        return ReplaceSelected(selected with { To = selected.To.Shift(dx, dy) });
    }

    /// <summary>Records the current content as one undo frame (drag start). Clears redo. The paired drag updates go through <see cref="NudgeSelected" />.</summary>
    public MarkupAnnotationModel Checkpoint()
    {
        var next = Undo.Add(new MarkupUndoFrame(Items, SelectedId, NextId));
        if (next.Length > MaxUndoDepth)
        {
            next = next.RemoveAt(0);
        }

        return this with { Undo = next, Redo = ImmutableArray<MarkupUndoFrame>.Empty };
    }

    /// <summary>Restores the newest undo frame; the current content moves to redo. Empty stack is a no-op.</summary>
    public MarkupAnnotationModel UndoFrame()
    {
        if (Undo.Length == 0)
        {
            return this;
        }

        var frame = Undo[^1];
        var redo = Redo.Add(new MarkupUndoFrame(Items, SelectedId, NextId));
        if (redo.Length > MaxUndoDepth)
        {
            redo = redo.RemoveAt(0);
        }

        return this with { Items = frame.Items, SelectedId = frame.SelectedId, NextId = frame.NextId, Undo = Undo.RemoveAt(Undo.Length - 1), Redo = redo };
    }

    /// <summary>Reapplies the newest redo frame; the current content moves back to undo. Empty stack is a no-op.</summary>
    public MarkupAnnotationModel RedoFrame()
    {
        if (Redo.Length == 0)
        {
            return this;
        }

        var frame = Redo[^1];
        var undo = Undo.Add(new MarkupUndoFrame(Items, SelectedId, NextId));
        if (undo.Length > MaxUndoDepth)
        {
            undo = undo.RemoveAt(0);
        }

        return this with { Items = frame.Items, SelectedId = frame.SelectedId, NextId = frame.NextId, Undo = undo, Redo = Redo.RemoveAt(Redo.Length - 1) };
    }

    private MarkupAnnotationModel PushUndo()
    {
        var next = Undo.Add(new MarkupUndoFrame(Items, SelectedId, NextId));
        if (next.Length > MaxUndoDepth)
        {
            next = next.RemoveAt(0);
        }

        return this with { Undo = next };
    }

    private MarkupAnnotationModel ReplaceSelected(MarkupAnnotation moved)
    {
        var next = PushUndo();
        var builder = next.Items.ToBuilder();
        for (int i = 0; i < builder.Count; i++)
        {
            if (builder[i].Id == moved.Id)
            {
                builder[i] = moved;
                break;
            }
        }

        return next with { Items = builder.MoveToImmutable(), Redo = ImmutableArray<MarkupUndoFrame>.Empty };
    }

    private MarkupAnnotationModel ReplaceSelectedNoUndo(MarkupAnnotation moved)
    {
        var builder = Items.ToBuilder();
        for (int i = 0; i < builder.Count; i++)
        {
            if (builder[i].Id == moved.Id)
            {
                builder[i] = moved;
                break;
            }
        }

        return this with { Items = builder.MoveToImmutable(), Redo = ImmutableArray<MarkupUndoFrame>.Empty };
    }
}
