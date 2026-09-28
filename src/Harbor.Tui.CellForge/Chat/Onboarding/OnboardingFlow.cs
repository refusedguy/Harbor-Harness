using System.Text;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Onboarding;

/// <summary>
/// First-run onboarding step of <see cref="OnboardingFlow"/>.
/// Linear: Welcome → Provider → Auth (skipped for local/already-configured
/// providers) → Model → Done. Any step can transition to
/// <see cref="OnboardingStep.Finished"/> via Esc.
/// </summary>
public enum OnboardingStep
{
    Idle,
    Welcome,
    Provider,
    Auth,
    Model,
    Done,
    Finished,
}

/// <summary>
/// Provider catalogue entry for the onboarding picker.
/// Data is injected by the host: the composition root maps
/// <c>Harbor.Application.Configuration.ProviderPresets.All</c> into these.
/// CellForge (Presentation) must not reference Harbor.Application, so the
/// catalogue crosses the layer boundary as plain data — see
/// <c>FullLayerMatrixTests</c>.
/// </summary>
/// <param name="Id">Stable lowercase provider id (e.g. <c>"kilocode"</c>).</param>
/// <param name="DisplayName">Human-readable name shown in the picker.</param>
/// <param name="DefaultModel">Default model id for this provider (without provider prefix).</param>
/// <param name="RequiresApiKey">Whether the provider needs an API key.</param>
/// <param name="SetupHint">Optional hint shown on the auth step (e.g. where to get a key).</param>
public sealed record OnboardingProvider(
    string Id,
    string DisplayName,
    string DefaultModel,
    bool RequiresApiKey,
    string? SetupHint);

/// <summary>
/// Terminal outcome of <see cref="OnboardingFlow"/>.
/// <c>Completed</c> is false when the user cancelled (Esc) — the host then
/// keeps its previous/default config. The flow never persists anything
/// itself: saving provider/model/key is the host's (policy/wiring) job.
/// </summary>
/// <param name="Completed">True when the user walked through to Done.</param>
/// <param name="ProviderId">Selected provider id (empty when cancelled).</param>
/// <param name="Model">Selected model as <c>provider/model</c> (empty when cancelled).</param>
/// <param name="ApiKey">Entered API key; null for local providers, already-configured providers, or cancellation.</param>
/// <param name="KeyAlreadyConfigured">True when the auth step was skipped because the provider already had a key.</param>
public sealed record OnboardingResult(
    bool Completed,
    string ProviderId,
    string Model,
    string? ApiKey,
    bool KeyAlreadyConfigured);

/// <summary>
/// First-run onboarding wizard ([UX8], epic #260) built on the existing
/// <see cref="DialogOverlay"/> primitives (Alert for init/done, Prompt for
/// provider/auth/model picks). The only genuinely new code in the epic;
/// everything else is policy/wiring owned by other slices.
/// </summary>
/// <remarks>
/// <para>
/// Scope: new onboarding flow ONLY. This controller owns step state and the
/// dialog content; it does not touch layout, blocks, approval, status, or
/// the chat-screen bridge. The host paints <see cref="Dialog"/> with the
/// regular <c>DialogOverlay.Paint</c> path (so no golden baselines change)
/// and forwards keys to <see cref="HandleKey"/> while the flow is active.
/// </para>
/// <para>
/// Step semantics mirror the console
/// <c>Harbor.Application.Onboarding.OnboardingWizard</c> (number-or-id
/// provider pick, auth skipped for local providers, empty model input keeps
/// the default, <c>provider/</c> prefix added unless present) minus the
/// agent pick — init/auth/model only, per issue #268.
/// </para>
/// </remarks>
public sealed class OnboardingFlow
{
    /// <summary>
    /// Cap on numbered picker entries. The dialog clips at
    /// <see cref="DialogOverlay.MaxHeight"/> rows, so long catalogues show
    /// the head plus an "…and N more (type the id)" tail — any valid id is
    /// accepted even when unlisted.
    /// </summary>
    public const int MaxListedEntries = 10;

    private const string CancelButtonId = "cancel";

    private readonly IReadOnlyList<OnboardingProvider> _providers;
    private readonly Dictionary<string, IReadOnlyList<string>> _liveModels;
    private readonly HashSet<string> _configured;

    private OnboardingProvider? _selected;
    private string _model = string.Empty;

    /// <summary>What the model prompt was prefilled with (preset default, or empty for live lists).</summary>
    private string _modelPrefill = string.Empty;
    private string? _apiKey;
    private bool _keyAlreadyConfigured;

