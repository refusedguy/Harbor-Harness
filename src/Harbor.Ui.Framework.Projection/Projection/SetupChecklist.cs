namespace Harbor.Ui.Framework.Projection;

// KILLER_FEATURES §2.7 Feature 9 (Orca `SetupGuideProgressRing.tsx` +
// `SetupGuideModal.tsx`), issue #23 checklist box, issue #383: pure setup-guide
// model. Five stable setup tasks with a done/pending state, a `done/total`
// progress text and a 0..1 ratio the renderers feed straight into the
// cell-native `GaugeBar` (CellForge) or a ring/gauge of their own.
//
// Immutable-on-update by contract: every state change returns a NEW instance
// (`WithCompletion` / `WithTask`), so the host thread never mutates a snapshot
// the render thread is painting — publish by reference swap and readers stay
// safe without a lock. Zero Harbor dependencies, zero rendering — any host
// (CellForge overlay, Avalonia flyout, Spectre panel) drives the model.

/// <summary>
/// Stable setup-task ids (Orca setup-guide steps). Ids are part of the public
/// contract: detection and tests key on them, never on the label text.
/// </summary>
public static class SetupTaskIds
{
    /// <summary>A <c>config.json</c> exists for this user.</summary>
    public const string ConfigFile = "config-file";

    /// <summary>At least one provider API key is stored (config or env).</summary>
    public const string ProviderKey = "provider-key";

    /// <summary>The configured provider answered a health probe.</summary>
    public const string ProviderHealth = "provider-health";

    /// <summary>A workspace (working directory) is resolved.</summary>
    public const string Workspace = "workspace";

    /// <summary>The first prompt was sent in this session.</summary>
    public const string FirstPrompt = "first-prompt";

    /// <summary>Canonical paint order — the checklist lists tasks in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ConfigFile, ProviderKey, ProviderHealth, Workspace, FirstPrompt];
}

/// <summary>
/// One setup task as the checklist paints it: stable id, human label and the
/// derived marker. Pure data — no I/O, no rendering.
/// </summary>
/// <param name="Id">Stable task id (see <see cref="SetupTaskIds"/>).</param>
/// <param name="Label">Human-readable step text.</param>
/// <param name="IsDone">True when the task's done-when predicate has passed.</param>
public sealed record SetupTaskState(string Id, string Label, bool IsDone)
{
    /// <summary>Marker for a completed task.</summary>
    public const string DoneMarker = "✓";

    /// <summary>Marker for a pending task.</summary>
    public const string PendingMarker = "○";

    /// <summary><c>✓</c> when done, <c>○</c> otherwise.</summary>
    public string Marker => IsDone ? DoneMarker : PendingMarker;

    /// <summary>Full checklist row: <c>✓ Label</c> / <c>○ Label</c>.</summary>
    public string RowText => $"{Marker} {Label}";
}

/// <summary>
/// Pure setup-guide checklist (KILLER_FEATURES §2.7 Feature 9, issue #383):
/// an ordered task list plus the progress numbers a renderer needs
/// (<see cref="ProgressText" /> for the <c>done/total</c> caption,
/// <see cref="Ratio" /> for the gauge/ring).
///
/// Instances never change after construction — <see cref="WithCompletion" />
/// and <see cref="WithTask" /> return new instances — so the render thread can
/// paint a snapshot while the host publishes the next one.
/// </summary>
public sealed class SetupChecklistModel
{
    /// <summary>The five canonical tasks with their default labels, all pending.</summary>
    public static IReadOnlyList<SetupTaskState> DefaultTasks { get; } = Array.AsReadOnly(
        new[]
        {
            new SetupTaskState(SetupTaskIds.ConfigFile, "Config file created", false),
            new SetupTaskState(SetupTaskIds.ProviderKey, "Provider key stored", false),
            new SetupTaskState(SetupTaskIds.ProviderHealth, "Provider reachable", false),
            new SetupTaskState(SetupTaskIds.Workspace, "Workspace open", false),
            new SetupTaskState(SetupTaskIds.FirstPrompt, "First prompt sent", false),
        });

    /// <summary>Empty checklist over the canonical tasks (nothing completed yet).</summary>
    public static SetupChecklistModel Empty { get; } = new(DefaultTasks);

    private readonly IReadOnlyList<SetupTaskState> _tasks;

