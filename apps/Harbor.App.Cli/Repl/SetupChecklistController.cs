using Harbor.Application.Onboarding;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering.Input;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

// KILLER_FEATURES §2.7 Feature 9 (issue #383): host wiring for the setup-guide
// checklist. Mirrors the `SkillFreshness` slice: the pure model lives in the UI
// framework, detection in `Harbor.Application`, and this composition-root
// controller maps signals onto the model and feeds the CellForge overlay.
//
// Three responsibilities:
//   * detection → completion snapshot (`TaskId → bool`) → `SetupChecklistModel`;
//   * first-run gating — auto-open when setup never completed, latched so a
//     dismissal never re-traps the user in the same session;
//   * in-place progress — a task completing while the checklist is open
//     re-renders the modal alone (damage hint on its rect), so unrelated panels
//     are not rescanned.

/// <summary>
/// Drives the CellForge setup checklist for one REPL session: detection,
/// first-run gating, and the <c>/setup</c> re-entry.
/// </summary>
internal sealed class SetupChecklistController
{
    private readonly CellForgeReplRunner _host;
    private readonly SetupChecklistDetector _detector;

    /// <summary>
    /// Writer lock for the completion snapshot. Two writers are possible (the
    /// frame thread flips "first prompt sent", the probe thread flips
    /// "provider reachable"), so the map is copied-on-write under this lock and
    /// published as a whole — readers never see a half-updated map.
    /// </summary>
    private readonly object _completionGate = new();

    private IReadOnlyDictionary<string, bool> _completion = BuildInitialCompletion();

    /// <summary>First-run auto-open is armed and the user has not dismissed it yet.</summary>
    private bool _gateArmed;

    internal SetupChecklistController(CellForgeReplRunner host, SetupChecklistDetector detector)
    {
        _host = host;
        _detector = detector;
    }

    /// <summary>The overlay this controller drives (seated by <c>SyncOverlays</c>).</summary>
    private SetupChecklistOverlay Overlay => _host.Screen.SetupChecklist;

    /// <summary>
    /// Startup pass: detect the local signals, publish the first snapshot and
    /// open the checklist on first run (setup never completed). The provider
    /// probe is a network call — it runs off-thread and updates the snapshot in
    /// place when it lands, so the first frame never waits on the network.
    /// Never throws: detection is existence-tolerant by contract.
    /// </summary>
    /// <param name="ct">Cancellation token (REPL lifetime).</param>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            SetupChecklistSignals signals = await _detector.DetectAsync(ct).ConfigureAwait(false);
            Update(next =>
            {
                next[SetupTaskIds.ConfigFile] = signals.ConfigFileExists;
                next[SetupTaskIds.ProviderKey] = signals.ProviderKeyStored;
                next[SetupTaskIds.Workspace] = signals.WorkspaceResolved;
            });

            // First run: setup has never completed, so open the checklist once.
            if (!await _detector.HasCompletedSetupAsync(ct).ConfigureAwait(false))
            {
                _gateArmed = true;
                Overlay.Show(CurrentModel);
                RequestFrame();
            }

            // Provider probe off the startup path. ProbeProviderHealthAsync
            // awaits the probe and can only return a bool (every failure path is
            // guarded inside the detector), so nothing is dropped on the floor.
            _ = Task.Run(() => ProbeProviderHealthAsync(ct), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // REPL is shutting down — nothing to set up.
        }
        catch (Exception ex)
        {
            // Setup detection is a convenience surface: a failure must never
            // keep the REPL from starting.
            _host.Log.LogWarning(ex, "Setup checklist detection failed — checklist stays empty");
        }
    }

    /// <summary>
    /// First prompt left the composer — flips the "first prompt sent" task.
    /// Cheap, idempotent and safe from any thread: an unchanged snapshot is a
    /// no-op publish.
    /// </summary>
    internal void MarkPromptSent() => Update(next => next[SetupTaskIds.FirstPrompt] = true);

    /// <summary>
    /// Modal key routing for the checklist. Returns true when the key was
    /// consumed (it dismissed the overlay, or the modal swallowed it) — the
    /// input loop then skips the composer, so nothing leaks behind the
    /// checklist. A dismissal also disarms the first-run gate.
    /// </summary>
    /// <param name="key">Decoded key event.</param>
    internal bool HandleKey(in KeyEvent key)
    {
        if (!Overlay.Visible)
        {
            return false;
        }

        bool consumed = Overlay.HandleKey(key);
        if (!Overlay.Visible)
        {
            _gateArmed = false;
        }

        return consumed;
    }

    private SetupChecklistModel CurrentModel => SetupChecklistModel.Empty.WithCompletion(
        Volatile.Read(ref _completion));

    private async Task ProbeProviderHealthAsync(CancellationToken ct)
    {
        bool passed = await _detector.CheckProviderHealthAsync(ct).ConfigureAwait(false);
        Update(next => next[SetupTaskIds.ProviderHealth] = passed);
    }

    /// <summary>
    /// Applies task updates to the completion snapshot and republishes the
    /// model. A no-op update (the state is already what is being set) publishes
    /// nothing and asks for no frame — repeated detections stay free.
    ///
    /// An armed first-run gate keeps the auto-opened modal current as tasks
    /// land; once the user has dismissed it the gate is cleared and a later
    /// completion only updates the model — the modal never re-traps them in
    /// the same session.
    /// </summary>
    private void Update(Action<Dictionary<string, bool>> mutate)
    {
        lock (_completionGate)
        {
            var next = new Dictionary<string, bool>(_completion, StringComparer.OrdinalIgnoreCase);
            mutate(next);

            bool changed = false;
            foreach (KeyValuePair<string, bool> pair in next)
            {
                if (!_completion.TryGetValue(pair.Key, out bool current) || current != pair.Value)
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                return;
            }

            Volatile.Write(ref _completion, next);
            Overlay.SetModel(CurrentModel);
            if (_gateArmed)
            {
                Overlay.Show(CurrentModel);
            }
        }

        if (Overlay.Visible)
        {
            RequestFrame();
        }
    }

    private static Dictionary<string, bool> BuildInitialCompletion()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in SetupTaskIds.All)
        {
            map[id] = false;
        }

        return map;
    }

    /// <summary>
    /// Asks for a repaint of the checklist box only: the rect is staged for the
    /// frame loop (the diff engine's hint list is render-thread owned) and the
    /// dirty sequence wakes the frame gate, so the next flush rescans the modal
    /// rect instead of the whole screen.
    /// </summary>
    private void RequestFrame()
    {
        int cols = Math.Max(0, _host.ScreenSession.CurrentCols);
        int rows = Math.Max(0, _host.ScreenSession.CurrentRows);
        if (cols > 0 && rows > 0)
        {
            Rect box = Overlay.ComputeBox(new Rect(0, 0, cols, rows));
            if (box.Width > 0 && box.Height > 0)
            {
                _host._setupChecklistDamage = box;
                _host._setupChecklistDamagePending = true;
            }
        }

        Interlocked.Increment(ref _host._frameDirtySeq);
        _host._wake.Writer.TryWrite(null);
    }
}
