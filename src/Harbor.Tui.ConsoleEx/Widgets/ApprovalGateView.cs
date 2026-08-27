using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>User decision produced by an <see cref="ApprovalGateView" /> gate.</summary>
public enum ApprovalChoice : byte
{
    /// <summary>Still waiting.</summary>
    None = 0,
    Approve,
    Deny,
    /// <summary>Approve AND remember for this run (host decides the scope).</summary>
    AlwaysAllow,
}

/// <summary>
/// Interactive permission card in the chat timeline: shows which tool wants
/// approval, what it targets, and the key bindings. Painted as a pending gate;
/// after a decision the hint row is replaced with a colored stamp so the
/// history keeps an audit trail (height stays identical — layout never jumps).
/// Blocks stay paint-only in this renderer, so decisions are made by the host
/// frame loop calling <see cref="HandleKey" /> first while the gate is focused.
/// </summary>
public sealed class ApprovalGateView : IChatBlock
{
    private const string HeaderLabel = "⚠ permission required";
    private const string HintLine = "[y] approve   [n] deny   [a] always allow";
    private const int LeftPad = 2;

    private readonly string _detailText;
    private List<string> _wrapped = [];
    private int _wrappedWidth = -1;
    private (int Start, int End)[]? _hintSpans;

    /// <summary>
    /// Screen-space clip rect from the most recent <see cref="Paint" /> pass.
    /// Lets hosts hit-test mouse clicks against the card without a layout pass;
    /// harmless staleness until the next frame.
    /// </summary>
    internal Rect? LastPaintRect { get; private set; }

    public ApprovalGateView(string toolName, string detail)
    {
        ToolName = string.IsNullOrWhiteSpace(toolName) ? "?" : toolName.Trim();
        _detailText = (detail ?? string.Empty).Trim();
        Decision = ApprovalChoice.None;
    }

    public string Kind => "approval";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 128 + ((ToolName.Length + _detailText.Length) * 2);

    /// <summary>Tool requesting approval (header line and audit stamp).</summary>
    public string ToolName { get; }

    public bool IsPending => Decision == ApprovalChoice.None;

    public ApprovalChoice Decision { get; private set; }

    /// <summary>
    /// Raised exactly once, on the render/input thread that called
    /// <see cref="HandleKey" />, when a decision is recorded. Hosts use it
    /// to wake awaiting prompt continuations (e.g. the permission asker).
    /// </summary>
    public event EventHandler? DecisionRecorded;

    private void Decide(ApprovalChoice choice)
    {
        Decision = choice;
        DecisionRecorded?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> WrappedDetail(int width)
    {
        EnsureWrapped(Math.Max(8, width));
        return _wrapped;
    }

    /// <summary>
    /// Height: header row + wrapped detail rows + hint/stamp row — identical
    /// pending and resolved so a repaint never shifts timeline slots.
    /// </summary>
    public BlockMeasure Measure(int width)
    {
        int w = Math.Max(8, width);
        EnsureWrapped(w);
        return BlockMeasure.Exact(_wrapped.Count + 2);
    }

    public int CheapEstimate(int width)
    {
        int w = Math.Max(8, width);
        return Math.Max(3, _wrapped.Count > 0 ? _wrapped.Count + 2 : BlockMath.EstimateLines(_detailText, w - 4) + 2);
    }

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int inner = Math.Max(0, ctx.Rect.Width - LeftPad);
        if (inner == 0 || ctx.Rect.Height <= 0)
        {
            return;
        }

        // Registered for click-to-decide hit-testing (mouse routing is host-side).
        LastPaintRect = ctx.Rect;

        // Header — warning accent until decided, dim once stamped.
        var headerStyle = IsPending
            ? new CellStyle(PackedColor.Indexed(3), attrs: StyleAttr.Bold)
            : ChatPalette.Dim;
        buffer.SetText(ctx.Rect.X, ctx.Rect.Y, Truncate(HeaderLabel + " · " + ToolName, ctx.Rect.Width), headerStyle);

        // Detail body, wrapped to the clip rect, plain tool-args tone.
        EnsureWrapped(inner);
        for (int i = 0; i < _wrapped.Count && 1 + i < ctx.Rect.Height; i++)
        {
            buffer.SetText(ctx.Rect.X + LeftPad, ctx.Rect.Y + 1 + i, _wrapped[i], ChatPalette.ToolArgs);
        }

        // Hints while pending, decision stamp after — same slot either way.
        int lastRow = ctx.Rect.Y + _wrapped.Count + 1;
        if (!ctx.Rect.Contains(ctx.Rect.X, lastRow))
        {
            return;
        }

        if (IsPending)
        {
            buffer.SetText(ctx.Rect.X, lastRow, Truncate(HintLine, ctx.Rect.Width), ChatPalette.Dim);
            return;
        }

        (string stamp, CellStyle style) = Decision switch
        {
            ApprovalChoice.Approve => ("✓ approved", ChatPalette.ToolOk),
            ApprovalChoice.Deny => ("✗ denied", ChatPalette.ToolError),
            _ => ("✓ approved (always)", new CellStyle(PackedColor.Indexed(6))),
        };
        buffer.SetText(ctx.Rect.X, lastRow, Truncate(stamp, ctx.Rect.Width), style);
    }

