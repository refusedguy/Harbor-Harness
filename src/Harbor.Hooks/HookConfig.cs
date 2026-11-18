using System.Collections;

namespace Harbor.Application.Hooks;

/// <summary>
///     One hook entry in <c>~/.harbor/hooks.json</c>: a tool matcher plus the
///     shell command to run when it matches.
/// </summary>
/// <param name="Matcher">
///     Which tools this entry applies to. Null, empty, or <c>"*"</c> matches
///     every tool; otherwise a <c>|</c>-separated list of tool names matched
///     case-insensitively (e.g. <c>"bash|write"</c>). Deliberately equality,
///     not regex — deterministic, no ReDoS, no surprise matches.
/// </param>
/// <param name="Command">
///     Shell command to run (via <c>/bin/sh -c</c>, <c>cmd.exe /c</c> on
///     Windows). Receives the hook payload as JSON on stdin and reports its
///     verdict as JSON on stdout (see <see cref="IHookRunner" />).
/// </param>
/// <param name="TimeoutSeconds">
///     Per-hook ceiling in seconds (Claude parity). Null or non-positive means
///     the runner default (30s); clamped to 300s.
/// </param>
public sealed record HookEntry(
    [property: JsonPropertyName("matcher")] string? Matcher,
    [property: JsonPropertyName("command")] string Command = "",
    [property: JsonPropertyName("timeout")] int? TimeoutSeconds = null);

/// <summary>
///     Root of <c>~/.harbor/hooks.json</c>. Keys under <c>"hooks"</c> are event
///     names (<see cref="HookEvents" />); unknown keys are ignored so a newer
///     file never breaks an older binary.
/// </summary>
/// <param name="Hooks">Event name → hook entries.</param>
public sealed record HookFileConfig(
    [property: JsonPropertyName("hooks")] Dictionary<string, List<HookEntry>>? Hooks);

