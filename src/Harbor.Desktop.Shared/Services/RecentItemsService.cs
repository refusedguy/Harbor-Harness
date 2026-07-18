using System.IO;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Shared.Services;

/// <summary>
///     Most-recently-used items service for the command palette and the file
///     recent-items menu. Persists to <c>~/.harbor/recent.json</c>.
/// </summary>
public sealed class RecentItemsService
{
    private readonly ILogger<RecentItemsService> _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly List<string> _items = new();
    private readonly int _maxItems;

    /// <summary>Construct a <see cref="RecentItemsService"/>.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="maxItems">Max items to retain. Default 50.</param>
    /// <param name="filePath">
    ///     Optional override for the persistence path. Defaults to
    ///     <c>~/.harbor/recent.json</c>. Test code can pass a temp path.
    /// </param>
    public RecentItemsService(ILogger<RecentItemsService> logger, int maxItems = 50, string? filePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxItems = maxItems > 0 ? maxItems : 50;
        _filePath = filePath ?? DefaultPath();
        Load();
    }

    /// <summary>Default persistence path: <c>~/.harbor/recent.json</c>.</summary>
    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".harbor", "recent.json");
    }

    /// <summary>Snapshot of the current MRU list, most-recent first.</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate) return _items.ToArray();
        }
    }

    /// <summary>Push <paramref name="item"/> to the front of the MRU list.</summary>
    public void Add(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        lock (_gate)
        {
            _items.Remove(item);
            _items.Insert(0, item);
            while (_items.Count > _maxItems) _items.RemoveAt(_items.Count - 1);
        }
        Save();
    }

    /// <summary>Remove <paramref name="item"/> from the MRU list (e.g. file deleted).</summary>
    public void Remove(string item)
    {
        lock (_gate) _items.Remove(item);
        Save();
    }

    /// <summary>Clear all items.</summary>
    public void Clear()
    {
        lock (_gate) _items.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (items is null) return;
            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent items from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            List<string> snapshot;
            lock (_gate) snapshot = new List<string>(_items);
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save recent items to {Path}", _filePath);
        }
    }
}