    public string RawText()
    {
        var sb = new System.Text.StringBuilder(HeaderLabel.Length + ToolName.Length + _detailText.Length + 32);
        sb.Append(HeaderLabel).Append(" · ").AppendLine(ToolName);
        if (_detailText.Length > 0)
        {
            sb.AppendLine(_detailText);
        }

        sb.Append(IsPending ? HintLine : Decision switch
        {
            ApprovalChoice.Approve => "approved",
            ApprovalChoice.Deny => "denied",
            _ => "approved (always)",
        });
        return sb.ToString();
    }

    /// <summary>
    /// Route one key event. Handles press/repeat only, no modifiers, and only
    /// while pending: y/Enter approve, n/Escape deny, a always-allow. Returns
    /// true when the key was consumed (the caller suppresses composer routing).
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!IsPending || (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat))
        {
            return false;
        }

        if (key.Modifiers != KeyModifiers.None)
        {
            return false;
        }

        if (key.Key == KeyCode.Enter)
        {
            Decide(ApprovalChoice.Approve);
            return true;
        }

        if (key.Key == KeyCode.Escape)
        {
            Decide(ApprovalChoice.Deny);
            return true;
        }

        if (key.Key != KeyCode.Char)
        {
            return false;
        }

        switch (Rune.ToUpperInvariant(key.Character).Value)
        {
            case 'Y':
                Decide(ApprovalChoice.Approve);
                return true;
            case 'N':
                Decide(ApprovalChoice.Deny);
                return true;
            case 'A':
                Decide(ApprovalChoice.AlwaysAllow);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Maps a mouse click to an approval choice: only the hint-row button
    /// spans of a PENDING gate respond, and only while its last painted rect
    /// is known. Returns null when the click lands outside any zone.
    /// </summary>
    public ApprovalChoice? TryHitDecision(int col, int row)
    {
        if (!IsPending || LastPaintRect is not { } rect)
        {
            return null;
        }

        int hintRow = rect.Y + _wrapped.Count + 1;
        if (row != hintRow || !rect.Contains(rect.X, hintRow))
        {
            return null;
        }

        int relCol = col - rect.X;
        EnsureHintSpans();
        var spans = _hintSpans!;
        for (int i = 0; i < spans.Length; i++)
        {
            if (relCol >= spans[i].Start && relCol < spans[i].End)
            {
                return (ApprovalChoice)(i + 1); // enum order: Approve, Deny, AlwaysAllow
            }
        }

        return null;
    }

    /// <summary>
    /// Column spans of the "[y] …", "[n] …", "[a] …" zones inside
    /// <see cref="HintLine" /> — located by the bracket markers so label edits
    /// cannot desync the hit map. Zone i runs from its '[' to the next zone's
    /// '[' (the separating gap belongs to no button).
    /// </summary>
    private void EnsureHintSpans()
    {
        if (_hintSpans != null)
        {
            return;
        }

        var starts = new int[3];
        int found = 0;
        for (int i = 0; i < HintLine.Length && found < 3; i++)
        {
            if (HintLine[i] == '[')
            {
                starts[found++] = i;
            }
        }

        var spans = new (int Start, int End)[3];
        for (int i = 0; i < 3; i++)
        {
            int nextStart = i + 1 < 3 ? starts[i + 1] : HintLine.Length;
            spans[i] = (starts[i], Math.Max(starts[i] + 1, nextStart));
        }

        _hintSpans = spans;
    }

    private void EnsureWrapped(int width)
    {
        if (_wrappedWidth == width)
        {
            return;
        }

        var lines = new List<string>(1);
        if (_detailText.Length > 0)
        {
            TextWrap.WrapTo(_detailText, Math.Max(4, width - LeftPad), lines);
        }

        _wrapped = lines;
        _wrappedWidth = width;
    }

    private static string Truncate(string s, int max) =>
        max <= 0 ? string.Empty : s.Length <= max ? s : s[..max];
}