/// <summary>
///     Loads <c>~/.harbor/hooks.json</c> and answers per-event hook queries.
///     This is NOT a plugin: entries are plain shell commands owned by the
///     user, loaded without Roslyn, without <c>Assembly.Load</c>, without any
///     plugin machinery.
/// </summary>
/// <remarks>
///     The file is re-read only when its mtime moves (a missing file costs one
///     stat per query, never a parse), so per-tool-call <c>PreToolUse</c>
///     queries stay cheap. A corrupt file parses to an empty table and records
///     <see cref="LoadError" /> — the runner logs that warning and runs no
///     hooks, rather than blocking every tool call on a JSON typo.
/// </remarks>
public sealed class HookConfig
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<HookEntry>> EmptyTable =
        new Dictionary<string, IReadOnlyList<HookEntry>>(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _lock = new();
    private bool _loaded;
    private DateTime _writeStamp = DateTime.MinValue;
    private IReadOnlyDictionary<string, IReadOnlyList<HookEntry>> _table = EmptyTable;
    // Written under _lock by the reload. Volatile publishes that write to the
    // public LoadError readers that live outside the lock.
    private volatile string? _loadError;

    /// <summary>Construct a hook config bound to <paramref name="path" />.</summary>
    /// <param name="path">Config path override; defaults to <see cref="GetDefaultPath" />.</param>
    public HookConfig(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    /// <summary>
    ///     The last load failure, if the file exists but could not be parsed.
    ///     Null when the file is absent (no hooks — not an error) or healthy.
    /// </summary>
    /// <remarks>
    ///     Volatile-backed for safe publication from the reload thread. A caller
    ///     that needs the error to match a particular set of hooks must use
    ///     <see cref="GetHooksWithError" />, which reads both under one lock.
    /// </remarks>
    public string? LoadError => _loadError;

    /// <summary>Returns the default config file path (<c>~/.harbor/hooks.json</c>).</summary>
    public static string GetDefaultPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".harbor", "hooks.json");
    }

    /// <summary>
    ///     Entries for <paramref name="eventName" /> whose matcher accepts
    ///     <paramref name="toolName" />. A null <paramref name="toolName" />
    ///     (e.g. <c>SessionEnd</c>, which has no tool) returns every entry.
    /// </summary>
    public IReadOnlyList<HookEntry> GetHooks(string eventName, string? toolName) =>
        GetHooksWithError(eventName, toolName).Entries;

    /// <summary>
    ///     Hooks and the load error from the SAME reload, read under one lock.
    ///     The runner uses this so a concurrent reload cannot swap the error
    ///     between the hook lookup and the error read (the old pair of calls
    ///     released <c>_lock</c> in between and could log a stale warning).
    /// </summary>
    internal HookLookup GetHooksWithError(string eventName, string? toolName)
    {
        lock (_lock)
        {
            IReadOnlyDictionary<string, IReadOnlyList<HookEntry>> table = GetTableCore();
            if (!table.TryGetValue(eventName, out var entries))
            {
                return new HookLookup([], _loadError);
            }

            if (toolName is null)
            {
                return new HookLookup(entries, _loadError);
            }

            List<HookEntry>? matched = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (Matches(entries[i].Matcher, toolName))
                {
                    matched ??= new List<HookEntry>();
                    matched.Add(entries[i]);
                }
            }

            return new HookLookup(matched ?? (IReadOnlyList<HookEntry>)[], _loadError);
        }
    }

    /// <summary>Hooks and the load error observed by one locked reload.</summary>
    internal readonly record struct HookLookup(IReadOnlyList<HookEntry> Entries, string? LoadError);

    /// <summary>
    ///     Matcher predicate, factored out so tests can pin it without a file.
    ///     Null/empty/<c>"*"</c> matches all; otherwise <c>|</c>-separated
    ///     case-insensitive tool-name equality.
    /// </summary>
    public static bool Matches(string? matcher, string toolName)
    {
        if (string.IsNullOrWhiteSpace(matcher))
        {
            return true;
        }

        string trimmed = matcher.Trim();
        if (trimmed == "*")
        {
            return true;
        }

        int start = 0;
        for (int i = 0; i <= trimmed.Length; i++)
        {
            if (i == trimmed.Length || trimmed[i] == '|')
            {
                if (trimmed.Substring(start, i - start).Trim().Equals(toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                start = i + 1;
            }
        }

        return false;
    }

    // Called only while _lock is held (from GetHooksWithError). Returns the
    // current table, reloading from disk when the file's stamp has moved.
    private IReadOnlyDictionary<string, IReadOnlyList<HookEntry>> GetTableCore()
    {
        DateTime stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (_loaded && stamp == _writeStamp)
        {
            return _table;
        }

        _table = LoadCore();
        _writeStamp = stamp;
        _loaded = true;
        return _table;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<HookEntry>> LoadCore()
    {
        _loadError = null;
        if (!File.Exists(_path))
        {
            return EmptyTable;
        }

        HookFileConfig? file;
        try
        {
            string json = File.ReadAllText(_path);
            file = JsonSerializer.Deserialize(json, HookJsonContext.Default.HookFileConfig);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            _loadError = $"Cannot parse '{_path}': {ex.Message}";
            return EmptyTable;
        }

        if (file?.Hooks is null)
        {
            return EmptyTable;
        }

        var table = new Dictionary<string, IReadOnlyList<HookEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in file.Hooks)
        {
            if (pair.Value is null)
            {
                continue;
            }

            var clean = new List<HookEntry>(pair.Value.Count);
            for (int i = 0; i < pair.Value.Count; i++)
            {
                // A blank command is a typo, not a hook: drop it rather than
                // spawning an empty shell for every matching tool call.
                if (pair.Value[i] is not null && !string.IsNullOrWhiteSpace(pair.Value[i].Command))
                {
                    clean.Add(pair.Value[i]);
                }
            }

            table[pair.Key] = clean;
        }

        return table;
    }
}
