using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Host-supplied metadata for one attachment. Deliberately domain-neutral:
/// no message, tool call, session, panel or chat type leaks into this record.
/// A host maps whatever it stores into these fields before handing the card
/// an instance, so the primitive stays reusable outside the chat timeline.
/// </summary>
/// <param name="Name">File name shown on the card (already path-free).</param>
/// <param name="MimeType">Source MIME type, used for the summary line.</param>
/// <param name="SizeBytes">Byte count, clamped to zero when negative.</param>
/// <param name="Path">Optional source path, never used for I/O by the card.</param>
/// <param name="Data">Optional raw bytes, consumed only through ImageBlock.</param>
public sealed record AttachmentRecord(
    string Name,
    string MimeType,
    long SizeBytes,
    string? Path = null,
    byte[]? Data = null);

/// <summary>
/// One host action the card offers. The card owns the label and the callback
/// but performs no work itself: invoking an action is the host's decision.
/// This is a slot, not an extension axis — no registry or plugin is involved.
/// </summary>
/// <param name="Label">Short text shown in the action row.</param>
/// <param name="Invoke">Host callback, handed the current record.</param>
public sealed record AttachmentAction(string Label, Action<AttachmentRecord> Invoke);

/// <summary>
/// Host-produced preview for a record. Text lines are painted by the card;
/// image bytes are handed to <see cref="ImageBlock"/>, which places them
/// through the frame's <see cref="IInlineImageSink"/>. The card never encodes
/// an image and never learns which terminal protocol is in use.
/// </summary>
/// <param name="Lines">Optional text lines, painted below the summary.</param>
/// <param name="ImageBytes">Optional raw image bytes for the graphics path.</param>
/// <param name="ImageMimeType">MIME of <paramref name="ImageBytes"/>, or the record's MIME.</param>
public sealed record AttachmentPreview(
    IReadOnlyList<string>? Lines = null,
    byte[]? ImageBytes = null,
    string? ImageMimeType = null);

/// <summary>
/// Compact, domain-independent attachment card: name, type, size, a host
/// preview callback and a set of host actions. Generalized from the chat
/// image card (#387) without pulling chat or Harbor-session types into the
/// contract. The record is supplied from outside (<see cref="SetRecord"/>),
/// so a host can refresh the card without rebuilding it.
/// </summary>
/// <remarks>
/// <para>
///     Preview is a callback, not an I/O path. The host decides what to read
///     and returns an <see cref="AttachmentPreview"/>. When that preview has
///     image bytes and the frame carries an enabled <see cref="IInlineImageSink"/>,
///     the card delegates placement to <see cref="ImageBlock"/> — the same
///     encode/clip/place path every image block uses, so no second image layer
///     is introduced (#2210/#2254). With no sink, the card paints only its text.
/// </para>
/// <para>
///     Actions are data: the card paints their labels and exposes
///     <see cref="ActivateAction"/> for the host's input layer. It owns no key
///     bindings, so it adds no key-gate site and no extension axis (#555).
/// </para>
/// </remarks>
public sealed class AttachmentCard
{
    /// <summary>Left gutter, matching the image card's indent.</summary>
    public const int LeftPad = 1;

    /// <summary>Upper bound on preview text lines painted in one card.</summary>
    public const int MaxPreviewLines = 8;

    private AttachmentRecord _record;

    /// <summary>Build a card around a host-supplied record.</summary>
    public AttachmentCard(AttachmentRecord record) => _record = Ensure.NotNull(record);

    /// <summary>Current record. Content, not widget state.</summary>
    public AttachmentRecord Record => _record;

    /// <summary>
    /// Host preview producer. Null (or a null result) paints no preview. The
    /// callback is called both by <see cref="Measure"/> and by <see cref="Paint"/>,
    /// so it must be side-effect free — the same rule GenericTreeView.Preview uses.
    /// </summary>
    public Func<AttachmentRecord, AttachmentPreview?>? Preview { get; set; }

    /// <summary>Action labels and callbacks, in display order.</summary>
    public IReadOnlyList<AttachmentAction> Actions { get; set; } = Array.Empty<AttachmentAction>();

    /// <summary>Index of the highlighted action, clamped to the action set.</summary>
    public int FocusedAction
    {
        get => _focused;
        set => _focused = Math.Clamp(value, 0, Math.Max(0, Actions.Count - 1));
    }

    private int _focused;

    /// <summary>Replace the record, keeping preview and actions attached.</summary>
    public void SetRecord(AttachmentRecord record) => _record = Ensure.NotNull(record);

    /// <summary>
    /// Invoke action <paramref name="index"/> with the current record. Returns
    /// false for an out-of-range index, so a host can treat it as "not consumed".
    /// </summary>
    public bool ActivateAction(int index)
    {
        if ((uint)index >= (uint)Actions.Count)
        {
            return false;
        }

        Actions[index].Invoke(_record);
        return true;
    }

