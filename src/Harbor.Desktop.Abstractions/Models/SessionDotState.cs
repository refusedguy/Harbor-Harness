using Harbor.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.Models;

/// <summary>
///     Presentation state of a session dot — the vocabulary
///     <c>StatusDot</c> understands (it indexes its pseudo-class and brush
///     table by this enum). A narrower set than <see cref="SessionStatus" />:
///     it has no <c>Aborted</c> of its own, and adds <c>Thinking</c> /
///     <c>Queued</c> for the running animation.
/// </summary>
public enum SessionDotState
{
    Idle,
    Running,
    Thinking,
    Queued,
    Done,
    Error
}

/// <summary>
///     The one place a <see cref="SessionStatus" /> becomes a
///     <see cref="SessionDotState" />.
/// </summary>
/// <remarks>
/// <para>
///     #663. The translation used to be a <c>switch</c> on
///     <c>SessionCardViewModel</c>. That made a view model a second authority
///     on what a status looks like: the same <c>Working</c> status was
///     "MochaYellow" through <c>StatusMappers</c> and
///     <c>AccentPrimaryBrush</c> through the row view model, with a comment
///     declaring the difference intentional. Two UI layers, two answers, each
///     documented as correct.
/// </para>
/// <para>
///     It lives beside the enum rather than in <c>StatusMappers</c> because
///     <c>StatusMappers</c> is in <c>Harbor.Ui.Framework.ViewModels</c>, which
///     does not reference this project — a translation to a Desktop-layer type
///     cannot go in the layer above the type it produces. One owner, one
///     place: this static class is the only writer of that mapping.
/// </para>
/// <para>
///     It does NOT claim to be a status→COLOUR table. <see cref="SessionStatus" />
///     to brush resource key is <c>StatusMappers.SessionStatusToBrushKey</c>,
///     and that is the one <c>Harbor.Architecture.Tests.SessionStatusTableRule</c>
///     guards. This enum feeds a control's pseudo-class and its own animation,
///     so the running/aborted collapse below is a decision about the DOT, not a
///     second opinion about the colour.
/// </para>
/// </remarks>
public static class SessionDotStates
{
    /// <summary>
    ///     Map a session status to the dot state. <see cref="SessionStatus.Aborted" />
    ///     reads as <see cref="SessionDotState.Error" />: the dot has no stopped
    ///     state, and an aborted session is the one the user most needs to notice.
    /// </summary>
    public static SessionDotState FromStatus(SessionStatus status) => status switch
    {
        SessionStatus.Working => SessionDotState.Running,
        SessionStatus.Done => SessionDotState.Done,
        SessionStatus.Error or SessionStatus.Aborted => SessionDotState.Error,
        _ => SessionDotState.Idle
    };
}
