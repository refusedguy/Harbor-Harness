using Harbor.Plugins.Runtime.Compilation;
namespace Harbor.Plugins.Runtime.Instantiation;

/// <summary>
///     Reflects over a <see cref="CompiledPluginAssembly" />, finds public
/// <see cref="Harbor.Abstractions.Plugins.IPlugin" /> implementations with parameterless
/// constructors, and creates live instances via
/// <see cref="System.Activator.CreateInstance(System.Type)" />. Does NOT call
/// <c>Initialize</c> or any <c>Register*</c> method — that is the responsibility of the
/// registration layer.
/// </summary>
/// <remarks>
///     The split lets tests verify instantiation in isolation (no host, no
///     configuration, no DI) and lets the host plug in alternative instantiation
/// strategies (e.g. DI-aware activators, interpreter-based plugins).
/// </remarks>
public interface IPluginInstantiator
{
    /// <summary>
    ///     Instantiate all <see cref="Harbor.Abstractions.Plugins.IPlugin" />
    ///     implementations in <paramref name="compiled" />.
    /// </summary>
    /// <param name="compiled">The compiled plugin assembly.</param>
    /// <returns>
    ///     Success with the list of instantiated <see cref="LoadedPlugin" />s, or
    ///     failure with an error message. A successful return may carry an empty list if
    ///     no plugin types were found.
    /// </returns>
    Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled);
}
