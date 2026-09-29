using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;
using Harbor.Ui.Framework.Services;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.Logging;
namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Per-provider configuration row view-model. One instance per
///     registered provider — holds the editable API key, the live auth
///     status, and the Save / Test commands that persist + verify the key.
/// </summary>
/// <remarks>
///     <para>
///         Extracted from <c>SettingsViewModel</c> so the per-provider
///         save / test logic is unit-testable in isolation (construct a
///         <see cref="ProviderConfigViewModel" /> with a fake
///         <see cref="ICommonConfigStore" /> + <see cref="IProviderRegistry" />
///         and assert the commands behave correctly without spinning up
///         the full Settings dialog).
///     </para>
///     <para>
///         Constructed manually by <c>SettingsViewModel.LoadProviderConfigs</c>
///         (one row per registered provider). Each row carries its own
///         dependencies so the Save / Test commands don't need to bounce
///         through the parent VM.
///     </para>
/// </remarks>
public partial class ProviderConfigViewModel : ObservableObject
{
    private readonly ICommonConfigStore _commonStore;
    private readonly ILogger<ProviderConfigViewModel> _logger;
    private readonly IAuthResolver _auth;
    private readonly IProviderRegistry _providers;
    private readonly IToastService _toasts;

    /// <summary>API key (editable in the UI).</summary>
    [ObservableProperty]
    private string _apiKey;

    /// <summary>Whether an API key has been resolved for this provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthIcon))]
    [NotifyPropertyChangedFor(nameof(AuthText))]
    private bool _isAuthenticated;

    /// <summary>True while a Test connection request is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    /// <summary>Human-readable result of the last Test connection attempt.</summary>
    [ObservableProperty]
    private string _testResult = string.Empty;

    /// <summary>Construct a per-provider config row.</summary>
    /// <param name="id">Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</param>
    /// <param name="displayName">Human-readable display name.</param>
    /// <param name="apiKey">Persisted API key (or empty).</param>
    /// <param name="requiresApiKey">Whether this provider requires an API key (false for Ollama).</param>
    /// <param name="isAuthenticated">Initial auth-status (true if a usable key was resolved).</param>
    /// <param name="commonStore">The common-config store (for persisting the key).</param>
    /// <param name="providers">The provider registry (for Test connection).</param>
    /// <param name="toasts">Toast service (for user feedback).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="auth">The single auth abstraction — the only source of an auth verdict (#671).</param>
    public ProviderConfigViewModel(
        string id,
        string displayName,
        string apiKey,
        bool requiresApiKey,
        bool isAuthenticated,
        ICommonConfigStore commonStore,
        IProviderRegistry providers,
        IToastService toasts,
        ILogger<ProviderConfigViewModel> logger,
        IAuthResolver auth)
    {
        Id = id;
        DisplayName = displayName;
        _apiKey = apiKey;
        RequiresApiKey = requiresApiKey;
        _isAuthenticated = isAuthenticated;
        _commonStore = commonStore;
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _providers = providers;
        _toasts = toasts;
        _logger = logger;
    }

    /// <summary>Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</summary>
    public string Id { get; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; }

    /// <summary>Whether this provider requires an API key (false for Ollama).</summary>
    public bool RequiresApiKey { get; }

    /// <summary>Auth status icon — <c>✓</c> or <c>✗</c>.</summary>
    public string AuthIcon => IsAuthenticated ? "✓" : RequiresApiKey ? "✗" : "—";

    /// <summary>Auth status text — e.g. "Authenticated", "No key".</summary>
    public string AuthText => !RequiresApiKey
        ? "No key needed"
        : IsAuthenticated
            ? "Authenticated"
            : "No key";

