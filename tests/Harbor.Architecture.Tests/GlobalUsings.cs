// Harbor.Architecture.Tests — global usings.
//
// This project asserts the layering invariants documented in
// docs/ARCHITECTURE_LAYERS.md. It loads every Harbor assembly into the AppDomain
// and inspects Assembly.GetReferencedAssemblies() to verify the dependency
// direction (outer → inner only). See ARCHITECTURE_LAYERS.md §5 for the full
// list of rules and §2 for the canonical reference matrix.

global using System.Reflection;
global using TUnit.Core;

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
    public static IReadOnlyDictionary<string, System.Reflection.Assembly> LoadHarborAssemblies()
    {
        var result = new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);

        // Seed with what's already loaded — cheaper than re-loading.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name ?? string.Empty;
            if (name.StartsWith("Harbor", StringComparison.Ordinal))
            {
                result[name] = asm;
            }
        }

        // Force-load every Harbor assembly referenced by the entry assembly so the
        // full set is available for inventory. Assembly.Load is a no-op if already
        // loaded.
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        if (entry is not null)
        {
            foreach (AssemblyName refName in entry.GetReferencedAssemblies())
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
                    var loaded = System.Reflection.Assembly.Load(refName);
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
        foreach (AssemblyName refName in asm.GetReferencedAssemblies())
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
