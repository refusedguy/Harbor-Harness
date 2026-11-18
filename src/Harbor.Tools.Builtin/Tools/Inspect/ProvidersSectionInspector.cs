using System.Text;
using Harbor.Abstractions.Providers;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The provider section of the runtime catalog: the catalogue this build
///     knows and the providers registered in this runtime, read live from
///     <see cref="IProviderRegistry" />.
/// </summary>
/// <remarks>
///     <para>
///         The first SRP/OCP slice of <see cref="InspectTool" />. A provider is a
///         fact of the runtime, not of the calling agent, so this section needs
///         neither the tool's permission helpers nor its agent lookup — only the
///         registry that owns providers and the snapshot that owns the
///         catalogue. Moving it here changes no output: the strings are the
///         same ones the tool used to build inline.
///     </para>
///     <para>
///         No credential ever reaches this section. The catalogue shape has no
///         field for one, and a provider found only in the registry is described
///         from its id alone.
///     </para>
/// </remarks>
internal sealed class ProvidersSectionInspector(
    IProviderRegistry providers,
    RuntimeApiFacts facts) : ISectionInspector
{
    /// <inheritdoc />
    public InspectTool.Section Section => InspectTool.Section.Providers;

    /// <inheritdoc />
    public Result<string> Describe(string? name, ToolContext context)
    {
        // Providers are runtime facts; the agent asking about them changes nothing.
        _ = context;

        IReadOnlyList<ProviderId> registered = providers.GetRegisteredProviderIds();
        IReadOnlyList<RuntimeProviderEntry> known = facts.KnownProviders;

        if (name is not null)
        {
            foreach (RuntimeProviderEntry entry in known)
            {
                if (string.Equals(entry.Id, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildProviderDetail(entry, IsRegistered(registered, entry.Id)));
                }
            }

            foreach (ProviderId id in registered)
            {
                if (string.Equals(id.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildProviderDetail(
                        new RuntimeProviderEntry(id.Value, id.Value, string.Empty, false, null),
                        true));
                }
            }

            return Result.Failure<string>(
                $"No provider named '{name}'. Known providers: {InspectTool.Names(known.Select(p => p.Id))}.");
        }

        var sb = new StringBuilder();
        sb.Append("providers: ").Append(known.Count).Append(" known, ").Append(registered.Count).Append(" registered\n");
        foreach (RuntimeProviderEntry entry in known)
        {
            sb.Append(entry.Id);
            sb.Append(" | ").Append(entry.DisplayName);
            if (!string.IsNullOrWhiteSpace(entry.DefaultModel))
            {
                sb.Append(" | default model ").Append(entry.DefaultModel);
            }

            sb.Append(" | ").Append(entry.RequiresApiKey ? "api key required" : "no api key");
            sb.Append(" | ").Append(IsRegistered(registered, entry.Id) ? "registered" : "not registered");
            sb.Append('\n');
        }

        return Result.Success(sb.ToString());
    }

    private static string BuildProviderDetail(RuntimeProviderEntry entry, bool registered)
    {
        var sb = new StringBuilder();
        sb.Append("provider ").Append(entry.Id).Append('\n');
        sb.Append("display: ").Append(entry.DisplayName).Append('\n');
        if (!string.IsNullOrWhiteSpace(entry.DefaultModel))
        {
            sb.Append("default model: ").Append(entry.DefaultModel).Append('\n');
        }

        sb.Append("credential: ").Append(entry.RequiresApiKey ? "an api key is required" : "none required").Append('\n');
        sb.Append("registered: ").Append(registered ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(entry.SetupHint))
        {
            sb.Append("setup: ").Append(entry.SetupHint).Append('\n');
        }

        return sb.ToString();
    }

    private static bool IsRegistered(IReadOnlyList<ProviderId> registered, string id)
    {
        foreach (ProviderId provider in registered)
        {
            if (string.Equals(provider.Value, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
