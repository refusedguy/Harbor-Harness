using System.Text;

namespace Harbor.App.Cli.Commands;

/// <summary>Health level of one installation check.</summary>
internal enum DoctorStatus
{
    /// <summary>The check found a healthy installation.</summary>
    Ok,

    /// <summary>The installation works but a setting is missing or degraded.</summary>
    Warn,

    /// <summary>The installation cannot work until the finding is fixed.</summary>
    Fail,
}

/// <summary>One row of the doctor report.</summary>
/// <param name="Name">Short check name printed in the table.</param>
/// <param name="Status">The finding.</param>
/// <param name="Detail">What was observed, including any missing environment variable name.</param>
/// <param name="Hint">An actionable next step, or <see langword="null"/> when none is needed.</param>
internal sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? Hint = null);

/// <summary>One provider's credential requirement, projected from its preset.</summary>
/// <param name="ProviderId">The provider that needs the key.</param>
/// <param name="EnvVarName">The environment variable that carries it.</param>
/// <param name="SetupHint">The provider's own setup hint, when it declares one.</param>
internal sealed record DoctorCredential(string ProviderId, string EnvVarName, string? SetupHint);

/// <summary>The live plugin state the doctor reads from the published inventory.</summary>
/// <param name="InventoryPresent">Whether the plugin host published an inventory at all.</param>
/// <param name="Loaded">Entries that are not in the failed phase.</param>
/// <param name="Failed">Entries in the failed phase.</param>
/// <param name="Problem">A read/parse problem, when the inventory could not be trusted.</param>
internal sealed record DoctorPluginState(
    bool InventoryPresent,
    int Loaded,
    int Failed,
    string? Problem = null);

/// <summary>The two ambient operations the doctor needs, as functions.</summary>
/// <remarks>
///     Deliberately not an interface: the seam graph indexes every <c>I</c>-prefixed
///     interface it sees, and a CLI-local probe helper is not an extension seam.
///     Delegates keep the finding logic pure and the substitute-environment test
///     small while leaving the graph untouched.
/// </remarks>
/// <param name="ReadEnv">Reads an environment variable by name.</param>
/// <param name="ProbeDirectory">Checks whether a directory is usable; returns a human-readable detail.</param>
internal sealed record DoctorEnvironment(
    Func<string, string?> ReadEnv,
    Func<string, (bool Ok, string Detail)> ProbeDirectory);

/// <summary>The facts a doctor run collects before it judges them.</summary>
internal sealed record DoctorInputs(
    string RuntimeVersion,
    string ConfigPath,
    bool ConfigExists,
    string? ConfigProblem,
    string StorageBackend,
    string StoragePath,
    IReadOnlyList<string> Renderers,
    IReadOnlyList<DoctorCredential> Credentials,
    IReadOnlyList<string> PluginSources,
    DoctorPluginState Plugins);

/// <summary>
///     Pure finding logic for <c>harbor doctor</c>: every check is a function of
///     the collected facts and the injected environment, so a missing key can be
///     tested against a substitute environment without a real provider or a
///     real filesystem write.
/// </summary>
internal static class DoctorDiagnostics
{
    /// <summary>Build the full report, in the order the checks are printed.</summary>
    internal static IReadOnlyList<DoctorCheck> Build(DoctorInputs inputs, DoctorEnvironment env) =>
    [
        Runtime(inputs.RuntimeVersion),
        Config(inputs.ConfigPath, inputs.ConfigExists, inputs.ConfigProblem),
        ApiKeys(inputs.Credentials, env.ReadEnv),
        Storage(inputs.StorageBackend, inputs.StoragePath, env.ProbeDirectory),
        Renderers(inputs.Renderers),
        Plugins(inputs.PluginSources, inputs.Plugins),
    ];

    /// <summary>The running .NET version. Informational: there is nothing to fix here.</summary>
    internal static DoctorCheck Runtime(string version) =>
        new("SDK/runtime", DoctorStatus.Ok, $".NET {version} (the runtime Harbor is running on)");

