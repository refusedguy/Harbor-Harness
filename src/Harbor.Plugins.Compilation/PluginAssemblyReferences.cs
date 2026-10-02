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
///     Collects <see cref="MetadataReference" />s for the Roslyn compilation.
/// </summary>
/// <remarks>
///     <para>
///         The set has two tiers and the order of precedence is the contract:
///         <list type="number">
///             <item>
///                 <see cref="PluginContractAssemblies" /> — the declared plugin contract
///                 surface, resolved BY NAME against the deployment directory. Present in
///                 every process, whatever it has loaded.
///             </item>
///             <item>
///                 An <see cref="AppDomain" /> snapshot plus the BCL contract assemblies.
///                 This tier may only WIDEN the set. It is what makes the common case
///                 (a CLI that has already initialised most of itself) work smoothly, and
///                 it is not allowed to be the reason anything on the first tier resolves.
///             </item>
///         </list>
///     </para>
///     <para>
///         Before #1004 there was no first tier: the Harbor contracts were pinned with
///         <c>typeof(...).Assembly</c> calls, which sound for the assemblies this project
///         compile-time references and unsound for the ones it does not. A CS plugin's
///         ability to name a namespace therefore depended on what the host had happened to
///         load, which is the same undecidability as "AOT-clean" (#828) and "dev is green"
///         (#951) — nothing in the product could contradict it.
///     </para>
///     <para>
///         Step 3b is the pin for the assembly that owns the
///         <c>Harbor.Abstractions.Models</c> types — Session, ContentPart, ToolResult and
///         the rest. A plugin source imports that namespace and gets CS0234 when the
///         assembly is missing; that was the user-visible failure that broke all 3
///         CompilationLayer tests when the Domain/Abstractions split landed. The comment
///         that used to sit there named a "Harbor.Domain" assembly. There is no such
///         assembly in this tree — the namespace lives in
///         <c>Harbor.Abstractions.Contracts</c>. (#1004.)
///     </para>
/// <para>
///         For NativeAOT scenarios plugins cannot be compiled in-process at all — use the
///         DLL-based or out-of-process plugin path instead.
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

    /// <summary>
    ///     The <b>declared</b> plugin contract surface: the Harbor assemblies a CS
    ///     plugin author is documented to be able to name, as assembly SIMPLE NAMES.
    ///     Each is resolved against the deployment directory by name, so membership in
    ///     the reference set is a property of the deployed tree and not of which
    ///     assemblies the host happened to have loaded.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #1004. The AppDomain sweep below may WIDEN the set; nothing on this list
    ///         may depend on it. An earlier shape of this file pinned its contracts with
    ///         <c>typeof(...).Assembly</c> calls only, and that is sound for exactly the
    ///         assemblies this project has a COMPILE-TIME reference to — the JIT must
    ///         have loaded them to resolve the token, so the pin can never miss. A
    ///         transitive runtime dependency has no such guarantee: it is absent from a
    ///         process that has not touched it, and present in one that has.
    ///     </para>
    ///     <para>
    ///         The list is a name list rather than a set of <c>typeof</c> pins because the
    ///         only thing this collector needs from the panel assembly is its FILE. Pinning
    ///         <c>typeof(IPanelRegistry).Assembly</c> would work too, and
    ///         <c>Harbor.Plugins.Registration</c> already carries a documented
    ///         Infrastructure -> Presentation exception for exactly this assembly — but that
    ///         project binds the type because <c>PanelRegistryPluginAdapter</c> implements it.
    ///         Here the collector would be binding a UI-framework type at the one place that
    ///         decides what a plugin is allowed to reference, for no gain: the edge would buy
    ///         a type the method does not use, and cost a compile-time dependency plus a
    ///         second architecture-test exemption. A name adds neither.
    ///     </para>
    /// </remarks>
    internal static readonly string[] PluginContractAssemblies =
    [
        "Harbor.Abstractions",
        "Harbor.Abstractions.Contracts",
        "Harbor.Terminal.Abstractions",
    ];

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
    ///     Build the metadata-reference list, declared core first. Includes:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="PluginContractAssemblies" />, resolved by name from the
    ///             deployment directory — the part that does not depend on ambient state.
    ///         </item>
    ///         <item>All non-dynamic, on-disk assemblies in <see cref="AppDomain.CurrentDomain" />.</item>
    ///         <item>
    ///             <c>typeof</c> fallbacks for the contracts this project references at
    ///             compile time, and the BCL contract assemblies from the runtime directory.
    ///         </item>
    ///     </list>
    /// </summary>
    private IReadOnlyList<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>(capacity: 96);

        // 1. The DECLARED contract core — resolved by name against the deployment
        //    directory, so it is present in every process regardless of load order.
        //    Everything after this point may only widen the set, never narrow it.
        string? deployDir = ResolveDeploymentDirectory();
        if (deployDir is not null)
        {
            foreach (string name in PluginContractAssemblies)
            {
                EnsureDeclaredReference(refs, seen, name, deployDir);
            }
        }

        // 2. Snapshot the AppDomain — covers System.Runtime, the BCL contracts the
        //    pin list below cannot name, plus any assemblies already loaded via DI
        //    (logging, configuration, …). #1004: this is an amplifier. It used to be
        //    the ONLY source of the Harbor contracts, which made a CS plugin's
        //    compilability depend on what the host had happened to initialize.
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

        // 3. Type-token fallbacks for the contracts this project references at compile
        //    time. Taking the Assembly property off a type forces that assembly to
        //    load, so these can never miss. The declared list above is what covers
        //    the assemblies this project does NOT reference at compile time.
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

        // 3b. The Models-namespace owner. Class remarks carry the story.
        EnsureReference(refs, seen, typeof(Session).Assembly);

        // 4. Scan the .NET runtime directory for System.Runtime / System.Collections /
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

    /// <summary>
    ///     The directory the Harbor assemblies are deployed next to. Prefers the
    ///     located <see cref="IPlugin" /> assembly (the authoritative answer for a
    ///     normal deployment) and falls back to <see cref="AppContext.BaseDirectory" />,
    ///     which is what a single-file publish reports instead.
    /// </summary>
    private static string? ResolveDeploymentDirectory()
    {
#pragma warning disable IL3000 // Assembly.Location is intentional here — JIT-only path, not AOT.
        string? located = Path.GetDirectoryName(typeof(IPlugin).Assembly.Location);
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(located))
        {
            return located;
        }

        string baseDir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty(baseDir) ? null : baseDir;
    }

    /// <summary>
    ///     Add <paramref name="simpleName" /> from <paramref name="directory" /> without
    ///     loading it. A miss is logged, not swallowed: a declared contract assembly that
    ///     is not deployed is a CS0246 in a plugin author's build, and the only place the
    ///     host can see it is here.
    /// </summary>
    private void EnsureDeclaredReference(
        List<MetadataReference> refs,
        HashSet<string> seen,
        string simpleName,
        string directory)
    {
        string path = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Declared plugin contract assembly {Assembly} is not deployed in {Directory}. "
                + "A CS plugin that imports a namespace it owns will fail to compile with CS0234.",
                simpleName,
                directory);
            return;
        }

        if (!seen.Add(path))
        {
            return;
        }

        try
        {
            refs.Add(MetadataReference.CreateFromFile(path));
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Skipped metadata reference for {Assembly}", path);
        }
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
