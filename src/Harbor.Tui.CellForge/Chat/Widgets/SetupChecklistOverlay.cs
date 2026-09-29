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
    /// Key routing for the checklist. The guide is an informational read-only
    /// surface with no focusable control, so it never traps the user:
    /// <c>Esc</c>/<c>q</c>/<c>?</c> dismiss and consume, <c>Enter</c> dismisses
    /// but falls through so an empty composer still submits, and every other key
    /// falls straight through to the composer behind it. Returns false when the
    /// checklist is hidden or the key is not one of the dismiss keys.
    /// </summary>
    /// <param name="key">Decoded key event.</param>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        if (key.Key == KeyCode.Escape)
        {
            Hide();
            return true;
        }

        if (key.Key == KeyCode.Char && (key.Character.Value is 'q' or '?'))
        {
            Hide();
            return true;
        }

        if (key.Key == KeyCode.Enter)
        {
            Hide();
        }

        return false;
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

        PanelChrome.PaintBorderBox(buffer, box);

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
                ? new CellStyle(ChatPalette.Success, attrs: StyleAttr.Bold)
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

    private static string Truncate(string text, int width) =>
        width <= 0 ? string.Empty : text.Length <= width ? text : text[..width];
}
