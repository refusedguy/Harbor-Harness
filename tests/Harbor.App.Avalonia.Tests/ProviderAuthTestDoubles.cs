// ProviderAuthTestDoubles.cs — doubles for the #671 "one question, one answer" tests.
//
// Every double here exists to make ONE fact expressible: a provider whose key
// lives in the environment, and nowhere else.
//
// EmptyApiKeyConfigStore is the crucial one. It loads a CommonConfig with an
// EMPTY ApiKeys map, so any view-model that reads the config dictionary to
// decide authorization is guaranteed to answer "no key" — which is exactly the
// false negative #671 reported. EnvOnlyAuthResolver answers the truth for the
// same provider. Pairing them makes the two answers differ, and the tests then
// assert the surfaces report the resolver's, because the resolver is the
// declared single source.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     <see cref="IAuthResolver" /> that authorizes a caller-named set of
///     provider ids and refuses the rest — the shape of a key that arrived via
///     the environment, a keychain or a CLI override, i.e. outside every config
///     file the view-models can read.
/// </summary>
internal sealed class EnvOnlyAuthResolver(params string[] authorizedProviderIds) : IAuthResolver
{
    private readonly HashSet<string> _authorized =
        new(authorizedProviderIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider ids this resolver was asked about, in call order.</summary>
    internal List<string> Asked { get; } = [];

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        Asked.Add(providerId);
        return Task.FromResult(
            _authorized.Contains(providerId)
                ? Result.Success($"env-only-key-for-{providerId}")
                : Result.Failure<string>($"No API key for '{providerId}'."));
    }
}

/// <summary>
///     An <see cref="IAuthResolver" /> that authorizes nobody. The negative
///     control: without it, a "both surfaces agree" test would also pass if
///     every surface always said "No key".
/// </summary>
internal sealed class NoKeysAnywhereAuthResolver : IAuthResolver
{
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>($"No API key for '{providerId}'."));
}

/// <summary>
///     An <see cref="IAuthResolver" /> that throws, standing in for a keychain
///     that is locked or a store that is corrupt. A broken credential source must
///     render a verdict, not take the window down.
/// </summary>
internal sealed class ThrowingAuthResolver : IAuthResolver
{
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
        throw new InvalidOperationException("keychain is locked");
}

/// <summary>
///     A config store whose <see cref="CommonConfig.ApiKeys" /> is ALWAYS empty —
///     the "this provider has no saved key" world. Records what a save wrote so
///     a test can assert the row asked the resolver rather than re-deriving the
///     verdict from the field it just typed.
/// </summary>
internal sealed class EmptyApiKeyConfigStore : ICommonConfigStore
{
    /// <summary>Provider ids whose keys were persisted through this store.</summary>
    internal List<string> SavedProviderIds { get; } = [];

    public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new CommonConfig()));

    public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default)
    {
        CommonConfig next = updater(new CommonConfig());
        SavedProviderIds.AddRange([.. next.ApiKeys.Keys]);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>A toast sink that swallows everything — tests assert on state, not on toasts.</summary>
internal sealed class SilentToastService : IToastService
{
#pragma warning disable CS0067
    public event EventHandler<ToastNotification>? ToastAdded;
#pragma warning restore CS0067

    public void Show(string message, ToastKind kind = ToastKind.Info) { }
}

/// <summary>
///     A registry serving one provider with a caller-controlled model catalogue,
///     so a test can make /models answer "here are N models", "here are none",
///     or "I am unreachable" without a network.
/// </summary>
internal sealed class StaticModelRegistry : IProviderRegistry
{
    private readonly Result<IReadOnlyList<ModelInfo>> _models;

    /// <param name="providerId">The single provider this registry reports.</param>
    /// <param name="models">The catalogue <c>GetAllModelsAsync</c> answers with.</param>
    internal StaticModelRegistry(string providerId, Result<IReadOnlyList<ModelInfo>> models)
    {
        Provider = providerId;
        _models = models;
    }

    /// <summary>The single provider id this registry reports.</summary>
    internal string Provider { get; }

    /// <summary>A registry whose /models answers with the given model ids.</summary>
    internal static StaticModelRegistry Serving(string providerId, params string[] modelIds) =>
        new(providerId, Result.Success<IReadOnlyList<ModelInfo>>(
            [.. modelIds.Select(id => Model(id, providerId))]));

    /// <summary>A registry whose /models answers with an empty catalogue.</summary>
    internal static StaticModelRegistry ServingNothing(string providerId) =>
        new(providerId, Result.Success<IReadOnlyList<ModelInfo>>([]));

    /// <summary>A registry whose /models fails — the provider is unreachable.</summary>
    internal static StaticModelRegistry Unreachable(string providerId) =>
        new(providerId, Result.Failure<IReadOnlyList<ModelInfo>>("connection refused"));

    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [ProviderId.Create(Provider)];

    public Result<ILlmClient> GetClient(ProviderId providerId) =>
        Result.Failure<ILlmClient>("not registered");

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_models);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
        GetAllModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

    public Result Unregister(ProviderId providerId) => Result.Failure("not supported");

    private static ModelInfo Model(string modelId, string providerId) =>
        new(modelId, providerId, modelId, 128_000, 8_192, false, false, true, Pricing.Unknown, "openai");
}

/// <summary>
///     A session manager with no session and no router — the picker only reads
///     <c>Active</c> and writes on selection, neither of which these tests do.
/// </summary>
internal sealed class NoSessionsManager : ISessionManager
{
    public Session? Active => null;
    public SessionContext? ActiveContext => null;
    public SessionContext? GetContext(string sessionId) => null;
    public GitSessionInfo GetGitInfo(string sessionId) => new(null, false, 0, null);
    public void RefreshGitInfo(string sessionId, string directory) { }
    public Task EnsureDefaultSessionAsync() => Task.CompletedTask;
    public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

    public Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null) =>
        Task.FromResult(Result.Failure<Session>("not configured"));

    public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);

    public Task<bool> OpenPanelSessionAsync(string sessionId) => OpenSessionAsync(sessionId);

    public Task<Result<Session>> BranchActiveAsync() =>
        Task.FromResult(Result.Failure<Session>("not configured"));

    public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(true);

    public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(true);

    public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;

    public void SetStatus(string sessionId, SessionStatus status) { }

    public void NotifyMessageCount(string sessionId, int count) { }

#pragma warning disable CS0067
    public event Action<string, SessionStatus>? StatusChanged;
    public event Action<string, int>? MessageCountChanged;
#pragma warning restore CS0067

    // ── IPanelSessionGateway (#470) ─────────────────────────────────────────
    public string? GetDirectory(string sessionId) => null;

    public string? GetStatusText(string sessionId) => null;

    public string? GetBranch(string sessionId) => null;

    public bool GetIsDirty(string sessionId) => false;

    public bool? GetIsSubagent(string sessionId) => null;
}
