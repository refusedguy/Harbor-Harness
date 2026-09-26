using Microsoft.Extensions.Logging;
using Harbor.Ui.Framework.Diagnostics;

namespace Harbor.Tui.CellForge.Diagnostics;

public sealed class DiagnosticsPanel : IDiagnosticsPanel
{
    public const int DefaultCapacity = 1000;
    private readonly Queue<DiagnosticEntry> _entries = new(DefaultCapacity);
    private readonly object _gate = new();

    public DiagnosticsPanel(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            capacity = DefaultCapacity;
        Capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public int Capacity { get; }

    public void Log(LogLevel level, string category, string message)
    {
        if (string.IsNullOrEmpty(category))
            category = string.Empty;
        if (message is null)
            message = string.Empty;

        var entry = new DiagnosticEntry(DateTimeOffset.UtcNow, level, category, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100)
    {
        if (max <= 0)
            return Array.Empty<DiagnosticEntry>();

        DiagnosticEntry[] snapshot;
        lock (_gate)
        {
            if (_entries.Count == 0)
                return Array.Empty<DiagnosticEntry>();
            snapshot = _entries.ToArray();
        }

        int start = Math.Max(0, snapshot.Length - max);
        int count = snapshot.Length - start;
        if (start == 0 && count == snapshot.Length)
            return snapshot;
        var result = new DiagnosticEntry[count];
        Array.Copy(snapshot, start, result, 0, count);
        return result;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void ReportInputError(string message) => Log(LogLevel.Error, "CellForge.InputParser", message);

    public void ReportDiffIssue(string message) => Log(LogLevel.Warning, "CellForge.DiffEngine", message);

    public void ReportLayoutCacheMiss(string message) => Log(LogLevel.Warning, "CellForge.LayoutCache", message);
}