    /// <summary>
    /// Create a flow over an injected provider catalogue.
    /// </summary>
    /// <param name="providers">Picker catalogue, recommendation-first order.</param>
    /// <param name="configuredProviderIds">
    /// Provider ids that already have a stored key — their auth step is
    /// skipped (mirrors the console wizard's "already set" branch).
    /// </param>
    /// <param name="liveModels">
    /// Optional live model lists per provider id (from
    /// <c>GetModelsAsync</c>). When present and non-empty for the selected
    /// provider, the model step shows a numbered list; otherwise it degrades
    /// to free text with the preset default prefilled.
    /// </param>
    public OnboardingFlow(
        IReadOnlyList<OnboardingProvider> providers,
        IReadOnlyCollection<string>? configuredProviderIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? liveModels = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        _configured = configuredProviderIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(configuredProviderIds, StringComparer.OrdinalIgnoreCase);
        _liveModels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (liveModels is not null)
        {
            foreach (var kv in liveModels)
            {
                _liveModels[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>The dialog primitive the host paints and the flow drives.</summary>
    public DialogOverlay Dialog { get; } = new();

    /// <summary>Current step; <see cref="OnboardingStep.Finished"/> is terminal.</summary>
    public OnboardingStep Step { get; private set; } = OnboardingStep.Idle;

    /// <summary>True once the flow reached a terminal outcome (completed or cancelled).</summary>
    public bool IsComplete => Step == OnboardingStep.Finished;

    /// <summary>Terminal outcome; null until <see cref="IsComplete"/>.</summary>
    public OnboardingResult? Result { get; private set; }

    /// <summary>Provider selected on the picker step; null until selected.</summary>
    public OnboardingProvider? SelectedProvider => _selected;

    /// <summary>Start the flow (shows the welcome alert). Ignored unless idle.</summary>
    public void Start()
    {
        if (Step != OnboardingStep.Idle)
        {
            return;
        }
        if (_providers.Count == 0)
        {
            Cancel();
            return;
        }
        Step = OnboardingStep.Welcome;
        Dialog.ShowAlert(
            "Welcome to Harbor",
            "Set up your AI coding agent in 30 seconds.\nEnter = next, Esc = cancel.",
            "Next");
    }

    /// <summary>
    /// Route a key to the flow. Esc cancels from any step; Enter commits the
    /// current step (or cancels when the Cancel button is focused); anything
    /// else goes to the dialog (prompt editing, Tab focus cycling).
    /// Returns false once the flow is finished or idle.
    /// </summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (IsComplete || Step == OnboardingStep.Idle)
        {
            return false;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            Cancel();
            return true;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            if (Dialog.Kind == DialogKind.Prompt && IsCancelFocused())
            {
                Cancel();
                return true;
            }
            CommitCurrentStep();
            return true;
        }
        return Dialog.HandleKey(key);
    }

    private bool IsCancelFocused()
    {
        var buttons = Dialog.Buttons;
        if (buttons.Count == 0)
        {
            return false;
        }
        int index = Math.Clamp(Dialog.FocusedButtonIndex, 0, buttons.Count - 1);
        return string.Equals(buttons[index].Id, CancelButtonId, StringComparison.Ordinal);
    }

    private void CommitCurrentStep()
    {
        switch (Step)
        {
            case OnboardingStep.Welcome:
                ShowProvider(null);
                break;
            case OnboardingStep.Provider:
                TryCommitProvider(Dialog.Input);
                break;
            case OnboardingStep.Auth:
                TryCommitAuth(Dialog.Input);
                break;
            case OnboardingStep.Model:
                TryCommitModel(Dialog.Input);
                break;
            case OnboardingStep.Done:
                FinishCompleted();
                break;
            case OnboardingStep.Idle:
            case OnboardingStep.Finished:
                break;
        }
    }

    private void ShowProvider(string? error)
    {
        Step = OnboardingStep.Provider;
        Dialog.ShowPrompt(
            "Pick a provider",
            BuildProviderMessage(error),
            string.Empty,
            "Next",
            "Cancel");
    }

    private string BuildProviderMessage(string? error)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(error))
        {
            sb.Append(error).Append('\n');
        }
        int shown = Math.Min(_providers.Count, MaxListedEntries);
        for (int i = 0; i < shown; i++)
        {
            var p = _providers[i];
            string tag = p.RequiresApiKey ? string.Empty : " (no key needed)";
            sb.Append("  [").Append(i + 1).Append("] ").Append(p.Id).Append(" — ").Append(p.DisplayName).Append(tag).Append('\n');
        }
        if (shown < _providers.Count)
        {
            sb.Append("  …and ").Append(_providers.Count - shown).Append(" more (type the id)\n");
        }
        sb.Append("Type a number or id (Esc to cancel).");
        return sb.ToString();
    }

    private void TryCommitProvider(string rawInput)
    {
        string input = rawInput.Trim();
        if (input.Length == 0)
        {
            ShowProvider("Type a number or id to continue.");
            return;
        }
        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= _providers.Count)
        {
            SelectProvider(_providers[idx - 1]);
            return;
        }
        var byId = FindProvider(input);
        if (byId is not null)
        {
            SelectProvider(byId);
            return;
        }
        ShowProvider($"Unknown provider '{input}'. Type a number or id.");
    }

