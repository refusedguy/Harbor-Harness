using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     The mutable state of one <see cref="PluginHost"/> (#2607): the loaded,
///     pending, defined and running plugins, the inventory ledger, and the
///     gate that serializes lifecycle operations.
/// </summary>
/// <remarks>
///     <para>
///         The host compiles, instantiates, registers and runs plugins, and
///         makes the decisions. This class holds only what those decisions
///         produced. Every collection is private and every access goes
///         through a member here, under the same lock the host used before
///         the extraction.
///     </para>
///     <para>
///         This is a mechanical extraction. The state, its lock and the
///         lifecycle gate moved here unchanged, so the host's behavior and
///         public surface stay the same.
///     </para>
/// </remarks>
internal sealed class PluginStateStore
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    // #1733: `_active` holds the plugins that are initialized and registered;
    // `_pending` holds the ones loaded and waiting for a dependency.
    // `_activeNames` is what a plugin-to-plugin dependency resolves against.
    private readonly List<LoadedPlugin> _active = [];
    private readonly HashSet<string> _activeNames = new(StringComparer.Ordinal);
    private readonly List<PendingEntry> _pending = [];

    // #1737: `_definitions` is what each plugin IS, the immutable package
    // that outlives the run. `_runs` is its live activation.
    private readonly Dictionary<string, LoadedPlugin> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginRun> _runs = new(StringComparer.Ordinal);

    // #1722: the ledger the live composition is projected from. One entry per
    // plugin the host met, with the phase it reached.
    private readonly List<PluginInventoryEntry> _inventory = [];

    /// <summary>
    ///     A loaded plugin that is waiting, and the dependencies it is waiting for.
    /// </summary>
    internal sealed class PendingEntry
    {
        internal PendingEntry(LoadedPlugin plugin, IReadOnlyList<string> unsatisfied)
        {
            Plugin = plugin;
            Unsatisfied = unsatisfied;
        }

        /// <summary>The loaded plugin.</summary>
        internal LoadedPlugin Plugin { get; }

        /// <summary>The dependency names that are not satisfied yet.</summary>
        internal IReadOnlyList<string> Unsatisfied { get; set; }
    }

    /// <summary>Snapshot of the plugins that are loaded and activated.</summary>
    internal IReadOnlyList<LoadedPlugin> ActivePlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _active];
            }
        }
    }

    /// <summary>Snapshot of the plugins that are loaded but not activated.</summary>
    internal IReadOnlyList<PendingPlugin> PendingPlugins
    {
        get
        {
            lock (_sync)
            {
                var pending = new List<PendingPlugin>(_pending.Count);
                foreach (PendingEntry entry in _pending)
                {
                    pending.Add(new PendingPlugin(
                        entry.Plugin.Name,
                        entry.Plugin.Version,
                        entry.Unsatisfied));
                }

                return pending;
            }
        }
    }

    /// <summary>Snapshot of the inventory ledger entries, in the order met.</summary>
    internal IReadOnlyList<PluginInventoryEntry> InventoryEntries
    {
        get
        {
            lock (_sync)
            {
                return [.. _inventory];
            }
        }
    }

    /// <summary>Snapshot of the definitions the host knows about.</summary>
    internal IReadOnlyList<LoadedPlugin> Definitions
    {
        get
        {
            lock (_sync)
            {
                return [.. _definitions.Values];
            }
        }
    }

    /// <summary>Names of the live runs, in insertion order.</summary>
    internal string[] RunNames
    {
        get
        {
            lock (_sync)
            {
                return _runs.Keys.ToArray();
            }
        }
    }

    /// <summary>Snapshot of the pending entries, for one activation pass.</summary>
    internal IReadOnlyList<PendingEntry> PendingEntries
    {
        get
        {
            lock (_sync)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>Record a plugin as waiting for the named dependencies.</summary>
    internal void AddPending(LoadedPlugin plugin, IReadOnlyList<string> unsatisfied)
    {
        lock (_sync)
        {
            _pending.Add(new PendingEntry(plugin, unsatisfied));
        }
    }

    /// <summary>Refresh the reasons a pending plugin is waiting.</summary>
    internal void UpdatePending(PendingEntry entry, IReadOnlyList<string> unsatisfied)
    {
        lock (_sync)
        {
            entry.Unsatisfied = unsatisfied;
        }
    }

    /// <summary>Forget one pending entry after it activated or failed.</summary>
    internal void RemovePending(PendingEntry entry)
    {
        lock (_sync)
        {
            _pending.Remove(entry);
        }
    }

    /// <summary>Whether a plugin with this name is active in this load set.</summary>
    internal bool IsActiveName(string name)
    {
        lock (_sync)
        {
            return _activeNames.Contains(name);
        }
    }

    /// <summary>Whether a run is live under this name.</summary>
    internal bool HasRun(string name)
    {
        lock (_sync)
        {
            return _runs.ContainsKey(name);
        }
    }

    /// <summary>
    ///     Whether the incoming plugin is the run already live under its name,
    ///     so an unchanged source can be left alone (#1737).
    /// </summary>
    internal bool IsUnchangedRun(LoadedPlugin plugin)
    {
        lock (_sync)
        {
            return _runs.ContainsKey(plugin.Name)
                   && _definitions.TryGetValue(plugin.Name, out LoadedPlugin? current)
                   && current is not null
                   && string.Equals(current.SourceHash, plugin.SourceHash, StringComparison.Ordinal);
        }
    }

    /// <summary>Get a definition by name.</summary>
    internal bool TryGetDefinition(string name, out LoadedPlugin? definition)
    {
        lock (_sync)
        {
            return _definitions.TryGetValue(name, out definition);
        }
    }

    /// <summary>
    ///     Take the live run out under a name, together with its definition.
    ///     The caller stops the run outside the lock, then calls
    ///     <see cref="FinishStop"/>.
    /// </summary>
    internal bool TryTakeRun(string name, out IPluginRun? run, out LoadedPlugin? definition)
    {
        lock (_sync)
        {
            if (!_runs.TryGetValue(name, out run) || run is null)
            {
                run = null;
                definition = null;
                return false;
            }

            _runs.Remove(name);
            definition = _definitions.TryGetValue(name, out LoadedPlugin? found) ? found : null;
            return true;
        }
    }

    /// <summary>Drop the active markers of a run that was stopped.</summary>
    internal void FinishStop(string name, LoadedPlugin? definition)
    {
        lock (_sync)
        {
            if (definition is not null)
            {
                _active.Remove(definition);
            }

            _activeNames.Remove(name);
        }
    }

    /// <summary>Record the state produced by a successful registration.</summary>
    internal void RecordRun(LoadedPlugin plugin, IPluginRun run)
    {
        lock (_sync)
        {
            _active.Add(plugin);
            _activeNames.Add(plugin.Name);
            _definitions[plugin.Name] = plugin;
            _runs[plugin.Name] = run;
        }
    }

    /// <summary>Forget a definition; the live run, if any, is untouched.</summary>
    internal void RemoveDefinition(string name)
    {
        lock (_sync)
        {
            _definitions.Remove(name);
        }
    }

    /// <summary>Add an entry to the ledger.</summary>
    internal void RecordInventory(PluginInventoryEntry entry)
    {
        lock (_sync)
        {
            _inventory.Add(entry);
        }
    }

    /// <summary>Replace the ledger entry for the same plugin, or add it.</summary>
    internal void ReplaceInventory(PluginInventoryEntry entry)
    {
        lock (_sync)
        {
            for (int i = 0; i < _inventory.Count; i++)
            {
                if (SamePlugin(_inventory[i], entry))
                {
                    _inventory[i] = entry;
                    return;
                }
            }

            _inventory.Add(entry);
        }
    }

    /// <summary>Forget every ledger entry with this plugin name.</summary>
    internal void RemoveInventory(string name)
    {
        lock (_sync)
        {
            _inventory.RemoveAll(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
        }
    }

    /// <summary>
    ///     Whether two entries describe the same plugin: the same source file
    ///     and the same plugin name inside it.
    /// </summary>
    private static bool SamePlugin(PluginInventoryEntry left, PluginInventoryEntry right) =>
        string.Equals(left.SourcePath, right.SourcePath, StringComparison.Ordinal)
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    /// <summary>Wait for the gate that serializes lifecycle operations.</summary>
    internal Task WaitLifecycleAsync(CancellationToken ct) => _lifecycle.WaitAsync(ct);

    /// <summary>Release the lifecycle gate held by the caller.</summary>
    internal void ReleaseLifecycle() => _lifecycle.Release();
}
