using System.Linq;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     serves as API for the focus-session panel view-model (upon_layers_featureFlags):
///     a pure projection of the active session's headline stats (title, model,
///     provider, agent, message/token counts) shown in the Focus overlay on
///     every desktop app. Zero behaviour — the shell VM copies values in when
///     the overlay opens.
/// </summary>
public abstract partial class FocusSessionViewModelBase : StoreSubscriberViewModel
{
    [ObservableProperty]
    private string _agent = string.Empty;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _title = "Focus Session";

    [ObservableProperty]
    private long _tokensIn;

    [ObservableProperty]
    private long _tokensOut;

    /// <summary>Construct a <see cref="FocusSessionViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="logger">Logger.</param>
    protected FocusSessionViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.AgentName, v => Agent = v);
        Select(state => state.Model, v => Model = v);
        Select(state => state.Provider, v => Provider = v);
        Select(state => state.Cost.TokensIn, v => TokensIn = v);
        Select(state => state.Cost.TokensOut, v => TokensOut = v);
        Select(state => state.Lines.Length, v => MessageCount = v);
        Select(state =>
        {
            var title = "Focus Session";
            if (state.ActiveSessionId is SessionId id)
            {
                var sessions = state.Sessions;
                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i].SessionId.Equals(id))
                    {
                        title = sessions[i].Title;
                        break;
                    }
                }
            }
            return title;
        }, v => Title = v);
    }

    /// <summary>
    ///     Called when the global <see cref="UiState" /> changes. Applies all
    ///     declared selectors to project state slices into view-model properties.
    /// </summary>
    /// <param name="state">The current UI state snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}
