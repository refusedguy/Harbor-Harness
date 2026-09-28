using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Narrow <see cref="ITuiEffectRunner" /> for the CellForge REPL's tab-strip
///     effects (#389): <see cref="TuiEffect.ActivateSession" /> and
///     <see cref="TuiEffect.RequestOpenSession" />.
/// </summary>
/// <remarks>
///     <para>
///         The REPL deliberately does not use <see cref="TuiEffectHost" />:
///         submit, abort and quit are executed as host gestures (composer buffer,
///         <c>HandleAbortGesture</c>, the Ctrl+C×2 chord) because dispatching them
///         from a store effect as well would fire each gesture twice. The
///         tab-strip effects have no such gesture twin — the key path has nothing
///         else to do — so they are exactly the set that must be run from here.
///     </para>
///     <para>
///         Everything else is ignored on purpose rather than deferred: the REPL
///         owns those paths, and swallowing an unrecognised effect keeps a newly
///         added one from crashing the frame loop mid-frame.
///     </para>
/// </remarks>
internal sealed class ReplTabEffectRunner(
    Func<string, Task> switchToSession,
    Action openSessionPalette,
    Action<Exception>? onError = null) : ITuiEffectRunner
{
    /// <summary>
    ///     Runs the tab effects. <see cref="TuiEffect.ActivateSession" /> goes
    ///     through the existing session-switch coordinator, whose guard (agent
    ///     busy / already there) announces and returns — so a rejected switch
    ///     leaves the tab focused and the transcript untouched rather than
    ///     half-applied, and the next session-list sync repairs the strip.
    /// </summary>
    public void Run(TuiEffect effect)
    {
        switch (effect)
        {
            case TuiEffect.ActivateSession activate:
                TaskFireAndForget.Forget(
                    switchToSession(activate.SessionId.Value),
                    onError);
                break;
            case TuiEffect.RequestOpenSession:
                // The REPL's "open / switch a session" surface is the sessions
                // palette, so Ctrl+T lands the user where /sessions would.
                openSessionPalette();
                break;
            default:
                break;
        }
    }
}
