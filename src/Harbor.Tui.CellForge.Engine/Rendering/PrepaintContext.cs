// #2361: per-frame prepaint context and the cache that survives frames.
// Entries are keyed by ElementId (stable path + folded prefix hash) and guarded
// by a caller-supplied version key. #2353's Scene supplies the display-list
// range: a node records its ops once on a miss, later frames replay that range.
// BCL-only, AOT-compatible, render-thread owned.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Drives the prepaint phase of an <see cref="INode"/> tree. The record cache
/// and <see cref="Scene"/> live across frames: <see cref="BeginFrame"/> starts
/// a pass, <see cref="EndFrame"/> drops entries whose id was not touched, so a
/// node that moves to a different id gets a fresh record instead of a stale
/// one. The scene is append-only within a context; use <see cref="Reset"/> for
/// a full invalidation.
/// </summary>
public sealed class PrepaintContext
{
    private readonly Dictionary<ElementId, Entry> _entries = [];
    private readonly HashSet<ElementId> _touched = [];
    private readonly List<string> _reads = [];
    private readonly Scene _scene = new();

    /// <summary>Persistent display list. Misses append ops; hits replay a stored range.</summary>
    public Scene Scene => _scene;

    /// <summary>Cache hits since construction or the last <see cref="Reset"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Records computed since construction or the last <see cref="Reset"/>.</summary>
    public int Misses { get; private set; }

    /// <summary>Number of live cache entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Starts a frame. The scene is kept: cached ranges must stay valid.</summary>
    public void BeginFrame()
    {
        _touched.Clear();
        _reads.Clear();
    }

    /// <summary>
    /// Finishes a frame: entries whose <see cref="ElementId"/> was not touched
    /// are removed, which is how a changed id resets its state.
    /// </summary>
    public void EndFrame()
    {
        if (_entries.Count == 0)
        {
            _touched.Clear();
            return;
        }

        List<ElementId>? stale = null;
        foreach (var id in _entries.Keys)
        {
            if (!_touched.Contains(id))
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is not null)
        {
            for (int i = 0; i < stale.Count; i++)
            {
                _entries.Remove(stale[i]);
            }
        }

        _touched.Clear();
    }

    /// <summary>
    /// Reuses a record when the id and the caller's version key both match.
    /// </summary>
    public bool TryReuse(ElementId id, ulong key, out PrepaintRecord record)
    {
        if (_entries.TryGetValue(id, out var entry) && entry.Key == key)
        {
            record = entry.Record;
            _touched.Add(id);
            Hits++;
            return true;
        }

        record = default;
        return false;
    }

    /// <summary>Stores a freshly prepared record and returns it unchanged.</summary>
    public PrepaintRecord Commit(ElementId id, ulong key, PrepaintRecord record)
    {
        _entries[id] = new Entry(key, record);
        _touched.Add(id);
        Misses++;
        return record;
    }

    /// <summary>Starts collecting the entities a node reads during prepaint.</summary>
    public void BeginReads() => _reads.Clear();

    /// <summary>Notes an entity read by the current node's prepaint.</summary>
    public void MarkRead(string entity)
    {
        ArgumentException.ThrowIfNullOrEmpty(entity);
        _reads.Add(entity);
    }

    /// <summary>Returns and clears the entities collected since <see cref="BeginReads"/>.</summary>
    public string[] TakeReads()
    {
        var reads = _reads.ToArray();
        _reads.Clear();
        return reads;
    }

    /// <summary>Full invalidation: cache, counters and display list are cleared.</summary>
    public void Reset()
    {
        _entries.Clear();
        _touched.Clear();
        _reads.Clear();
        _scene.Clear();
        Hits = 0;
        Misses = 0;
    }

    private readonly record struct Entry(ulong Key, PrepaintRecord Record);
}

/// <summary>
/// One frame of the node pipeline: measure, arrange, prepaint, paint. Keeping
/// the order in one place is what makes prepaint a phase rather than an
/// optional extra call, and gives the chat layer a single entry point to adopt
/// later without changing its own panel solver in the same step.
/// </summary>
public static class NodeFrame
{
    /// <summary>Runs the four phases over <paramref name="root"/>.</summary>
    public static void Render(
        INode root,
        PrepaintContext context,
        ScreenBuffer buffer,
        Constraints constraints,
        Rect screen)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(buffer);

        _ = root.Measure(constraints);
        root.Arrange(screen);
        context.BeginFrame();
        _ = root.Prepaint(context, ElementId.Root);
        root.Paint(buffer, screen);
        context.EndFrame();
    }
}
