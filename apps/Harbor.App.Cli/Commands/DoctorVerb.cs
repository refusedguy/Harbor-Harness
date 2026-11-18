using System.Text.Json;
using ConsoleAppFramework;
using Harbor.Application.Configuration;
using Harbor.Hosting;
using Harbor.Plugins.Hosting;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>doctor</c> verb (see <see cref="CafPilotVerbs"/>).</summary>
internal static class CafDoctorCommands
{
    /// <summary>Check the Harbor installation and print an OK/WARN/FAIL report.</summary>
    public static int Doctor() => DoctorRunner.Run();
}

/// <summary>
///     Collects the live installation facts and prints the <c>harbor doctor</c>
///     report. All facts come from the product's own declarations (provider
///     presets, renderer registry, plugin inventory) rather than a second
///     hand-maintained list, so the report cannot describe a different
///     installation from the one that is running.
/// </summary>
internal static class DoctorRunner
{
    /// <summary>Run the checks, print the table, and fail only on a hard failure.</summary>
    /// <returns>0 when nothing failed, 1 when at least one check failed.</returns>
    internal static int Run()
    {
        DoctorInputs inputs = Collect();
        var env = new DoctorEnvironment(
            static name => Environment.GetEnvironmentVariable(name),
            ProbeDirectory);

        IReadOnlyList<DoctorCheck> checks = DoctorDiagnostics.Build(inputs, env);
        Print(checks);
        return checks.Any(static check => check.Status == DoctorStatus.Fail) ? 1 : 0;
    }

    /// <summary>Read the real surroundings into the pure input record.</summary>
    internal static DoctorInputs Collect()
    {
        string harborDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
        string configPath = JsonConfigStore.GetDefaultPath();
        (bool configExists, string? configProblem) = ReadConfig(configPath);

        string backend = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "jsonl";
        string[] renderers = TuiBackendRegistry.Build().Values
            .Select(static factory => factory.BackendId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        DoctorCredential[] credentials = ProviderPresets.All
            .Where(static preset => preset.RequiresApiKey && !string.IsNullOrWhiteSpace(preset.EnvVarName))
            .Select(static preset => new DoctorCredential(preset.Id, preset.EnvVarName!, preset.SetupHint))
            .ToArray();

        string pluginDir = Path.Combine(harborDir, "plugins");
        IReadOnlyList<string> sources = ReadPluginSources(pluginDir);
        DoctorPluginState plugins = ReadPluginState(Path.Combine(pluginDir, PluginInventory.FileName));

        return new DoctorInputs(
            Environment.Version.ToString(),
            configPath,
            configExists,
            configProblem,
            backend,
            harborDir,
            renderers,
            credentials,
            sources,
            plugins);
    }

    private static (bool Exists, string? Problem) ReadConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, null);
            _ = File.ReadAllText(path);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, ex.Message);
        }
    }

    private static IReadOnlyList<string> ReadPluginSources(string pluginDir)
    {
        try
        {
            if (!Directory.Exists(pluginDir)) return [];
            return Directory.GetFiles(pluginDir, "*.cs")
                .Concat(Directory.GetFiles(pluginDir, "*.dll"))
                .Select(static path => Path.GetFileName(path) ?? path)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DoctorPluginState ReadPluginState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new DoctorPluginState(false, 0, 0);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("plugins", out JsonElement plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
            {
                return new DoctorPluginState(true, 0, 0, "no plugins array in the document");
            }

            int loaded = 0;
            int failed = 0;
            foreach (JsonElement entry in plugins.EnumerateArray())
            {
                string phase = entry.TryGetProperty("phase", out JsonElement phaseElement)
                    ? phaseElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase)) failed++;
                else loaded++;
            }

            return new DoctorPluginState(true, loaded, failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DoctorPluginState(true, 0, 0, ex.Message);
        }
    }

    private static (bool Ok, string Detail) ProbeDirectory(string path)
    {
        string probe = Path.Combine(path, $".doctor-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return (true, $"writable: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"{path}: {ex.Message}");
        }
    }

    private static void Print(IReadOnlyList<DoctorCheck> checks)
    {
        Console.WriteLine("Harbor doctor");
        Console.WriteLine($"{"Status",-6}  {"Check",-14}  Detail");
        foreach (DoctorCheck check in checks)
        {
            Console.WriteLine($"{check.Status,-6}  {check.Name,-14}  {check.Detail}");
            if (check.Hint is not null)
            {
                Console.WriteLine("        hint: " + check.Hint);
            }
        }

        int failed = checks.Count(static check => check.Status == DoctorStatus.Fail);
        int warned = checks.Count(static check => check.Status == DoctorStatus.Warn);
        Console.WriteLine();
        Console.WriteLine($"Summary: {checks.Count} checks, {failed} fail, {warned} warn.");
    }
}
