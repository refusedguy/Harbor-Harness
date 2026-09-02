using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Plugins;
using Harbor.Terminal.Abstractions.Plugins;
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

    /// <summary>
    ///     BCL contract assemblies that the Roslyn compiler needs to resolve
    ///     type-forwarded primitive types (Version, Task, CancellationToken,
    ///     IReadOnlyList&lt;&gt;, Array, …). Without these, plugin sources that
    ///     <c>using System.Threading.Tasks;</c> fail to compile with CS0246.
    /// </summary>
    private static readonly string[] WellKnownRuntimeAssemblies =
    {
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Collections.Concurrent.dll",
        "System.Text.RegularExpressions.dll",
        "System.Diagnostics.Process.dll",
        "System.Threading.Tasks.dll",
        "System.Threading.dll",
        "System.Resources.ResourceManager.dll",
        "System.Runtime.InteropServices.RuntimeInformation.dll",
        "System.Runtime.InteropServices.dll",
        "System.Private.Uri.dll",
        "System.Text.Json.dll",
        "System.Linq.dll",
        "System.Console.dll",
        "System.Net.Http.dll"
    };
    private readonly ILogger<PluginAssemblyReferences> _logger;

    /// <summary>
    ///     Construct a new reference collector and snapshot the current
    ///     <see cref="AppDomain" /> assemblies.
    /// </summary>
    /// <param name="logger">Logger for diagnostics (which assemblies were skipped, etc.).</param>
    public PluginAssemblyReferences(ILogger<PluginAssemblyReferences> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        References = BuildReferences();
    }

    /// <summary>The collected <see cref="MetadataReference" />s.</summary>
    public IReadOnlyList<MetadataReference> References
    {
        get;
    }

    /// <summary>
    ///     Build the metadata-reference list. Includes:
    ///     <list type="bullet">
    ///         <item>All non-dynamic, on-disk assemblies in <see cref="AppDomain.CurrentDomain" />.</item>
    ///         <item>
    ///             Explicit fallbacks for Harbor.Abstractions and System.Runtime in case
    ///             they were trimmed from the AppDomain snapshot.
    ///         </item>
    ///     </list>
    /// </summary>
    private IReadOnlyList<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>(capacity: 96);

        // 1. Snapshot the AppDomain — covers Harbor.Abstractions, System.Runtime, etc.
        //    Plus any assemblies already loaded via DI (logging, configuration, …).
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
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
        EnsureReference(refs, seen, typeof(IPlugin).Assembly);
        EnsureReference(refs, seen, typeof(object).Assembly);
        EnsureReference(refs, seen, typeof(JsonDocument).Assembly);
        EnsureReference(refs, seen, typeof(ILogger).Assembly);
        EnsureReference(refs, seen, typeof(Enumerable).Assembly);
        EnsureReference(refs, seen, typeof(Dictionary<,>).Assembly);
        EnsureReference(refs, seen, typeof(ITuiPlugin).Assembly);
        EnsureReference(refs, seen, typeof(Result).Assembly);
        EnsureReference(refs, seen, typeof(Task).Assembly);
        EnsureReference(refs, seen, typeof(CancellationToken).Assembly);

        // 2b. Harbor.Domain — holds the Harbor.Abstractions.Models.* types
        //     (Session, ContentPart, ToolResult, etc.). They declare
        //     `namespace Harbor.Abstractions.Models` but live in Harbor.Domain.dll,
        //     NOT Harbor.Abstractions.dll. Without this explicit reference, plugin
        //     sources with `using Harbor.Abstractions.Models;` fail with CS0234
        //     "The type or namespace name 'Models' does not exist in the namespace
        //     'Harbor.Abstractions'" — which was the user-visible failure that
        //     broke all 3 CompilationLayer tests after the Domain/Abstractions
        //     split landed. typeof() forces Harbor.Domain to load if it wasn't
        //     already (it usually is, via the Abstractions reference, but the
        //     AppDomain snapshot may have been taken before that).
        EnsureReference(refs, seen, typeof(Session).Assembly);

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
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            foreach (string wellKnown in WellKnownRuntimeAssemblies)
            {
                string path = Path.Combine(runtimeDir, wellKnown);
                if (File.Exists(path) && seen.Add(path))
                {
                    try
                    { refs.Add(MetadataReference.CreateFromFile(path)); }
                    catch (IOException)
                    { /* best-effort */
                    }
                }
            }
        }

        return refs;
    }

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