    /// <summary>A missing config file is fine (defaults are used); an unreadable one is not.</summary>
    internal static DoctorCheck Config(string path, bool exists, string? problem)
    {
        if (problem is not null)
        {
            return new DoctorCheck(
                "Config", DoctorStatus.Warn, $"unreadable: {path} ({problem})",
                "Fix or remove the file at that path so Harbor can fall back to its defaults.");
        }

        return new DoctorCheck(
            "Config", DoctorStatus.Ok,
            exists ? $"readable: {path}" : $"no config file, defaults in use: {path}");
    }

    /// <summary>
    ///     Check the environment variables the installed provider presets require.
    ///     A missing key is a WARN, never a FAIL: Harbor still runs with local or
    ///     already-authenticated providers. The missing variable's NAME is part of
    ///     both the detail and the hint so the user can act on it directly.
    /// </summary>
    internal static DoctorCheck ApiKeys(
        IReadOnlyList<DoctorCredential> credentials,
        Func<string, string?> readEnv)
    {
        var missing = new List<string>();
        int withKey = 0;
        foreach (DoctorCredential credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.EnvVarName)) continue;
            withKey++;
            if (string.IsNullOrWhiteSpace(readEnv(credential.EnvVarName)))
            {
                missing.Add(credential.EnvVarName);
            }
        }

        if (missing.Count == 0)
        {
            return new DoctorCheck(
                "API keys", DoctorStatus.Ok,
                withKey == 0
                    ? "no key-based providers configured"
                    : $"{withKey} credential env var(s) set");
        }

        var hint = new StringBuilder("Set ");
        hint.Append(string.Join(", ", missing));
        hint.Append(" (for example with `harbor auth set <provider>`), or use a local provider such as ollama.");
        return new DoctorCheck(
            "API keys", DoctorStatus.Warn,
            $"missing: {string.Join(", ", missing)}",
            hint.ToString());
    }

    /// <summary>The configured storage backend must be usable, unless it holds nothing on disk.</summary>
    internal static DoctorCheck Storage(
        string backend,
        string path,
        Func<string, (bool Ok, string Detail)> probe)
    {
        if (string.Equals(backend, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new DoctorCheck("Storage", DoctorStatus.Ok, "backend=memory (no disk access needed)");
        }

        (bool ok, string detail) = probe(path);
        return ok
            ? new DoctorCheck("Storage", DoctorStatus.Ok, $"backend={backend}; {detail}")
            : new DoctorCheck(
                "Storage", DoctorStatus.Fail, $"backend={backend}; {detail}",
                $"Fix access to {path}, or select another backend with HARBOR_STORAGE.");
    }

    /// <summary>At least one renderer must be compiled in, or Harbor cannot paint a session.</summary>
    internal static DoctorCheck Renderers(IReadOnlyList<string> renderers) =>
        renderers.Count == 0
            ? new DoctorCheck(
                "Renderers", DoctorStatus.Fail, "no TUI renderer is registered",
                "Rebuild Harbor with a renderer, or set HARBOR_TUI=plain.")
            : new DoctorCheck("Renderers", DoctorStatus.Ok, string.Join(", ", renderers));

    /// <summary>
    ///     Report what the plugin host actually published, and call out the two
    ///     honest gap cases: sources present without an inventory, and an inventory
    ///     that could not be read.
    /// </summary>
    internal static DoctorCheck Plugins(IReadOnlyList<string> sources, DoctorPluginState state)
    {
        if (state.Problem is not null)
        {
            return new DoctorCheck(
                "Plugins", DoctorStatus.Warn, $"inventory unreadable: {state.Problem}",
                "Run `harbor plugin inventory` and check the plugin root permissions.");
        }

        if (!state.InventoryPresent)
        {
            return sources.Count == 0
                ? new DoctorCheck("Plugins", DoctorStatus.Ok, "none installed")
                : new DoctorCheck(
                    "Plugins", DoctorStatus.Warn, $"{sources.Count} plugin source(s) found, no live inventory",
                    "The plugin host has not published an inventory yet; restart Harbor, then run `harbor plugin inventory`.");
        }

        if (state.Failed > 0)
        {
            return new DoctorCheck(
                "Plugins", DoctorStatus.Warn, $"{state.Loaded} loaded, {state.Failed} failed",
                "Run `harbor plugin inventory` for the per-plugin failure reasons.");
        }

        return new DoctorCheck("Plugins", DoctorStatus.Ok, $"{state.Loaded} loaded");
    }
}