    /// <summary>
    /// Rows the card needs at <paramref name="width"/>: two header rows, the
    /// preview slot (text lines or the fitted image height) and one action row
    /// when actions exist. Pure: the callback is consulted but no I/O happens.
    /// </summary>
    public BlockMeasure Measure(int width)
    {
        int rows = 2;
        AttachmentPreview? preview = Preview?.Invoke(_record);
        if (preview is not null)
        {
            if (preview.ImageBytes is { Length: > 0 })
            {
                rows += Math.Min(PreviewGraphicRows(preview, width), ImageBlock.MaxGraphicRows) + 1;
            }
            else if (preview.Lines is { Count: > 0 })
            {
                rows += Math.Min(preview.Lines.Count, MaxPreviewLines);
            }
        }

        if (Actions.Count > 0)
        {
            rows++;
        }

        return BlockMeasure.Exact(rows);
    }

    /// <summary>Plain-text copy of the card, independent of any styling.</summary>
    public string RawText()
    {
        var sb = new StringBuilder(Name.Length + MimeType.Length + 32)
            .Append(Name).Append(' ').Append(MimeType).Append(' ').AppendLine(FormatSize(SizeBytes));
        AttachmentPreview? preview = Preview?.Invoke(_record);
        if (preview?.Lines is { Count: > 0 })
        {
            foreach (string line in preview.Lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>File name from the current record.</summary>
    public string Name => _record.Name;

    /// <summary>MIME type from the current record.</summary>
    public string MimeType => _record.MimeType;

    /// <summary>Non-negative byte count from the current record.</summary>
    public long SizeBytes => Math.Max(0, _record.SizeBytes);

    /// <summary>
    /// Paint the card into <paramref name="rect"/>. Text always paints; the
    /// graphic path runs only when a preview carries bytes and the sink is on.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect, IInlineImageSink? sink = null)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int avail = rect.Width - LeftPad;
        if (avail <= 0)
        {
            return;
        }

        string line1 = "▣ " + Name;
        buffer.SetText(rect.X + LeftPad, rect.Y,
            line1.AsSpan(0, Math.Min(avail, line1.Length)), ChatPalette.ToolOk);

        if (rect.Height < 2)
        {
            return;
        }

        string summary = MimeType + " · " + FormatSize(SizeBytes);
        buffer.SetText(rect.X + LeftPad, rect.Y + 1,
            summary.AsSpan(0, Math.Min(avail, summary.Length)), ChatPalette.Dim);

        AttachmentPreview? preview = Preview?.Invoke(_record);
        int actionRow = Actions.Count > 0 ? rect.Y + rect.Height - 1 : -1;
        int previewRows = actionRow < 0
            ? rect.Y + rect.Height
            : actionRow;

        if (preview?.ImageBytes is { Length: > 0 } && sink is { Enabled: true })
        {
            PaintGraphic(buffer, rect, preview, sink, previewRows);
        }
        else if (preview?.Lines is { Count: > 0 })
        {
            int count = Math.Min(preview.Lines.Count, Math.Min(MaxPreviewLines, previewRows - (rect.Y + 2)));
            for (int i = 0; i < count; i++)
            {
                string text = preview.Lines[i];
                buffer.SetText(rect.X + LeftPad, rect.Y + 2 + i,
                    text.AsSpan(0, Math.Min(avail, text.Length)), ChatPalette.ToolBody);
            }
        }

        if (actionRow >= rect.Y + 2)
        {
            var actions = new StringBuilder();
            for (int i = 0; i < Actions.Count; i++)
            {
                if (i > 0)
                {
                    actions.Append("  ");
                }

                actions.Append('[').Append(i + 1).Append("] ").Append(Actions[i].Label);
            }

            string text = actions.ToString();
            buffer.SetText(rect.X + LeftPad, actionRow,
                text.AsSpan(0, Math.Min(avail, text.Length)), ChatPalette.Dim);
        }
    }

    /// <summary>
    /// Hand image bytes to ImageBlock, which clips to the frame and places
    /// the payload through the sink. The card reserves the caption row the
    /// block paints and leaves the action row untouched.
    /// </summary>
    private int PaintGraphic(ScreenBuffer buffer, Rect rect, AttachmentPreview preview, IInlineImageSink sink, int previewRows)
    {
        var block = new ImageBlock(Name, preview.ImageMimeType ?? MimeType, SizeBytes, preview.ImageBytes, true);
        int graphicRows = block.GraphicHeight(rect.Width);
        if (graphicRows <= 0 || rect.Y + 2 + graphicRows + 1 > previewRows)
        {
            return 0;
        }

        var box = new Rect(rect.X, rect.Y + 2, rect.Width, graphicRows + 1);
        block.Paint(new BlockPaintContext(buffer, box, 0, inlineImages: sink));
        return graphicRows;
    }

    /// <summary>Fitted graphic height, or zero when the bytes are not image data.</summary>
    private int PreviewGraphicRows(AttachmentPreview preview, int width)
    {
        if (preview.ImageBytes is not { Length: > 0 } || width <= LeftPad)
        {
            return 0;
        }

        var block = new ImageBlock(Name, preview.ImageMimeType ?? MimeType, SizeBytes, preview.ImageBytes, true);
        return block.GraphicHeight(width);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
