using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-B (#1287): <c>providers</c> + <c>models</c> + <c>auth</c> + <c>config</c>
///     verbs on ConsoleAppFramework. Handler methods only — the ONE
///     <c>RunAsync</c> call site stays in <see cref="CafPilotVerbs" /> (single
///     RunAsync per assembly: the generator keys its emitted file without a
///     per-site disambiguator, so two builder sites collide with CS8785 — see
///     the pilot remarks). Handlers take the raw positional tail and delegate
///     to <c>ProviderVerbs</c>/<c>AuthVerb</c>/<c>ConfigVerb</c>, so output
///     stays byte-identical; <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     Known deferral (same as CAF-A): tokens starting with <c>-</c> are
///     option candidates to the framework, so a <c>--flag</c> tail the manual
///     parsers used to accept now fails closed at the parser (exit 1) instead
///     of reaching the runner. Bare + error-usage paths are byte-identical
///     (see <c>CafProvidersAuthConfigTests</c>); fully typed flags are the
///     follow-up.
/// </remarks>
internal static class CafProviderCommands
{
    /// <summary>List registered LLM providers and their health.</summary>
    public static async Task<int> Providers() =>
        await ProviderVerbs.RunListProvidersAsync(NullLogger.Instance).ConfigureAwait(false);

    /// <summary>List models, optionally filtered to one provider.</summary>
    public static async Task<int> Models([Argument]params string[] args) =>
        await ProviderVerbs.RunListModelsAsync(NullLogger.Instance, args.FirstOrDefault()).ConfigureAwait(false);
}

/// <summary>
///     CAF-B (#1287): <c>auth</c> verb handler (set/list/reset API keys). See
///     the remarks on <see cref="CafProviderCommands" /> for the single-builder
///     rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafAuthCommands
{
    /// <summary>Manage API keys (set, list, reset).</summary>
    public static async Task<int> Auth([Argument]params string[] args) =>
        await AuthVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}

/// <summary>
///     CAF-B (#1287): <c>config</c> verb handler (show/set configuration). See
///     the remarks on <see cref="CafProviderCommands" /> for the single-builder
///     rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafConfigCommands
{
    /// <summary>Show or edit Harbor configuration.</summary>
    public static async Task<int> Config([Argument]params string[] args) =>
        await ConfigVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}