    /// <summary>Build a checklist over the supplied tasks (paint order preserved).</summary>
    /// <param name="tasks">Tasks in display order; null/blank ids are skipped.</param>
    public SetupChecklistModel(IEnumerable<SetupTaskState>? tasks)
    {
        var list = new List<SetupTaskState>();
        if (tasks is not null)
        {
            foreach (SetupTaskState task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Id))
                {
                    continue;
                }

                list.Add(task);
            }
        }

        _tasks = Array.AsReadOnly(list.ToArray());
        int done = 0;
        for (int i = 0; i < _tasks.Count; i++)
        {
            done += _tasks[i].IsDone ? 1 : 0;
        }

        TotalCount = _tasks.Count;
        CompletedCount = done;
        Ratio = TotalCount == 0 ? 0d : Math.Clamp((double)done / TotalCount, 0d, 1d);
    }

    /// <summary>Tasks in display order (immutable snapshot).</summary>
    public IReadOnlyList<SetupTaskState> Tasks => _tasks;

    /// <summary>Number of tasks in the checklist.</summary>
    public int TotalCount { get; }

    /// <summary>Number of completed tasks.</summary>
    public int CompletedCount { get; }

    /// <summary>Number of pending tasks.</summary>
    public int PendingCount => TotalCount - CompletedCount;

    /// <summary>
    /// Completion ratio clamped to <c>[0,1]</c> — exactly 1 when every task is
    /// done (the ring clamps), 0 for an empty checklist.
    /// </summary>
    public double Ratio { get; }

    /// <summary><c>done/total</c> caption (e.g. <c>3/5</c>).</summary>
    public string ProgressText => $"{CompletedCount}/{TotalCount}";

    /// <summary>True when at least one task exists and all of them are done.</summary>
    public bool IsComplete => TotalCount > 0 && CompletedCount == TotalCount;

    /// <summary>Task by id (ordinal-ignore-case), or null when unknown.</summary>
    public SetupTaskState? Find(string taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return null;
        }

        for (int i = 0; i < _tasks.Count; i++)
        {
            if (string.Equals(_tasks[i].Id, taskId, StringComparison.OrdinalIgnoreCase))
            {
                return _tasks[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a new checklist with the completion snapshot applied: every id
    /// present in <paramref name="completion" /> gets the given state, ids
    /// absent from it keep their current state. Unknown ids are ignored — a
    /// detector for a task this build doesn't know about can never corrupt the
    /// snapshot. Returns <c>this</c> when nothing changes.
    /// </summary>
    /// <param name="completion">Task id → done flag.</param>
    public SetupChecklistModel WithCompletion(IReadOnlyDictionary<string, bool>? completion)
    {
        if (completion is null || completion.Count == 0)
        {
            return this;
        }

        var next = new SetupTaskState[_tasks.Count];
        bool changed = false;
        for (int i = 0; i < _tasks.Count; i++)
        {
            SetupTaskState task = _tasks[i];
            bool done = task.IsDone;
            if (TryGet(completion, task.Id, out bool requested) && requested != done)
            {
                done = requested;
                changed = true;
            }

            next[i] = done == task.IsDone ? task : task with { IsDone = done };
        }

        return changed ? new SetupChecklistModel(next) : this;
    }

    /// <summary>
    /// Returns a new checklist with one task flipped; <c>this</c> when the
    /// state is already the requested one or the id is unknown, so repeated
    /// detections are free.
    /// </summary>
    /// <param name="taskId">Stable task id.</param>
    /// <param name="isDone">New completion state.</param>
    public SetupChecklistModel WithTask(string taskId, bool isDone)
    {
        SetupTaskState? current = Find(taskId);
        if (current is null || current.IsDone == isDone)
        {
            return this;
        }

        var next = new SetupTaskState[_tasks.Count];
        for (int i = 0; i < _tasks.Count; i++)
        {
            next[i] = _tasks[i];
        }

        for (int i = 0; i < _tasks.Count; i++)
        {
            if (string.Equals(next[i].Id, taskId, StringComparison.OrdinalIgnoreCase))
            {
                next[i] = next[i] with { IsDone = isDone };
            }
        }

        return new SetupChecklistModel(next);
    }

    /// <summary>
    /// Completion snapshot of the current checklist (task id → done flag) —
    /// the shape <see cref="WithCompletion" /> consumes, and what a host keeps
    /// so detections stay additive across refreshes.
    /// </summary>
    public IReadOnlyDictionary<string, bool> ToCompletion()
    {
        var map = new Dictionary<string, bool>(TotalCount, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _tasks.Count; i++)
        {
            map[_tasks[i].Id] = _tasks[i].IsDone;
        }

        return map;
    }

    private static bool TryGet(IReadOnlyDictionary<string, bool> map, string key, out bool value)
    {
        if (map.TryGetValue(key, out value))
        {
            return true;
        }

        // Hosts may key the snapshot with a different casing than the task id.
        foreach (KeyValuePair<string, bool> pair in map)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = false;
        return false;
    }
}
