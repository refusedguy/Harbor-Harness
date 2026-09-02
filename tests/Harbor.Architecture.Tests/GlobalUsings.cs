// Harbor.Architecture.Tests — global usings.
//
// This project asserts the layering invariants documented in
// docs/ARCHITECTURE_LAYERS.md. It loads every Harbor assembly into the AppDomain
// and inspects Assembly.GetReferencedAssemblies() to verify the dependency
// direction (outer → inner only). See ARCHITECTURE_LAYERS.md §5 for the full
// list of rules and §2 for the canonical reference matrix.

global using System.Reflection;
global using TUnit.Core;
using Assembly = System.Reflection.Assembly;

namespace Harbor.Architecture.Tests;
/// <summary>
///     Internal helpers for the architecture tests.
/// </summary>
internal static class ArchitectureTestHelpers
{
    /// <summary>
    ///     All Harbor assemblies loaded into the current AppDomain, keyed by simple
    ///     assembly name. Only assemblies whose name starts with <c>Harbor</c> are
    ///     returned — test framework assemblies, System.Runtime, etc. are filtered out.
    /// </summary>
    /// <remarks>
    ///     Because the runtime JIT-loads assemblies on first use, simply enumerating
    ///     <see cref="AppDomain.GetAssemblies" /> may miss assemblies that no test has
    ///     touched yet. To make the inventory deterministic, we walk the entry
    ///     assembly's <see cref="Assembly.GetReferencedAssemblies" /> list and explicitly
    ///     load every Harbor reference. (Recursive walk is unnecessary — the test
    ///     project has direct <c>ProjectReference</c> edges to every Harbor project.)
    /// </remarks>
    /// <returns>A read-only dictionary of Harbor assembly name → loaded assembly.</returns>
    public static IReadOnlyDictionary<string, Assembly> LoadHarborAssemblies()
    {
        var result = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        // Seed with what's already loaded — cheaper than re-loading.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? string.Empty;
            if (name.StartsWith("Harbor", StringComparison.Ordinal))
            {
                result[name] = asm;
            }
        }

        // Force-load every Harbor assembly referenced by THIS test assembly so
        // the full set is available for inventory. (Not Assembly.GetEntryAssembly:
        // under Microsoft.TestingPlatform the entry assembly is testhost, which
        // only references the test dll — not the individual Harbor projects.)
        // Note: Roslyn omits UNUSED assembly references from the ref list, so a
        // project this test dll never touches would be missed here — the bin-dir
        // sweep below closes that gap.
        var entry = typeof(ArchitectureTestHelpers).Assembly;
        if (entry is not null)
        {
            foreach (var refName in entry.GetReferencedAssemblies())
            {
                string? name = refName.Name;
                if (name is null || !name.StartsWith("Harbor", StringComparison.Ordinal))
                {
                    continue;
                }
                if (result.ContainsKey(name))
                {
                    continue;
                }
                try
                {
                    var loaded = Assembly.Load(refName);
                    result[name] = loaded;
                }
                catch (Exception ex)
                {
                    // Best-effort: if an assembly fails to load (e.g. missing
                    // native dependency), we leave it out of the inventory. The
                    // per-assembly tests will surface the actual layering failures.
                    _ = ex;
                }
            }
        }

        // Sweep the bin directory for every Harbor*.dll the csproj graph copied
        // local — deterministic inventory independent of which refs survived in
        // the test assembly's own reference list.
        string binDir = AppContext.BaseDirectory;
        string[] dlls;
        try { dlls = Directory.GetFiles(binDir, "Harbor*.dll"); }
        catch { dlls = []; }
        foreach (string path in dlls)
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(path);
                if (name.Name is null || result.ContainsKey(name.Name)) continue;
                result[name.Name] = Assembly.Load(name);
            }
            catch
            {
                // Not a managed assembly or unloadable — skip.
            }
        }
        return result;
    }

    /// <summary>
    ///     Get the simple names of assemblies referenced by the given assembly.
    /// </summary>
    /// <param name="asm">The assembly to inspect.</param>
    /// <returns>A set of referenced assembly simple names.</returns>
    public static HashSet<string> GetReferencedAssemblyNames(Assembly asm)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refName in asm.GetReferencedAssemblies())
        {
            if (refName.Name is { } n)
            {
                names.Add(n);
            }
        }
        return names;
    }

    /// <summary>
    ///     Assert that the given assembly does NOT reference any of the forbidden
    ///     Harbor assemblies. Returns the list of actual violations (empty if none).
    /// </summary>
    /// <param name="asm">The assembly to inspect.</param>
    /// <param name="forbidden">Forbidden Harbor assembly simple names.</param>
    /// <returns>The list of actual violations (empty if none).</returns>
    public static List<string> FindForbiddenReferences(Assembly asm, params string[] forbidden)
    {
        var refs = GetReferencedAssemblyNames(asm);
        var violations = new List<string>();
        foreach (string f in forbidden)
        {
            if (refs.Contains(f))
            {
                violations.Add(f);
            }
        }
        return violations;
    }
}
