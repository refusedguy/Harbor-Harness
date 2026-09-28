using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Widgets;

// KILLER_FEATURES §2.7 Feature 9 (issue #383): the cell-native setup-guide
// modal — a centered checklist with a `✓`/`○` marker per task plus a
// `GaugeBar` progress indicator (`3/5`) driven by the same pure model. Uses the
// existing `GaugeBar` primitive and the dialog modal language (rounded box,
// panel fill, accent title); no new widget family, no new theme tokens.
//
// Hidden paint is a no-op so steady-state frames and goldens stay
// byte-identical until the host shows it.
// Thread model: `SetModel` may run on a host thread while the render thread
// paints — the model reference is swapped through `Volatile`, and the model
// itself is immutable, so no lock is needed and no paint can observe a torn
// snapshot.

/// <summary>
/// Setup-guide checklist overlay (issue #383): "you are 3/5 done, here is what
/// is left". The post-wizard surface — the linear
/// <see cref="Harbor.Tui.CellForge.Onboarding.OnboardingFlow" /> wizard stays a
/// separate, optional flow.
/// </summary>
public sealed class SetupChecklistOverlay
{
    public const int MinWidth = 28;
    public const int MaxWidth = 64;
    public const int MinHeight = 8;
    public const int MaxHeight = 20;
    private const int Padding = 1;
    private const int Chrome = 2 + (Padding * 2);

    /// <summary>Title shown in the modal header.</summary>
    internal const string Title = "Setup guide";

    /// <summary>Footer hint naming the dismiss key and the re-entry command.</summary>
    internal const string FooterHint = "Esc to close · /setup to reopen";

    private SetupChecklistModel _model = SetupChecklistModel.Empty;

    /// <summary>True while the checklist owns the screen.</summary>
    public bool Visible { get; private set; }

    /// <summary>
    /// Current checklist snapshot (never null). Volatile read so a host-side
    /// refresh is visible to the next frame without a lock.
    /// </summary>
    public SetupChecklistModel Model => Volatile.Read(ref _model);

    /// <summary>
    /// Publish a new snapshot. Safe from any thread — the reference swap is
    /// atomic and the model is immutable.
    /// </summary>
    /// <param name="model">New snapshot; null resets to the empty checklist.</param>
    public void SetModel(SetupChecklistModel? model) =>
        Volatile.Write(ref _model, model ?? SetupChecklistModel.Empty);

    /// <summary>Show the checklist over the current (or supplied) snapshot.</summary>
    /// <param name="model">Optional snapshot to publish before showing.</param>
    public void Show(SetupChecklistModel? model = null)
    {
        if (model is not null)
        {
            SetModel(model);
        }

        Visible = true;
    }

    /// <summary>Hide the checklist (the host decides whether it may re-open).</summary>
    public void Hide() => Visible = false;

    /// <summary>
    /// Modal key routing: <c>Esc</c>/<c>Enter</c>/<c>q</c>/<c>?</c> dismiss, any
    /// other key is consumed so nothing leaks into the composer behind the
    /// modal. Returns false when the checklist is hidden.
    /// </summary>
    /// <param name="key">Decoded key event.</param>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        if (key.Key is KeyCode.Escape or KeyCode.Enter)
        {
            Hide();
            return true;
        }

        if (key.Key == KeyCode.Char && (key.Character.Value is 'q' or '?'))
        {
            Hide();
            return true;
        }

