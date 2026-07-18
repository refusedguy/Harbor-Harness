using System.Reflection;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Runtime.Compilation;
namespace Harbor.Plugins.Runtime.Instantiation;

/// <summary>
///     Default <see cref="IPluginInstantiator" />. Uses plain reflection to find
/// <see cref="IPlugin" /> implementations and <see cref="Activator.CreateInstance" />
/// to construct them with their parameterless constructor. Does NOT call
/// <see cref="IPlugin.Initialize" /> — that is the responsibility of the registration
/// layer (<see cref="Harbor.Plugins.Runtime.Registration.PluginRegistrar" />).
/// </summary>
public sealed class ReflectionPluginInstantiator : IPluginInstantiator
{
    /// <inheritdoc />
    public Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled)
    {
        if (compiled is null)
            throw new ArgumentNullException(nameof(compiled));

        var pluginTypes = FindPluginTypes(compiled.Assembly);
        if (pluginTypes.Count == 0)
        {
            return Result.Failure<IReadOnlyList<LoadedPlugin>>(
                $"No public IPlugin implementations with a parameterless constructor found in '{compiled.SourcePath}'.");
        }

        var loaded = new List<LoadedPlugin>(pluginTypes.Count);
        var errors = new List<string>(pluginTypes.Count);
        foreach (var type in pluginTypes)
        {
            IPlugin instance;
            try
            {
                instance = (IPlugin)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                errors.Add($"{type.FullName}: Activator.CreateInstance failed: {ex.Message}");
                continue;
            }

            loaded.Add(new LoadedPlugin(
                Instance: instance,
                Name: instance.Name,
                Version: instance.Version,
                PluginType: type,
                SourcePath: compiled.SourcePath,
                SourceHash: compiled.SourceHash,
                LoadedFromCache: compiled.FromCache));
        }

        if (loaded.Count == 0)
        {
            return Result.Failure<IReadOnlyList<LoadedPlugin>>(
                $"Failed to instantiate any plugin from '{compiled.SourcePath}'. Errors: {string.Join("; ", errors)}");
        }

        return Result.Success<IReadOnlyList<LoadedPlugin>>(loaded);
    }

    /// <summary>
    ///     Find all public, non-abstract, non-interface types in <paramref name="assembly" />
    ///     that implement <see cref="IPlugin" /> and expose a parameterless constructor.
    /// </summary>
    private static List<Type> FindPluginTypes(Assembly assembly)
    {
        Type pluginType = typeof(IPlugin);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Use whatever types loaded successfully.
            types = ex.Types
                .Where(t => t is not null)
                .Select(t => t!)
                .ToArray();
        }

        return types
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(pluginType.IsAssignableFrom)
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();
    }
}
