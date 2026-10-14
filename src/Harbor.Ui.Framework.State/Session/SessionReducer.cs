namespace Harbor.Ui.Framework.State;

/// <summary>
///     Session-hydration transition of the chat reducer: the atomic
///     Reset + session-chrome bind + history replay (#89) — a pure fold over
///     <c>Chat</c>, extracted verbatim from <see cref="ChatAppReducer" /> (#95).
///     The host runs the effects; nothing here touches I/O or DI.
/// </summary>
/// <remarks>
///     <see cref="ChatAppReducer" /> keeps a thin forwarder over the entry point,
///     so existing call sites, tests and doc references keep resolving to the
///     same signature with the same behaviour.
/// </remarks>
public static class SessionReducer
{
    /// <summary>
    ///     Atomic session hydration (#89). Folds Reset + session-chrome bind +
    ///     history replay into one pure transition so the swap rides a single
    ///     store CAS: a concurrent background event applies strictly before
    ///     (superseded by the fresh state) or after (appended in order), never
    ///     interleaved mid-history.
    /// </summary>
    internal static UiState HydrateSession(UiState state, ChatAppMsg.HydrateSession h)
    {
        var next = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Model = h.Model,
                Provider = h.Provider,
                AgentName = h.AgentName,
                // The status the core persisted for this session, read rather
                // than re-derived from the replayed lines (#687) — the switch
                // path had the same defect the live path had.
                SessionStatus = h.Status,
                // The tab strip is workspace chrome, not transcript: hydrating a
                // session (i.e. switching to it) must not close the open tabs.
                TabStrip = state.Chat.TabStrip,
            },
        };
        foreach (var line in h.Lines)
            next = next.AddLine(line.Role, line.Text, line.ToolCallId);
        return next;
    }
}
