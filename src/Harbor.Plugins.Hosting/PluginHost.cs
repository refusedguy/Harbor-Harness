using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Registration;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Hosting;
/// <summary>
///     Composition root for the layered plugin runtime. Iterates the supplied
///     <see cref="IPluginSource" />, compiles each script via <see cref="IPluginCompiler" />,
///     instantiates <see cref="Harbor.Abstractions.Plugins.IPlugin" /> types via
///     <see cref="IPluginInstantiator" />, and wires them into the host via
///     <see cref="IPluginRegistrar" />.
/// </summary>
/// <remarks>
///     <para>
///         The host holds no mutable collections of its own (#2607): the load set,
///         the inventory ledger and the lifecycle gate live in
///         <see cref="PluginStateStore" />. The host orchestrates the layers and
///         keeps its constructor-injected dependencies, so it stays testable
///         with in-memory doubles.
///     </para>
///     <para>
///         Failures at any stage are logged. Whether they abort the run or not depends
///         on <see cref="PluginHostOptions.ContinueOnError" /> (default: continue).
///     </para>
///     <para>
///         <b>Deferred activation (#1733).</b> A plugin may declare what it needs
///         (<see cref="Harbor.Abstractions.Plugins.IPlugin.Dependencies" />). While a declared
///         dependency is not satisfied the plugin is LOADED but NOT ACTIVATED: it is known to
///         the host and listed in <see cref="PendingPlugins" />, but it has not been
///         initialized and has contributed nothing. It is activated as soon as its
///         dependencies are met — <see cref="ActivatePending" /> is the trigger, and the load
///         itself runs one activation pass at the end, so a plugin that depends on another
///         plugin loaded in the same pass comes up with it.
///     </para>
///     <para>
///         <b>The manifest gate (#1739).</b> A plugin may ship a manifest next to its
///         source, and the manifest is read as DATA — before the plugin is compiled. A
///         manifest the host cannot read, and a manifest that asks for a Harbor newer
///         than the one running, are both refused here, with the reason. A plugin with
///         no manifest is not affected: it compiles and registers exactly as before.
///     </para>
///     <para>
///         <b>The live composition (#1722).</b> The host keeps a ledger of every plugin
///         it met — the ones that loaded, the ones that are waiting for a dependency,
///         and the ones it refused — and projects it as <see cref="Inventory" />: a
///         machine-readable answer to "why is my tool not there?". The ledger is a
///         projection of the state the host already keeps, not a second lifecycle:
///         there is no way for the inventory to disagree with the host about a
///         plugin's phase.
///     </para>
/// </remarks>
public sealed class PluginHost
{
    private readonly IPluginCompiler _compiler;
    private readonly IPluginDependencyResolver? _dependencies;
    private readonly IPluginInstantiator _instantiator;
    private readonly ILogger<PluginHost> _logger;
    private readonly PluginHostOptions _options;
    private readonly IPluginRegistrar _registrar;
    private readonly IPluginSource _source;

    // #2607: the load set, the ledger and the lifecycle gate are one
    // responsibility. PluginStateStore owns them. The host keeps the
    // decisions and the orchestration; the store keeps the state they produce.
    private readonly PluginStateStore _state = new();

