using Harbor.Abstractions.Agents;
using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Agent registration — registers the default <c>code</c> /
///     <c>plan</c> / <c>explore</c> agents using the CommonConfig default
///     provider/model (or HARBOR_MODEL env override). The wizard saves
///     <c>DefaultProvider="kilocode"</c> + <c>DefaultModel="tencent/hy3:free"</c>
///     — these are combined into <c>"kilocode/tencent/hy3:free"</c> and split
///     on the first slash so the agent knows which provider + model to call.
/// </summary>
internal static class AgentRegistration
{
    /// <summary>
    ///     Build the <see cref="AgentRegistry" /> eagerly with the three
    ///     default agents (code / plan / explore) all pointing at the
    ///     resolved default provider + model.
    /// </summary>
    /// <param name="commonConfig">The loaded <see cref="CommonConfig" />.</param>
    /// <returns>The constructed <see cref="AgentRegistry" /> (not frozen — caller may add more).</returns>
    public static AgentRegistry Build(CommonConfig commonConfig)
    {
        var agentRegistry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(agentRegistry);
        string defaultModel = ResolveDefaultModel(commonConfig);
        string[] parts = defaultModel.Split('/', 2);
        string defaultProviderId = parts[0];
        string defaultModelId = parts.Length > 1 ? parts[1] : defaultModel;
        ab.AddAgent(AgentDefinition.CodeDefault(defaultModelId, defaultProviderId));
        ab.AddAgent(AgentDefinition.PlanDefault(defaultModelId, defaultProviderId));
        ab.AddAgent(AgentDefinition.ExploreDefault(defaultModelId, defaultProviderId));
        return agentRegistry;
    }

    /// <summary>
    ///     Resolve the default "provider/model" string from (1) the
    ///     HARBOR_MODEL env var, or (2) CommonConfig's DefaultProvider +
    ///     DefaultModel. The model may already contain the provider prefix
    ///     (e.g. the user typed "kilocode/tencent/hy3:free" in the wizard) —
    ///     in that case we use it as-is. Otherwise we prepend the provider:
    ///     "kilocode" + "/" + "tencent/hy3:free" → "kilocode/tencent/hy3:free".
    /// </summary>
    /// <param name="commonConfig">The loaded <see cref="CommonConfig" />.</param>
    /// <returns>The resolved "provider/model" string.</returns>
    public static string ResolveDefaultModel(CommonConfig commonConfig)
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        string model = commonConfig.DefaultModel;
        string provider = commonConfig.DefaultProvider;
        string prefix = provider + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model
            : prefix + model;
    }
}
