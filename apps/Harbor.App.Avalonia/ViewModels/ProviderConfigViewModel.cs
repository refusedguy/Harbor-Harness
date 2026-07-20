using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Per-provider configuration row view-model. One instance per
///     registered provider — holds the editable API key, the live auth
///     status, and the Save / Test commands that persist + verify the key.
/// </summary>
/// <remarks>
///     <para>
///         Extracted from <c>SettingsViewModel</c> so the per-provider
///         save / test logic is unit-testable in isolation (construct a
///         <see cref="ProviderConfigViewModel"/> with a fake
///         <see cref="ICommonConfigStore"/> + <see cref="IProviderRegistry"/>
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
public sealed partial class ProviderConfigViewModel : ObservableObject
{
    private readonly ICommonConfigStore _commonStore;
    private readonly IProviderRegistry _providers;
    private readonly IAuthResolver _authResolver;
    private readonly ToastService _toasts;
    private readonly ILogger<ProviderConfigViewModel> _logger;

    /// <summary>Construct a per-provider config row.</summary>
    /// <param name="id">Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</param>
    /// <param name="displayName">Human-readable display name.</param>
    /// <param name="apiKey">Persisted API key (or empty).</param>
    /// <param name="requiresApiKey">Whether this provider requires an API key (false for Ollama).</param>
    /// <param name="isAuthenticated">Initial auth-status (true if a usable key was resolved).</param>
    /// <param name="commonStore">The common-config store (for persisting the key).</param>
    /// <param name="providers">The provider registry (for Test connection).</param>
    /// <param name="authResolver">The auth resolver (for re-checking auth after save).</param>
    /// <param name="toasts">Toast service (for user feedback).</param>
    /// <param name="logger">Logger.</param>
    public ProviderConfigViewModel(
        string id,
        string displayName,
        string apiKey,
        bool requiresApiKey,
        bool isAuthenticated,
        ICommonConfigStore commonStore,
        IProviderRegistry providers,
        IAuthResolver authResolver,
        ToastService toasts,
        ILogger<ProviderConfigViewModel> logger)
    {
        Id = id;
        DisplayName = displayName;
        _apiKey = apiKey;
        RequiresApiKey = requiresApiKey;
        _isAuthenticated = isAuthenticated;
        _commonStore = commonStore;
        _providers = providers;
        _authResolver = authResolver;
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
    public string AuthIcon => IsAuthenticated ? "✓" : (RequiresApiKey ? "✗" : "—");

    /// <summary>Auth status text — e.g. "Authenticated", "No key".</summary>
    public string AuthText => !RequiresApiKey
        ? "No key needed"
        : (IsAuthenticated ? "Authenticated" : "No key");

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
    private bool _isTesting;

    /// <summary>Human-readable result of the last Test connection attempt.</summary>
    [ObservableProperty]
    private string _testResult = string.Empty;

    /// <summary>
    ///     Save this provider's API key immediately, without requiring the
    ///     user to click the global "Save" button. Persists via
    ///     <see cref="ICommonConfigStore.UpdateAsync"/> so the key is available
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
            // Re-resolve auth status so the row's badge updates.
            try
            {
                row.IsAuthenticated = _authResolver.ResolveApiKeyAsync(row.Id).GetAwaiter().GetResult().IsSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Re-resolve auth failed for {Provider}", row.Id);
            }
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
    ///     Test this provider's connection by fetching its model list
    ///     with a 5-second timeout. Persists the row's current API key first
    ///     (so the test sees the value the user just typed, not the on-disk
    ///     one). Used by the "Test" button on each provider row.
    /// </summary>
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var modelsResult = await _providers.GetAllModelsAsync(cts.Token).ConfigureAwait(true);
            if (modelsResult.IsSuccess)
            {
                int count = modelsResult.Value.Count(m => m.ProviderId == row.Id);
                row.IsAuthenticated = count > 0 || !row.RequiresApiKey;
                row.TestResult = count > 0
                    ? $"✓ {count} model(s) available"
                    : (row.RequiresApiKey ? "✓ Reachable but no models returned" : "✓ Reachable (no key needed)");
            }
            else
            {
                row.IsAuthenticated = false;
                row.TestResult = $"✗ {modelsResult.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            row.TestResult = "✗ Timed out after 5s — is the provider running?";
        }
        catch (Exception ex)
        {
            row.TestResult = $"✗ {ex.Message}";
        }
        finally
        {
            row.IsTesting = false;
            TestConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>CanExecute for <see cref="TestConnectionCommand"/> — disabled while a test is in flight.</summary>
    /// <returns>True when no test is currently running.</returns>
    private bool CanTestConnection() => !IsTesting;
}