    /// <summary>
    ///     Save this provider's API key immediately, without requiring the
    ///     user to click the global "Save" button. Persists via
    ///     <see cref="ICommonConfigStore.UpdateAsync" /> so the key is available
    ///     to the agent + picker right away (the in-process auth resolver
    ///     reloads the config on every request).
    /// </summary>
    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        var row = this;
        var result = await _commonStore.UpdateAsync(cfg =>
        {
            var keys = cfg.ApiKeys;
            if (string.IsNullOrWhiteSpace(row.ApiKey))
            {
                keys = keys.Remove(row.Id);
            }
            else
            {
                keys = keys.SetItem(row.Id, row.ApiKey);
            }
            return cfg with { ApiKeys = keys };
        }).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            // Re-read the verdict from the resolver rather than assuming the
            // save granted it (#671). Non-whitespace ApiKey was a third answer
            // to "is this provider authorized?" — and the wrong one, since a
            // blank field says nothing about the env key the agent will
            // actually use. The resolver reloads config per call, so the value
            // just persisted is already visible to it.
            row.IsAuthenticated = await IsAuthorizedAsync().ConfigureAwait(true);
            row.TestResult = row.IsAuthenticated ? "✓ Key saved" : "Key saved (will check on next request)";
            _toasts.Show($"API key saved for {row.DisplayName}.", ToastKind.Success);
        }
        else
        {
            row.TestResult = $"✗ Save failed: {result.Error}";
            _toasts.Show($"Could not save key for {row.DisplayName}: {result.Error}", ToastKind.Error);
        }
    }

    /// <summary>
    ///     Test this provider's connection by fetching its model list. Persists
    ///     the row's current API key first (so the test sees the value the user
    ///     just typed, not the on-disk one). Used by the "Test" button on each
    ///     provider row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fetching /models IS a provider probe, so this spends
    ///         <see cref="IProviderHealthCheck.DefaultTimeout" /> — the budget
    ///         the onboarding wizard already spends for the same question. It
    ///         used to carry a bare five-second literal and then tell the user
    ///         it had "Timed out after 5s": a second number under one concept,
    ///         and not the number the rest of the app means by it.
    ///     </para>
    ///     <para>
    ///         What this reports is REACHABILITY — how many models the endpoint
    ///         listed — and reachability is not authorization. It used to assign
    ///         <see cref="IsAuthenticated" /> from the model count and to clear
    ///         it when /models failed, so an authorized provider behind an empty
    ///         catalogue, or one behind a flaky network, read as "No key". The
    ///         auth verdict belongs to <see cref="IAuthResolver" />;
    ///         <see cref="SaveKeyAsync" /> asks it on the way in, and a probe
    ///         that fails changes only the probe's own line of text.
    ///     </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        var row = this;
        row.IsTesting = true;
        row.TestResult = "Testing…";
        try
        {
            // Persist the typed key first so the auth resolver (which reads
            // from CommonConfig on every call) sees the latest value.
            await SaveKeyAsync().ConfigureAwait(true);

            using var cts = new CancellationTokenSource(IProviderHealthCheck.DefaultTimeout);
            var modelsResult = await _providers.GetAllModelsAsync(cts.Token).ConfigureAwait(true);
            if (modelsResult.IsSuccess)
            {
                int listed = modelsResult.Value.Count(m => m.ProviderId == row.Id);
                row.TestResult = listed > 0
                    ? $"✓ {listed} model(s) available"
                    : row.RequiresApiKey
                        ? "✓ Reachable but no models returned"
                        : "✓ Reachable (no key needed)";
            }
            else
            {
                row.TestResult = $"✗ {modelsResult.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            // The number comes from the budget that actually elapsed, so the
            // message cannot drift away from the behaviour.
            int seconds = (int)IProviderHealthCheck.DefaultTimeout.TotalSeconds;
            row.TestResult = $"✗ Timed out after {seconds}s — is the provider running?";
        }
        catch (Exception ex)
        {
            row.TestResult = $"✗ {ex.Message}";
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    /// <summary>
    ///     Ask <see cref="IAuthResolver" /> whether this row's provider is
    ///     authorized (#671) — the same question, asked of the same abstraction,
    ///     so this row and the picker cannot disagree about one provider.
    /// </summary>
    /// <remarks>
    ///     <see cref="Result{T}.IsSuccess" /> is the whole verdict, read exactly
    ///     as the other call sites read it. A resolver that answers Success
    ///     answered "a usable key resolved"; re-deciding what a key looks like
    ///     here would be a second opinion, which is the bug.
    /// </remarks>
    private async Task<bool> IsAuthorizedAsync()
    {
        try
        {
            var resolved = await _auth.ResolveApiKeyAsync(Id).ConfigureAwait(true);
            return resolved.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth resolution threw for {Provider}", Id);
            return false;
        }
    }

    /// <summary>CanExecute for <see cref="TestConnectionCommand" /> — disabled while a test is in flight.</summary>
    /// <returns>True when no test is currently running.</returns>
    private bool CanTestConnection() => !IsTesting;
}