        return true;
    }

    /// <summary>
    /// Single source of truth for the centered box: content-measured width
    /// clamped to <c>MinWidth..MaxWidth</c> and the viewport, height from the
    /// row count. Never escapes the viewport.
    /// </summary>
    /// <param name="viewport">Viewport the box is centered in.</param>
    public Rect ComputeBox(Rect viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return default;
        }

        SetupChecklistModel model = Model;
        int longest = Title.Length;
        foreach (SetupTaskState task in model.Tasks)
        {
            longest = Math.Max(longest, task.RowText.Length);
        }

        longest = Math.Max(longest, model.ProgressText.Length + 4);

        int availW = Math.Max(0, viewport.Width - 2);
        int availH = Math.Max(0, viewport.Height - 2);
        int width = Math.Min(Math.Clamp(longest + Chrome, MinWidth, MaxWidth), Math.Max(1, availW));
        int rows = RowCount(model);
        int height = Math.Min(Math.Clamp(rows + 2, MinHeight, MaxHeight), Math.Max(1, availH));
        int x = viewport.X + Math.Max(0, (viewport.Width - width) / 2);
        int y = viewport.Y + Math.Max(0, (viewport.Height - height) / 2);
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Paints the modal centered inside <paramref name="viewport" /> (typically
    /// the full screen). No-op when hidden or when the box drops below the
    /// minimums — a hidden checklist never touches the buffer.
    /// </summary>
    /// <param name="buffer">Back buffer.</param>
    /// <param name="viewport">Viewport the box is centered in.</param>
    public void Paint(ScreenBuffer buffer, Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Visible)
        {
            return;
        }

        var box = ComputeBox(viewport);
        if (box.Width < MinWidth || box.Height < MinHeight)
        {
            return;
        }

        if (box.X >= buffer.Cols || box.Y >= buffer.Rows)
        {
            return;
        }

        DrawBox(buffer, box);

        SetupChecklistModel model = Model;
        int innerW = box.Width - Chrome;
        int x = box.X + 1 + Padding;
        int y = box.Y + 1;

        buffer.SetText(x, y, Truncate(Title, innerW), new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));
        y++;

        // Progress indicator: the shared GaugeBar primitive, labeled `done/total`
        // and filled from the same model's ratio — the ring equivalent in cells.
        GaugeBar.Paint(
            buffer,
            new Rect(x, y, innerW, 1),
            new GaugeState(model.Ratio, model.ProgressText, ShowLabel: true));
        y += 2;

        for (int i = 0; i < model.Tasks.Count && y < box.Bottom - 1; i++)
        {
            SetupTaskState task = model.Tasks[i];
            CellStyle markerStyle = task.IsDone
                ? new CellStyle(ChatPalette.ToolOk, attrs: StyleAttr.Bold)
                : ChatPalette.Dim;
            buffer.SetText(x, y, Truncate(task.Marker, innerW), markerStyle);
            buffer.SetText(x + 2, y, Truncate(task.Label, Math.Max(0, innerW - 2)), new CellStyle(ChatPalette.Text));
            y++;
        }

        if (y + 1 < box.Bottom)
        {
            buffer.SetText(x, box.Bottom - 2, Truncate(FooterHint, innerW), ChatPalette.Dim);
        }
    }

    /// <summary>Total content rows: title, gauge, one blank, tasks, footer.</summary>
    private static int RowCount(SetupChecklistModel model) => 3 + model.Tasks.Count + 1;

    /// <summary>Rounded modal box in the shared dialog language (panel fill, border).</summary>
    private static void DrawBox(ScreenBuffer buffer, Rect rect)
    {
        var fillStyle = new CellStyle(ChatPalette.Panel);
        var borderStyle = new CellStyle(ChatPalette.Border);
        buffer.Fill(rect, Cell.From(new Rune(' '), fillStyle));
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        buffer.At(x1, y1) = Cell.From(new Rune('╭'), borderStyle);
        buffer.At(x2, y1) = Cell.From(new Rune('╮'), borderStyle);
        buffer.At(x1, y2) = Cell.From(new Rune('╰'), borderStyle);
        buffer.At(x2, y2) = Cell.From(new Rune('╯'), borderStyle);
        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = Cell.From(new Rune('─'), borderStyle);
            buffer.At(x, y2) = Cell.From(new Rune('─'), borderStyle);
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = Cell.From(new Rune('│'), borderStyle);
            buffer.At(x2, y) = Cell.From(new Rune('│'), borderStyle);
        }
    }

    private static string Truncate(string text, int width) =>
        width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];
}
