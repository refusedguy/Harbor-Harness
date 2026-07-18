// Bridge layer — fluent setup helpers for <see cref="ScriptGlobals" />.
//
// Layering rule (see ScriptGlobals.cs): depends only on Harbor.Abstractions.
namespace Harbor.Scripting.Bridge;

/// <summary>
///     Fluent extension methods for constructing <see cref="ScriptGlobals" />.
/// </summary>
public static class ScriptGlobalsExtensions
{
    /// <summary>
    ///     Attach a tool registry to the globals builder.
    /// </summary>
    public static ScriptGlobalsBuilder WithTools(this ScriptGlobalsBuilder builder, IToolRegistry tools)
    {
        builder.Tools = tools;
        return builder;
    }

    /// <summary>
    ///     Attach a provider registry.
    /// </summary>
    public static ScriptGlobalsBuilder WithProviders(this ScriptGlobalsBuilder builder, IProviderRegistry? providers)
    {
        builder.Providers = providers;
        return builder;
    }

    /// <summary>
    ///     Attach an agent registry.
    /// </summary>
    public static ScriptGlobalsBuilder WithAgents(this ScriptGlobalsBuilder builder, IAgentRegistry? agents)
    {
        builder.Agents = agents;
        return builder;
    }

    /// <summary>
    ///     Attach a logger.
    /// </summary>
    public static ScriptGlobalsBuilder WithLogger(this ScriptGlobalsBuilder builder, ILogger logger)
    {
        builder.Logger = logger;
        return builder;
    }

    /// <summary>
    ///     Materialize the builder into a <see cref="ScriptGlobals" />. Throws if
    ///     required fields (<see cref="ScriptGlobals.Tools" />,
    ///     <see cref="ScriptGlobals.Logger" />) are unset.
    /// </summary>
    public static ScriptGlobals Build(this ScriptGlobalsBuilder builder)
    {
        if (builder.Tools is null)
        {
            throw new InvalidOperationException("ScriptGlobals requires Tools. Call WithTools(...) before Build().");
        }
        if (builder.Logger is null)
        {
            throw new InvalidOperationException("ScriptGlobals requires Logger. Call WithLogger(...) before Build().");
        }
        return new ScriptGlobals
        {
            Tools = builder.Tools,
            Providers = builder.Providers,
            Agents = builder.Agents,
            Logger = builder.Logger
        };
    }
}

/// <summary>
///     Mutable builder for <see cref="ScriptGlobals" />. Construct via
///     <c>ScriptGlobalsBuilder.Create()</c> and chain <c>With*</c> methods.
/// </summary>
public sealed class ScriptGlobalsBuilder
{
    /// <summary>Tool registry (required).</summary>
    public IToolRegistry? Tools { get; set; }

    /// <summary>Provider registry (optional).</summary>
    public IProviderRegistry? Providers { get; set; }

    /// <summary>Agent registry (optional).</summary>
    public IAgentRegistry? Agents { get; set; }

    /// <summary>Logger (required).</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Create a fresh builder.</summary>
    public static ScriptGlobalsBuilder Create() => new();
}
