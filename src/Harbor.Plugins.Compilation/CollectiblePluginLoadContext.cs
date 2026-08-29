using System.Reflection;
using System.Runtime.Loader;
using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Compilation;

/// <summary>
///     Collectible <see cref="AssemblyLoadContext" /> that isolates a single plugin
///     assembly from the host. Defence in depth on top of the trust policy:
///     <list type="bullet">
///         <item><b>Collectible</b> — <c>isCollectible: true</c>; after
///         <see cref="AssemblyLoadContext.Unload" /> the plugin's code and metadata are
///         reclaimable by GC (no leak when a plugin is re-loaded or removed).</item>
///         <item><b>Deny-list</b> — resolves of sensitive framework assemblies
///         (<c>System.IO.FileSystem</c>, <c>System.Diagnostics.Process</c>,
///         <c>System.Net.Http</c>) fail with <see cref="FileNotFoundException" /> at the
///         call site unless the plugin's manifest declares the matching capability and
///         the user approved it. Fail-closed: unknown capability = deny.</item>
///         <item><b>Shared types</b> — host-owned assemblies
///         (<c>Harbor.Abstractions</c>, <c>Harbor.Abstractions.Contracts</c>,
///         <c>Harbor.Plugins.Abstractions</c>) resolve from the default ALC so plugin
///         instances can be cast to host interfaces (<see cref="Harbor.Abstractions.Plugins.IPlugin" />
///         is type-identical, not structurally matched).</item>
///     </list>
/// </summary>
public sealed class CollectiblePluginLoadContext : AssemblyLoadContext
{
    /// <summary>Sensitive assembly → capability required to resolve it. Fail-closed.</summary>
    private static readonly IReadOnlyDictionary<string, PluginCapability> DenyList =
        new Dictionary<string, PluginCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.IO.FileSystem"] = PluginCapability.ReadFiles,
            ["System.Diagnostics.Process"] = PluginCapability.RunProcesses,
            ["System.Net.Http"] = PluginCapability.HttpRequests,
        };

    private static readonly IReadOnlySet<PluginCapability> EmptyCapabilities =
        new HashSet<PluginCapability>();

    private readonly IReadOnlySet<PluginCapability> _granted;
    private readonly IReadOnlyCollection<Assembly> _sharedAssembliesWideOpen;

    /// <summary>
    ///     Construct a sandbox for a plugin assembly image.
    /// </summary>
    /// <param name="pluginName">
    ///     Stable identifier used for the ALC name and diagnostics (typically the source path).
    /// </param>
    /// <param name="granted">
    ///     Capabilities the user approved for this plugin. Resolves of denied assemblies
    ///     fail when the required capability is absent.
    /// </param>
    /// <param name="sharedAssemblies">
    ///     Host assemblies whose types must be shared with the plugin (identity resolved
    ///     from the default ALC). Callers should pass at least the assemblies containing
    ///     <see cref="Harbor.Abstractions.Plugins.IPlugin" />.
    /// </param>
    public CollectiblePluginLoadContext(
        string pluginName,
        IReadOnlySet<PluginCapability> granted,
        IEnumerable<Assembly>? sharedAssemblies = null)
        : base(name: $"harbor-plugin:{pluginName}", isCollectible: true)
    {
        _granted = granted ?? throw new ArgumentNullException(nameof(granted));
        _sharedAssembliesWideOpen = (sharedAssemblies ?? [])
            .Where(a => a is not null)
            .ToArray();
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (assemblyName.Name is null)
            return null;

        // 1. Shared host types: resolve from the default ALC so interfaces are
        //    type-identical between plugin and host (Harbor.Abstractions, ...).
        foreach (var shared in _sharedAssembliesWideOpen)
        {
            if (string.Equals(assemblyName.Name, shared.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return shared;
        }

        // 2. Deny-list: refuse to resolve sensitive framework assemblies unless the
        //    matching capability was approved. Throwing here surfaces as a
        //    FileNotFoundException at the plugin's call site — at the moment it
        //    actually attempts File.Delete / Process.Start / new HttpClient().
        if (DenyList.TryGetValue(assemblyName.Name, out var required) && !_granted.Contains(required))
        {
            throw new FileNotFoundException(
                $"Plugin assembly '{assemblyName.Name}' is blocked by the Harbor plugin sandbox: " +
                $"capability '{PluginCapabilities.ToName(required)}' was not approved for this plugin. " +
                "Declare it in '// harbor:capabilities ...' and re-approve the plugin.");
        }

        // 3. Everything else falls through to the default resolution order
        //    (runtime shared framework), which keeps normal BCL usage working.
        return null;
    }

    /// <summary>
    ///     Load the plugin PE image into this collectible context.
    /// </summary>
    public Assembly LoadFromImage(byte[] assemblyBytes, byte[]? pdbBytes = null) =>
        pdbBytes is null
            ? LoadFromStream(new MemoryStream(assemblyBytes))
            : LoadFromStream(new MemoryStream(assemblyBytes), new MemoryStream(pdbBytes));

    /// <summary>
    ///     Load a cached plugin assembly file into this collectible context.
    /// </summary>
    public Assembly LoadFromPluginPath(string assemblyPath) =>
        LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

    /// <summary>
    ///     Capability-set check helper used by tests and the audit path: is
    ///     <paramref name="assemblyName" /> blocked for the granted set?
    /// </summary>
    public static bool IsDenied(string assemblyName, IReadOnlySet<PluginCapability> granted) =>
        DenyList.TryGetValue(assemblyName, out var required) && !granted.Contains(required);
}