    private OnboardingProvider? FindProvider(string id)
    {
        for (int i = 0; i < _providers.Count; i++)
        {
            if (string.Equals(_providers[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return _providers[i];
            }
        }
        return null;
    }

    private void SelectProvider(OnboardingProvider provider)
    {
        _selected = provider;
        _apiKey = null;
        _keyAlreadyConfigured = false;
        if (!provider.RequiresApiKey || _configured.Contains(provider.Id))
        {
            _keyAlreadyConfigured = provider.RequiresApiKey && _configured.Contains(provider.Id);
            ShowModel(null);
            return;
        }
        ShowAuth(null);
    }

    private void ShowAuth(string? error)
    {
        Step = OnboardingStep.Auth;
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(error))
        {
            sb.Append(error).Append('\n');
        }
        if (_selected?.SetupHint is not null)
        {
            sb.Append(_selected.SetupHint).Append('\n');
        }
        sb.Append("Paste the API key and press Enter (Esc to cancel).");
        Dialog.ShowPrompt($"API key — {_selected?.Id}", sb.ToString(), string.Empty, "Next", "Cancel");
    }

    private void TryCommitAuth(string rawInput)
    {
        string input = rawInput.Trim();
        if (input.Length == 0)
        {
            ShowAuth("Key must not be empty (Esc to cancel).");
            return;
        }
        _apiKey = input;
        ShowModel(null);
    }

    private void ShowModel(string? error)
    {
        Step = OnboardingStep.Model;
        string providerId = _selected?.Id ?? string.Empty;
        string defaultModel = $"{providerId}/{_selected?.DefaultModel}";
        if (_liveModels.TryGetValue(providerId, out var live) && live.Count > 0)
        {
            _modelPrefill = string.Empty;
            Dialog.ShowPrompt($"Pick a model — {providerId}", BuildLiveModelMessage(error, live, defaultModel), string.Empty, "Done", "Cancel");
            return;
        }
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(error))
        {
            sb.Append(error).Append('\n');
        }
        sb.Append("Default model: ").Append(defaultModel).Append('\n');
        sb.Append("Press Enter for default, or type a model name.");
        _modelPrefill = _selected?.DefaultModel ?? string.Empty;
        Dialog.ShowPrompt($"Pick a model — {providerId}", sb.ToString(), _modelPrefill, "Done", "Cancel");
    }

    private string BuildLiveModelMessage(string? error, IReadOnlyList<string> live, string defaultModel)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(error))
        {
            sb.Append(error).Append('\n');
        }
        int shown = Math.Min(live.Count, MaxListedEntries);
        for (int i = 0; i < shown; i++)
        {
            sb.Append("  [").Append(i + 1).Append("] ").Append(live[i]).Append('\n');
        }
        if (shown < live.Count)
        {
            sb.Append("  …and ").Append(live.Count - shown).Append(" more (type the id)\n");
        }
        sb.Append("Type a number or id, Enter for ").Append(defaultModel).Append('.');
        return sb.ToString();
    }

    private void TryCommitModel(string rawInput)
    {
        if (_selected is null)
        {
            ShowProvider("Pick a provider first.");
            return;
        }
        string providerId = _selected.Id;
        string defaultModel = $"{providerId}/{_selected.DefaultModel}";
        string input = rawInput.Trim();
        if (input.Length == 0)
        {
            FinishModel(defaultModel);
            return;
        }
        // The prompt is prefilled with the preset default and DialogOverlay
        // appends typed text to it: untouched prefill keeps the default, an
        // input extending the prefill carries a user-typed suffix — interpret
        // the suffix (replacement, not append), so a full slash-id wins over
        // the default and the provider prefix is joined with '/'.
        if (input.Equals(_modelPrefill, StringComparison.Ordinal))
        {
            FinishModel(defaultModel);
            return;
        }
        if (_modelPrefill.Length > 0 && input.StartsWith(_modelPrefill, StringComparison.Ordinal))
        {
            input = input[_modelPrefill.Length..].Trim();
            if (input.Length == 0)
            {
                FinishModel(defaultModel);
                return;
            }
        }
        if (_liveModels.TryGetValue(providerId, out var live) && live.Count > 0
            && int.TryParse(input, out int idx) && idx >= 1 && idx <= live.Count)
        {
            FinishModel($"{providerId}/{live[idx - 1]}");
            return;
        }
        if (!input.Contains('/'))
        {
            FinishModel($"{providerId}/{input}");
            return;
        }
        FinishModel(input);
    }

    private void FinishModel(string model)
    {
        _model = model;
        Step = OnboardingStep.Done;
        string keyLine = _selected is not null && !_selected.RequiresApiKey
            ? "API key: not needed (local)"
            : _keyAlreadyConfigured
                ? "API key: already configured"
                : "API key: saved";
        Dialog.ShowAlert(
            "Setup complete!",
            $"Provider: {_selected?.Id}\nModel: {model}\n{keyLine}\nPress Enter to start.",
            "Start");
    }

    private void FinishCompleted()
    {
        Result = new OnboardingResult(
            true,
            _selected?.Id ?? string.Empty,
            _model,
            _apiKey,
            _keyAlreadyConfigured);
        Step = OnboardingStep.Finished;
        Dialog.Dismiss();
    }

    private void Cancel()
    {
        Result = new OnboardingResult(false, string.Empty, string.Empty, null, false);
        Step = OnboardingStep.Finished;
        Dialog.Dismiss();
    }
}
