using Harbor.Plugins.Abstractions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Compilation;

/// <summary>
///     Collects <see cref="MetadataReference" />s for the Roslyn compilation by snapshotting
///     the assemblies already loaded into the current <see cref="AppDomain" /> plus a small
///     set of well-known Harbor contracts that must always be available to plugin authors.
/// </summary>
/// <remarks>
///     <para>
///         The snapshot is taken once at construction and cached for the lifetime of the
///         loader. A second loader instance (e.g. for a test) builds a fresh snapshot.
///     </para>
///     <para>
///         This approach works for JIT-compiled Harbor (the CLI default). For NativeAOT
///         scenarios, plugins cannot be compiled in-process — use the DLL-based or
///         out-of-process plugin path instead.
///     </para>
/// </remarks>
public sealed class PluginAssemblyReferences
{
    private readonly IReadOnlyList<MetadataReference> _references;
    private readonly ILogger<PluginAssemblyReferences> _logger;

    /// <summary>
    ///     Construct a new reference collector and snapshot the current
    ///     <see cref="AppDomain" /> assemblies.
    /// </summary>
    /// <param name="logger">Logger for diagnostics (which assemblies were skipped, etc.).</param>
    public PluginAssemblyReferences(ILogger<PluginAssemblyReferences> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _references = BuildReferences();
    }

    /// <summary>The collected <see cref="MetadataReference" />s.</summary>
    public IReadOnlyList<MetadataReference> References => _references;

    /// <summary>
    ///     Build the metadata-reference list. Includes:
    ///     <list type="bullet">
    ///         <item>All non-dynamic, on-disk assemblies in <see cref="AppDomain.CurrentDomain" />.</item>
    ///         <item>Explicit fallbacks for Harbor.Abstractions and System.Runtime in case
    ///         they were trimmed from the AppDomain snapshot.</item>
    ///     </list>
    /// </summary>
    private IReadOnlyList<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>(capacity: 96);

        // 1. Snapshot the AppDomain — covers Harbor.Abstractions, System.Runtime, etc.
        //    Plus any assemblies already loaded via DI (logging, configuration, …).
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in assemblies)
        {
            if (asm.IsDynamic)
                continue;

#pragma warning disable IL3000 // Assembly.Location is intentional here — JIT-only path, not AOT.
            string location = asm.Location;
#pragma warning restore IL3000
            if (string.IsNullOrEmpty(location))
                continue;

            if (!seen.Add(location))
                continue;

            try
            {
                refs.Add(MetadataReference.CreateFromFile(location));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Skipped metadata reference for {Assembly}", location);
            }
        }

        // 2. Explicit fallbacks — guarantees Harbor.Abstractions is referenced even if
        //    (somehow) the AppDomain snapshot doesn't include it yet. typeof() forces the
        //    assembly to load.
        EnsureReference(refs, seen, typeof(Harbor.Abstractions.Plugins.IPlugin).Assembly);
        EnsureReference(refs, seen, typeof(object).Assembly);
        EnsureReference(refs, seen, typeof(System.Text.Json.JsonDocument).Assembly);
        EnsureReference(refs, seen, typeof(Microsoft.Extensions.Logging.ILogger).Assembly);
        EnsureReference(refs, seen, typeof(System.Linq.Enumerable).Assembly);
        EnsureReference(refs, seen, typeof(System.Collections.Generic.Dictionary<,>).Assembly);
        EnsureReference(refs, seen, typeof(Harbor.Tui.Abstractions.Plugins.ITuiPlugin).Assembly);
        EnsureReference(refs, seen, typeof(CSharpFunctionalExtensions.Result).Assembly);
        EnsureReference(refs, seen, typeof(System.Threading.Tasks.Task).Assembly);
        EnsureReference(refs, seen, typeof(System.Threading.CancellationToken).Assembly);

        // 3. Scan the .NET runtime directory for System.Runtime / System.Collections /
        //    etc. — these contract assemblies are needed by the compiler to resolve
        //    type-forwarded BCL types (Version, Task, IReadOnlyList<>, …) even when
        //    System.Private.CoreLib is referenced. In some host environments (e.g. test
        //    runners that don't trigger lazy loads of these contracts before the snapshot)
        //    they may be missing from the AppDomain loop above.
        //
        //    RuntimeEnvironment.GetRuntimeDirectory() is the documented way to find the
        //    runtime directory and works even when Assembly.Location returns empty (e.g.
        //    single-file publish or some test hosts).
        string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            foreach (var wellKnown in WellKnownRuntimeAssemblies)
            {
                string path = Path.Combine(runtimeDir, wellKnown);
                if (File.Exists(path) && seen.Add(path))
                {
                    try { refs.Add(MetadataReference.CreateFromFile(path)); }
                    catch (IOException) { /* best-effort */ }
                }
            }
        }

        return refs;
    }

    /// <summary>
    ///     BCL contract assemblies that the Roslyn compiler needs to resolve
    ///    type-forwarded primitive types (Version, Task, CancellationToken,
    ///    IReadOnlyList&lt;&gt;, Array, …). Without these, plugin sources that
    ///    <c>using System.Threading.Tasks;</c> fail to compile with CS0246.
    /// </summary>
    private static readonly string[] WellKnownRuntimeAssemblies =
    {
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Threading.Tasks.dll",
        "System.Threading.dll",
        "System.Resources.ResourceManager.dll",
        "System.Runtime.InteropServices.dll",
        "System.Private.Uri.dll",
        "System.Text.Json.dll",
        "System.Linq.dll",
        "System.Console.dll",
    };

    private static void EnsureReference(
        List<MetadataReference> refs,
        HashSet<string> seen,
        Assembly asm)
    {
#pragma warning disable IL3000 // Assembly.Location is intentional here — JIT-only path, not AOT.
        string location = asm.Location;
#pragma warning restore IL3000
        if (string.IsNullOrEmpty(location))
            return;
        if (!seen.Add(location))
            return;
        try
        {
            refs.Add(MetadataReference.CreateFromFile(location));
        }
        catch (IOException)
        {
            // Best-effort — silently skip.
        }
    }
}