    /// <summary>
    ///     Construct a new plugin host.
    /// </summary>
    /// <param name="source">Where plugin scripts come from.</param>
    /// <param name="compiler">CS → Assembly compiler.</param>
    /// <param name="instantiator">Assembly → live IPlugin instantiator.</param>
    /// <param name="registrar">Live IPlugin → host registration sink.</param>
    /// <param name="options">Host options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public PluginHost(
        IPluginSource source,
        IPluginCompiler compiler,
        IPluginInstantiator instantiator,
        IPluginRegistrar registrar,
        PluginHostOptions options,
        ILogger<PluginHost> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dependencies = options.Dependencies;
    }

    /// <summary>
    ///     The plugins that are loaded AND activated — initialized, with their
    ///     contributions registered.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> ActivePlugins => _state.ActivePlugins;

    /// <summary>
    ///     The plugins that are loaded but NOT activated, and the dependencies each of them
    ///     is waiting for (#1733).
    /// </summary>
    /// <remarks>
    ///     A pending plugin contributes nothing: no tool, no provider, no agent, no panel. It
    ///     is listed here so the state is visible to whoever asks the host, and the host logs
    ///     it too — a plugin that waits is a fact the user can be told, not a silence.
    /// </remarks>
    public IReadOnlyList<PendingPlugin> PendingPlugins => _state.PendingPlugins;

    /// <summary>
    ///     The live plugin composition, as one machine-readable document (#1722).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The snapshot says, for every plugin the host met: the phase it
    ///         reached (pending, active or failed), the extension axes it carries,
    ///         its version, the dependencies it declared, and what each of its
    ///         registrations did in the host.
    ///     </para>
    ///     <para>
    ///         It is a projection: the host fills the ledger in as the load
    ///         proceeds, and this property renders it. Nothing is cached and
    ///         nothing is decided here — the phase of a plugin is the phase the
    ///         host gave it.
    ///     </para>
    /// </remarks>
    public PluginInventory Inventory => new(_options.RouteId, _state.InventoryEntries);

    /// <summary>
    ///     The definitions the host knows about (#1737) — the plugins it has met,
    ///     whether they are running or not.
    /// </summary>
    /// <remarks>
    ///     A definition is what a plugin IS; the run is what it is DOING. A stopped
    ///     plugin is still defined: that is the difference between "stopped" and
    ///     "forgotten", and it is why the definition outlives the run.
    /// </remarks>
    public IReadOnlyList<LoadedPlugin> Definitions => _state.Definitions;

    /// <summary>
    ///     Load every script produced by the source, compiling + instantiating +
    ///     registering each in turn. The host log surfaces per-plugin failures; the
    ///     returned <see cref="Result" /> is failure only if the run itself aborted
    ///     (e.g. ContinueOnError=false and a plugin threw).
    /// </summary>
    /// <param name="host">The host registration sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success with the list of <see cref="LoadedPlugin" />s that were successfully
    ///     registered, or failure with an error message. A plugin that is waiting for a
    ///     dependency is NOT in that list — it is in <see cref="PendingPlugins" />.
    /// </returns>
    public async Task<Result<IReadOnlyList<LoadedPlugin>>> LoadAllAsync(
        IPluginLoadHost host,
        CancellationToken ct = default)
    {
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        await foreach (var script in _source.GetScriptsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // #1739: the manifest is read as data, before the plugin is built, so
            // a plugin that cannot run on this Harbor is refused for the price of
            // a JSON read instead of a Roslyn compile.
            string? refusal = RefuseBeforeCompile(script);
            if (refusal is not null)
            {
                _logger.LogWarning("Plugin {Path} was not loaded: {Reason}", script.Path, refusal);
                _state.RecordInventory(RefusedEntry(script, refusal));
                if (FailOrContinue(refusal))
                    return Result.Failure<IReadOnlyList<LoadedPlugin>>(refusal);
                continue;
            }

            var compiled = await _compiler.CompileAsync(script, ct).ConfigureAwait(false);
            if (compiled.IsFailure)
            {
                // #1055 slice 1: a broken plugin warns (with its file name) and the
                // run continues — per-plugin failure is never an error, because the
                // overall load still succeeds under ContinueOnError.
                _logger.LogWarning("Plugin compilation failed for {Path}: {Error}", script.Path, compiled.Error);
                _state.RecordInventory(RefusedEntry(script, compiled.Error));
                if (FailOrContinue(compiled.Error))
                    return compiled.ConvertFailure<IReadOnlyList<LoadedPlugin>>();
                continue;
            }

#pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure guard above is a nested
            // `if (FailOrContinue(...)) return; continue;`, a control-flow shape the
            // analyzer does not model. The .Value is safe. Baseline: #561.
            var instantiated = _instantiator.Instantiate(compiled.Value);
#pragma warning restore CFE0001
            if (instantiated.IsFailure)
            {
                _logger.LogWarning("Plugin instantiation failed for {Path}: {Error}", script.Path, instantiated.Error);
                _state.RecordInventory(RefusedEntry(script, instantiated.Error));
                if (FailOrContinue(instantiated.Error))
                    return instantiated.ConvertFailure<IReadOnlyList<LoadedPlugin>>();
                continue;
            }

            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            foreach (var plugin in instantiated.Value)
            #pragma warning restore CFE0001
            {
                var (outcome, error) = await TryActivateAsync(plugin, host, ct).ConfigureAwait(false);
                if (outcome == ActivationOutcome.Failed)
                {
                    _logger.LogWarning("Plugin registration failed for {DisplayName}: {Error}", plugin.DisplayName, error);
                    if (FailOrContinue(error))
                        return Result.Failure<IReadOnlyList<LoadedPlugin>>(error);
                }
            }
        }

        // #1733: a plugin may depend on another plugin that is loaded LATER in this
        // same pass. One activation pass at the end of the load lets it come up with
        // its dependency instead of waiting for the next trigger.
        ActivatePending(host);

        return Result.Success<IReadOnlyList<LoadedPlugin>>(ActivePlugins);
    }

    /// <summary>
    ///     Re-check the plugins that are waiting for a dependency and activate the ones whose
    ///     dependencies are met now (#1733).
    /// </summary>
    /// <param name="host">The host registration sink.</param>
    /// <returns>
    ///     How many plugins were activated by this pass. Zero means every pending plugin is
    ///     still waiting — for a capability that has not arrived, or for another plugin that
    ///     is itself waiting.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The pass repeats until nothing changes: activating one plugin can satisfy the
    ///         dependency of another. It is a loop over a snapshot, never a recursion, so a
    ///         dependency cycle cannot hang it — the plugins in the cycle simply stay pending
    ///         and the cycle is reported.
    ///     </para>
    ///     <para>
    ///         This is the trigger the composition root calls when the environment changed
    ///         (an MCP client connected, a configuration file appeared). Calling it when
    ///         nothing changed is harmless: it activates nothing and reports the same state.
    ///     </para>
    /// </remarks>
    public int ActivatePending(IPluginLoadHost host)
    {
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        int activated = 0;
        while (true)
        {
            IReadOnlyList<PluginStateStore.PendingEntry> snapshot = _state.PendingEntries;

            if (snapshot.Count == 0)
            {
                break;
            }

            bool progressed = false;
            foreach (PluginStateStore.PendingEntry entry in snapshot)
            {
                IReadOnlyList<string> unmet = UnsatisfiedDependencies(entry.Plugin, out string? malformed);
                if (malformed is not null)
                {
                    // A declaration the host cannot even read is a plugin bug, not a wait.
                    _state.RemovePending(entry);
                    _logger.LogWarning("Plugin registration failed for {DisplayName}: {Error}", entry.Plugin.DisplayName, malformed);
                    _state.ReplaceInventory(Entry(entry.Plugin, PluginInventoryPhase.Failed, [], [], malformed));
                    continue;
                }

                if (unmet.Count > 0)
                {
                    _state.UpdatePending(entry, unmet);
                    _state.ReplaceInventory(Entry(entry.Plugin, PluginInventoryPhase.Pending, unmet, [], null));
                    continue;
                }

                _state.RemovePending(entry);

                // #1737: a pending plugin is DEFINED but has no run. Bringing it up
                // is a plain run, not an update: there is nothing running under
                // its name to replace.
                Result started = StartRunCore(entry.Plugin, host, PluginRunMode.Run);
                if (started.IsFailure)
                {
                    _logger.LogWarning("Plugin registration failed for {DisplayName}: {Error}", entry.Plugin.DisplayName, started.Error);
                    continue;
                }

                activated++;
                progressed = true;
                _logger.LogInformation("Plugin {DisplayName} activated — its declared dependencies are met.", entry.Plugin.DisplayName);
            }

            if (!progressed)
            {
                break;
            }
        }

        ReportPending();
        return activated;
    }

    /// <summary>
    ///     Shared tail for the three per-plugin failure branches above:
    ///     returns <see langword="true" /> when the run must abort
    ///     (<c>ContinueOnError=false</c>), <see langword="false" /> to skip
    ///     the broken plugin and continue with the next. Logging stays at
    ///     the call site (each branch logs its own context).
    /// </summary>
    private bool FailOrContinue(string error)
    {
        _ = error;
        return !_options.ContinueOnError;
    }

    /// <summary>
    ///     The reason a plugin must not be compiled, or <c>null</c> when it may.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two things are refused here, and both are refusals of the
    ///         DECLARATION, not of the code:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 A manifest the host could not read. The plugin shipped a
    ///                 declaration and the host failed to apply it, so the
    ///                 plugin is not loaded — running it would mean running a
    ///                 plugin whose own constraints were dropped.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 A manifest that asks for a Harbor newer than the one
    ///                 running. The check is the same one
    ///                 <see cref="PluginCompatibility" /> applies to the
    ///                 in-code floor, so the manifest and the code cannot
    ///                 disagree about what the rule is.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A plugin with no manifest is not refused here: it is compiled and
    ///         registered exactly as it was before the manifest existed.
    ///     </para>
    /// </remarks>
    /// <param name="script">The discovered plugin source.</param>
    /// <returns>The reason to refuse, or <c>null</c>.</returns>
    private string? RefuseBeforeCompile(PluginScript script)
    {
        if (script.ManifestError is not null)
            return script.ManifestError;

        PluginManifest? manifest = script.Manifest;
        if (manifest is null)
            return null;

        Version? floor = manifest.RequiredHarborVersion;
        if (floor is null)
            return null;

        // The host does not read the running version from anywhere itself: the
        // composition root injects it, because the layer that owns it is a lower
        // plugin layer the host may not bind. Without it the early check is
        // skipped, and the registration gate remains the authoritative one.
        Version? runtime = _options.HarborVersion;
        if (runtime is null)
            return null;

        string name = manifest.Name ?? Path.GetFileName(script.Path);
        var compatibility = PluginCompatibility.Check(name, floor, runtime);
        if (compatibility.IsFailure)
            return compatibility.Error;

        return null;
    }

    /// <summary>
    ///     What happened to a plugin the host tried to bring up.
    /// </summary>
    private enum ActivationOutcome
    {
        /// <summary>Initialized and registered.</summary>
        Activated,

        /// <summary>Loaded, but waiting for a declared dependency.</summary>
        Pending,

        /// <summary>The host refused it (or it declared something unreadable).</summary>
        Failed,

        /// <summary>
        ///     It was already running and its source had not changed, so the live
        ///     run IS this plugin and nothing was done (#1737).
        /// </summary>
        AlreadyRunning,
    }

    /// <summary>
    ///     Bring one plugin up: activate it when its declared dependencies are met, park it
    ///     as pending when they are not, and report a malformed declaration as a failure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The load is IDEMPOTENT (#1737). A plugin whose source did not change is
    ///         left alone: the run that is already live IS that plugin, and running it
    ///         twice would collide with itself. A plugin whose source DID change is an
    ///         update: its old run is stopped and the new one takes its place.
    ///     </para>
    /// </remarks>
    private async Task<(ActivationOutcome Outcome, string Error)> TryActivateAsync(
        LoadedPlugin plugin,
        IPluginLoadHost host,
        CancellationToken ct)
    {
        IReadOnlyList<string> unmet = UnsatisfiedDependencies(plugin, out string? malformed);
        if (malformed is not null)
        {
            _state.RecordInventory(Entry(plugin, PluginInventoryPhase.Failed, [], [], malformed));
            return (ActivationOutcome.Failed, malformed);
        }

        if (unmet.Count > 0)
        {
            _state.AddPending(plugin, unmet);
            _state.RecordInventory(Entry(plugin, PluginInventoryPhase.Pending, unmet, [], null));
            return (ActivationOutcome.Pending, string.Empty);
        }

        if (_state.IsUnchangedRun(plugin))
        {
            return (ActivationOutcome.AlreadyRunning, string.Empty);
        }

        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            Result started = await StartRunCoreAsync(plugin, host, ct).ConfigureAwait(false);
            if (started.IsFailure)
            {
                return (ActivationOutcome.Failed, started.Error);
            }

            return (ActivationOutcome.Activated, string.Empty);
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    // ── The lifecycle operations (#1737) ────────────────────────────────────────

    /// <summary>
    ///     Run a plugin that is defined but not running (#1737).
    /// </summary>
    /// <remarks>
    ///     A plugin that is already running is refused with the reason and the way
    ///     out: stop it first. Two live runs of one plugin would fight over the same
    ///     names.
    /// </remarks>
    /// <param name="name">The plugin's name.</param>
    /// <param name="host">The host registration sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or a failure saying why the plugin did not run.</returns>
    public async Task<Result> RunAsync(string name, IPluginLoadHost host, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.TryGetDefinition(name, out LoadedPlugin? definition) || definition is null)
            {
                return Result.Failure($"No plugin named '{name}' is defined.");
            }

            if (_state.HasRun(name))
            {
                return Result.Failure(
                    $"Plugin '{name}' is already running. Stop it before running it again.");
            }

            return StartRunCore(definition, host, PluginRunMode.Run);
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    /// <summary>
    ///     Replace the running version of a plugin with a new one (#1737).
    /// </summary>
    /// <remarks>
    ///     The old run is stopped FIRST — its registrations are taken back and the
    ///     plugin is shut down — and only then does the new one start. The two never
    ///     hold the same name at the same time, so the new version cannot collide
    ///     with the old one.
    /// </remarks>
    /// <param name="plugin">The new version of the plugin.</param>
    /// <param name="host">The host registration sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or a failure saying why the new version did not run.</returns>
    public async Task<Result> UpdateAsync(
        LoadedPlugin plugin,
        IPluginLoadHost host,
        CancellationToken ct = default)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartRunCoreAsync(plugin, host, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    /// <summary>
    ///     Stop the live run of a plugin: take back exactly what it registered, shut
    ///     it down, and KEEP its definition (#1737).
    /// </summary>
    /// <param name="name">The plugin's name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success when the run is stopped, failure when something could not be taken
    ///     back. A plugin that is not running is not a failure: the state the caller
    ///     wanted already holds.
    /// </returns>
    public async Task<Result> StopAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            return await StopRunCoreAsync(name, ct).ConfigureAwait(false);
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    /// <summary>
    ///     Stop every live run (#1720): take back exactly what each plugin
    ///     registered and call its <c>ShutdownAsync</c>, newest run first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the exit path. <see cref="StopAsync" /> needs a plugin name and
    ///         is the right shape for an operator who wants one plugin down; a host
    ///         that is going away has no such name and must not have to know which
    ///         plugins are live to tear them all down. Without this, the runs the
    ///         host registered at startup are never stopped — their registrations
    ///         outlive the process and <c>ShutdownAsync</c> is never called (#1720).
    ///     </para>
    ///     <para>
    ///         Teardown is best-effort by design. One plugin that refuses to roll back
    ///         must not strand every other plugin's shutdown, so each failure is
    ///         logged here, every run is attempted, and the first failure is returned
    ///         to the caller. A caller that needs the detail reads the log; a caller
    ///         that only needs to know whether everything came down reads the result.
    ///     </para>
    ///     <para>
    ///         The runs are stopped newest first, the mirror of the order in which
    ///         they were started — the same order <c>PluginRun.StopAsync</c> unwinds a
    ///         single plugin's registrations.
    ///     </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success when every run came down, or the first failure encountered while
    ///     stopping them. Stopping is idempotent: with no live runs it succeeds.
    /// </returns>
    public async Task<Result> StopAllAsync(CancellationToken ct = default)
    {
        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            string[] names = _state.RunNames;
            Array.Reverse(names);

            Result firstFailure = Result.Success();
            foreach (string name in names)
            {
                Result stopped = await StopRunCoreAsync(name, ct).ConfigureAwait(false);
                if (stopped.IsFailure)
                {
                    _logger.LogWarning(
                        "Stopping plugin {Name} during teardown failed: {Error}",
                        name,
                        stopped.Error);
                    if (firstFailure.IsSuccess)
                    {
                        firstFailure = stopped;
                    }
                }
            }

            return firstFailure;
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    /// <summary>
    ///     Forget a plugin's definition (#1737).
    /// </summary>
    /// <remarks>
    ///     Undefining is not stopping. The definition goes; the live run does not — a
    ///     plugin that is running is refused, because forgetting a definition while its
    ///     registrations are live would leave a plugin nobody can name and nobody can
    ///     stop.
    /// </remarks>
    /// <param name="name">The plugin's name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or a failure saying why the definition was kept.</returns>
    public async Task<Result> UndefineAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _state.WaitLifecycleAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.HasRun(name))
            {
                return Result.Failure(
                    $"Plugin '{name}' is running. Stop it before forgetting it.");
            }

            _state.RemoveDefinition(name);
            _state.RemoveInventory(name);
            return Result.Success();
        }
        finally
        {
            _state.ReleaseLifecycle();
        }
    }

    /// <summary>
    ///     Start (or replace) the run of a plugin. The caller holds the lifecycle gate.
    /// </summary>
    private async Task<Result> StartRunCoreAsync(
        LoadedPlugin plugin,
        IPluginLoadHost host,
        CancellationToken ct)
    {
        bool replaced = _state.HasRun(plugin.Name);

        if (replaced)
        {
            Result stopped = await StopRunCoreAsync(plugin.Name, ct).ConfigureAwait(false);
            if (stopped.IsFailure)
            {
                return stopped;
            }
        }

        return StartRunCore(plugin, host, replaced ? PluginRunMode.Update : PluginRunMode.Run);
    }

    /// <summary>
    ///     Register the plugin and record the run it created. The caller holds the
    ///     lifecycle gate.
    /// </summary>
    /// <remarks>
    ///     The registration goes through a decorator that remembers what each door
    ///     answered (#1722). The decorator forwards the host's own Result unchanged,
    ///     so the plugin sees the truth and the host behaves exactly as before.
    /// </remarks>
    private Result StartRunCore(LoadedPlugin plugin, IPluginLoadHost host, PluginRunMode mode)
    {
        var recorder = new PluginInventoryLoadHost(host);
        Result<IPluginRun> registered = _registrar.Register(plugin, recorder, mode);
        if (registered is { IsSuccess: true, Value: var run })
        {
            _state.RecordRun(plugin, run);

            _logger.LogInformation("Plugin {DisplayName} is running ({Mode}).", plugin.DisplayName, mode);
            _state.ReplaceInventory(Entry(plugin, PluginInventoryPhase.Active, [], recorder.Registrations, null));
            return Result.Success();
        }

        string error = registered.Error;
        _state.RecordInventory(Entry(plugin, PluginInventoryPhase.Failed, [], recorder.Registrations, error));
        return Result.Failure(error);
    }

    /// <summary>
    ///     Stop the live run of a plugin. The caller holds the lifecycle gate.
    /// </summary>
    private async Task<Result> StopRunCoreAsync(string name, CancellationToken ct)
    {
        if (!_state.TryTakeRun(name, out IPluginRun? run, out LoadedPlugin? definition) || run is null)
        {
            // Nothing is running under that name. The state the caller wanted
            // already holds, so this is not a failure.
            return Result.Success();
        }

        Result stopped = await run.StopAsync(ct).ConfigureAwait(false);

        _state.FinishStop(name, definition);

        if (definition is not null)
        {
            _state.ReplaceInventory(Entry(definition, PluginInventoryPhase.Stopped, [], [], null));
        }

        _logger.LogInformation("Plugin {Name} was stopped.", name);
        return stopped;
    }

    // ── The live composition ledger (#1722) ──────────────────────────────────────

    /// <summary>
    ///     One plugin's entry in the ledger.
    /// </summary>
    /// <param name="plugin">The loaded plugin.</param>
    /// <param name="phase">The phase the plugin reached.</param>
    /// <param name="unsatisfied">The dependencies that are not satisfied yet.</param>
    /// <param name="registrations">What the plugin's doors answered.</param>
    /// <param name="failureReason">Why the host refused it, when it did.</param>
    /// <returns>The entry.</returns>
    private static PluginInventoryEntry Entry(
        LoadedPlugin plugin,
        PluginInventoryPhase phase,
        IReadOnlyList<string> unsatisfied,
        IReadOnlyList<PluginInventoryRegistration> registrations,
        string? failureReason)
    {
        // The axes come from the ONE marker table the registrar dispatches on
        // (`PluginAxisMarkers`), so the inventory cannot claim a plugin carries an
        // axis the registrar never looked for.
        PluginAxisMarkers markers = PluginAxisMarkers.Of(plugin.Instance);
        return new PluginInventoryEntry(
            plugin.Name,
            plugin.Version.ToString(),
            plugin.SourcePath,
            phase,
            markers.Names,
            plugin.Dependencies,
            unsatisfied,
            registrations,
            failureReason,
            plugin.Manifest is not null);
    }

    /// <summary>
    ///     The entry of a plugin the host never got far enough to know: a source
    ///     that did not compile, did not instantiate, or shipped a manifest the host
    ///     could not apply.
    /// </summary>
    /// <param name="script">The discovered plugin source.</param>
    /// <param name="reason">Why the host refused it.</param>
    /// <returns>The entry.</returns>
    private static PluginInventoryEntry RefusedEntry(PluginScript script, string reason) =>
        new(
            Name: string.Empty,
            Version: string.Empty,
            SourcePath: script.Path,
            Phase: PluginInventoryPhase.Failed,
            Axes: [],
            Dependencies: [],
            UnsatisfiedDependencies: [],
            Registrations: [],
            FailureReason: reason,
            ManifestPresent: script.Manifest is not null);

    /// <summary>
    ///     The declared dependencies that are not satisfied yet (#1733).
    /// </summary>
    /// <param name="plugin">The plugin to check.</param>
    /// <param name="malformed">
    ///     Set when the plugin declares something the host cannot read (an empty dependency
    ///     name). The plugin then fails instead of waiting for a name that means nothing.
    /// </param>
    private IReadOnlyList<string> UnsatisfiedDependencies(LoadedPlugin plugin, out string? malformed)
    {
        malformed = null;

        IReadOnlyList<string> declared = plugin.Dependencies;
        if (declared.Count == 0)
        {
            return [];
        }

        List<string> unmet = [];
        foreach (string name in declared)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                malformed = $"Plugin '{plugin.Name}' declares an empty dependency name.";
                return [];
            }

            if (!IsSatisfied(name))
            {
                unmet.Add(name);
            }
        }

        return unmet;
    }

    /// <summary>
    ///     Whether a dependency name is satisfied right now.
    /// </summary>
    /// <remarks>
    ///     A name that belongs to a plugin in this load set resolves against that plugin: it
    ///     is satisfied once that plugin is ACTIVE. Every other name goes to the resolver the
    ///     composition root published, which is where "the MCP client is connected" and "the
    ///     configuration file exists" live.
    /// </remarks>
    private bool IsSatisfied(string name)
    {
        if (_state.IsActiveName(name))
        {
            return true;
        }

        // No resolver means the host published no capability at all — nothing is ready.
        return _dependencies is not null && _dependencies.IsSatisfied(name);
    }

    /// <summary>
    ///     Say what the pending plugins are waiting for — and say when the reason is a
    ///     dependency cycle, which no amount of waiting will resolve.
    /// </summary>
    private void ReportPending()
    {
        IReadOnlyList<PluginStateStore.PendingEntry> snapshot = _state.PendingEntries;

        if (snapshot.Count == 0)
        {
            return;
        }

        Dictionary<string, PluginStateStore.PendingEntry> byName = new(StringComparer.Ordinal);
        foreach (PluginStateStore.PendingEntry entry in snapshot)
        {
            // First one wins: two plugins with the same name is a different defect, and
            // this report is about dependencies.
            byName.TryAdd(entry.Plugin.Name, entry);
        }

        foreach (PluginStateStore.PendingEntry entry in snapshot)
        {
            IReadOnlyList<string>? cycle = FindCycle(entry, byName);
            if (cycle is not null)
            {
                _logger.LogWarning(
                    "Plugin {DisplayName} is blocked by a circular dependency ({Cycle}). It will not activate until the cycle is broken.",
                    entry.Plugin.DisplayName,
                    string.Join(" -> ", cycle));
                continue;
            }

            _logger.LogInformation(
                "Plugin {DisplayName} is registered but not activated: waiting for {Dependencies}.",
                entry.Plugin.DisplayName,
                string.Join(", ", entry.Unsatisfied));
        }
    }

    /// <summary>
    ///     The cycle the given plugin sits in, or <see langword="null" /> when it is waiting
    ///     for something that is not a pending plugin.
    /// </summary>
    /// <remarks>
    ///     The walk follows the unmet dependencies that name another pending plugin. Coming
    ///     back to the plugin we started from is the cycle; a cycle that does not include it
    ///     is somebody else's, and is reported when that plugin is visited.
    /// </remarks>
    private static IReadOnlyList<string>? FindCycle(
        PluginStateStore.PendingEntry start,
        IReadOnlyDictionary<string, PluginStateStore.PendingEntry> pendingByName)
    {
        List<string> path = [start.Plugin.Name];
        HashSet<string> seen = new(StringComparer.Ordinal) { start.Plugin.Name };
        PluginStateStore.PendingEntry current = start;

        while (true)
        {
            string? next = null;
            foreach (string name in current.Unsatisfied)
            {
                if (pendingByName.ContainsKey(name))
                {
                    next = name;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            if (next == start.Plugin.Name)
            {
                path.Add(next);
                return path;
            }

            if (!seen.Add(next))
            {
                // A cycle that does not run through the plugin we started from.
                return null;
            }

            path.Add(next);
            current = pendingByName[next];
        }
    }
}
