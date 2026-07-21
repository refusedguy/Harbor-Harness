// Storage layer — in-memory store, primarily for tests. See IScriptStore.cs for layering rules.
namespace Harbor.Scripting.Storage;
/// <summary>
///     <see cref="IScriptStore" /> backed by an in-memory dictionary. Intended
///     for unit tests and ephemeral REPL sessions; not persistent.
/// </summary>
public sealed class InMemoryScriptStore : IScriptStore
{
    private readonly Dictionary<string, ScriptEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    ///     Construct an empty store, optionally seeded with the supplied scripts.
    /// </summary>
    public InMemoryScriptStore(IEnumerable<KeyValuePair<string, string>>? seed = null)
    {
        if (seed is not null)
        {
            foreach (var kv in seed)
            {
                _entries[kv.Key] = MakeEntry(kv.Key, kv.Value);
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ScriptEntry>>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = new List<ScriptEntry>(_entries.Count);
            foreach (var kv in _entries)
            {
                list.Add(kv.Value);
            }
            list.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return Task.FromResult(Result.Success<IReadOnlyList<ScriptEntry>>(list));
        }
    }

    /// <inheritdoc />
    public Task<Result<ScriptEntry>> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                return Task.FromResult(Result.Success(entry));
            }
            return Task.FromResult(Result.Failure<ScriptEntry>($"Script '{name}' not found."));
        }
    }

    /// <inheritdoc />
    public Task<Result> WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(Result.Failure("Script name is empty."));
        }
        lock (_lock)
        {
            _entries[name] = MakeEntry(name, content);
        }
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_entries.Remove(name))
            {
                return Task.FromResult(Result.Success());
            }
            return Task.FromResult(Result.Failure($"Script '{name}' not found."));
        }
    }

    private static ScriptEntry MakeEntry(string name, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        return new ScriptEntry(
            name,
            $"inmemory://{name}",
            content,
            Convert.ToHexString(hash),
            DateTimeOffset.UtcNow);
    }
}
