using Harbor.Abstractions.Interaction;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Interaction;

/// <summary>
///     The default <see cref="IUserQuestionService" />: it hands the batch to
///     the host's presenter and holds the wait window (issue #1759).
/// </summary>
/// <remarks>
///     <para>
///         The service owns the TIMING and nothing else. What the human sees
///         and how they answer it belongs to the presenter (the host's
///         terminal card, its console prompt, its future fullscreen widget)
///         and the service only ever sees the outcome. That split is the same
///         one the permission channel draws: <c>PermissionService</c> holds the
///         verdict, the asker holds the card.
///     </para>
///     <para>
///         The wait window is FINITE, always. A question that waits forever is
///         a run that never ends, so an expired window ends the question with
///         <see cref="UserQuestionStatus.Pending" /> and the model is told, in
///         the result it reads back, that nobody answered YET. The model may
///         ask again; what it may not do is wait.
///     </para>
///     <para>
///         <b>Cancellation is not a verdict.</b> A cancelled caller
///         (the <c>ct</c> argument) gets an exception, not a "no answer": the
///         same rule the permission channel follows, and the reason: a stop
///         must never be reported to the model as a decision the human made.
///     </para>
/// </remarks>
public sealed class UserQuestionService : IUserQuestionService
{
    /// <summary>
    ///     The wait window a request gets when it names none (120 s, the
    ///     window the interaction contract settled on).
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(120);

    /// <summary>
    ///     The longest window a question may hold a run (10 minutes). A request
    ///     that asks for more gets this one: the run's own wall-clock budget
    ///     is the same order, and a question that outlasts it has already lost
    ///     the turn it was asked in.
    /// </summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(10);

    private readonly ILogger<UserQuestionService> _logger;

    /// <summary>
    ///     The host's presenter, attached once at composition time. Null means
    ///     this host has no interactive human (a headless run, a pipe, CI) and
    ///     every question is answered
    ///     <see cref="UserQuestionStatus.Unavailable" />.
    /// </summary>
    private Func<UserQuestionRequest, CancellationToken, Task<UserQuestionOutcome>>? _presenter;

    /// <summary>
    ///     Construct a service with no presenter. The host attaches one with
    ///     <see cref="AttachPresenter" /> once it knows which UI it has.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public UserQuestionService(ILogger<UserQuestionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Attach the host's presenter, the surface that shows the question
    ///     and collects the answer.
    /// </summary>
    /// <param name="presenter">
    ///     The presenter. It is handed the request and the caller's token and
    ///     returns the outcome; it may take as long as the request's window
    ///     allows, and the service stops waiting when the window closes.
    /// </param>
    /// <remarks>
    ///     Attached once, before the first question: the field is written
    ///     through <see cref="Volatile.Write{T}(ref T, T)" /> and read through
    ///     <see cref="Volatile.Read{T}(ref T)" />, so a question asked while the
    ///     host is still wiring up sees either the old (null) or the new
    ///     presenter, never a torn one.
    /// </remarks>
    public void AttachPresenter(
        Func<UserQuestionRequest, CancellationToken, Task<UserQuestionOutcome>> presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        Volatile.Write(ref _presenter, presenter);
    }

    /// <inheritdoc />
    public async Task<UserQuestionOutcome> AskAsync(
        UserQuestionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var presenter = Volatile.Read(ref _presenter);
        if (presenter is null)
        {
            UserQuestionServiceLog.NoPresenter(_logger, request.Questions.Count);
            return UserQuestionOutcome.Unavailable(
                "This host has no interactive user to answer questions; ask only when a human can reply.");
        }

        TimeSpan window = Clamp(request.Timeout);

        // The presenter is handed the EFFECTIVE window, not the one that was
        // asked for: a surface that draws its own countdown must count down
        // the window the service will actually wait.
        var effective = request with { Timeout = window };

        // The presenter's token is LINKED to the caller's, never the caller's
        // itself: the service cancels it when the window closes, and that must
        // not cancel the run.
        using var presenterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<UserQuestionOutcome> presented = presenter(effective, presenterCts.Token);

        Task delay = Task.Delay(window, presenterCts.Token);
        Task first = await Task.WhenAny(presented, delay).ConfigureAwait(false);

        // The presenter won the race, or finished in the same tick the window
        // closed, which is an answer and not a timeout.
        if (ReferenceEquals(first, presented) || presented.IsCompletedSuccessfully)
        {
            CancelQuietly(presenterCts);
            return await presented.ConfigureAwait(false);
        }

        // The window closed (or the run was cancelled) with no answer. The
        // presenter's task is still in flight: observe it so a late failure is
        // logged rather than unobserved (§FP-003).
        Observe(presented);

        if (ct.IsCancellationRequested)
        {
            // A cancelled caller never gets a verdict: the cancellation
            // propagates instead of collapsing into "no answer".
            CancelQuietly(presenterCts);
            ct.ThrowIfCancellationRequested();
        }

        CancelQuietly(presenterCts);
        UserQuestionServiceLog.WindowExpired(_logger, window.TotalSeconds, request.Questions.Count);
        return UserQuestionOutcome.Pending(
            $"No answer within {window.TotalSeconds:0}s: the human has not answered yet. "
            + "Ask again if you still need the answer, or continue with what you have.");
    }

    /// <summary>
    ///     The window a request actually gets: the one it asked for, the
    ///     default when it asked for none, and never more than
    ///     <see cref="MaxWindow" />.
    /// </summary>
    /// <param name="requested">The window the request named, if any.</param>
    /// <returns>The effective window.</returns>
    private static TimeSpan Clamp(TimeSpan? requested)
    {
        if (requested is not { } value || value <= TimeSpan.Zero)
        {
            return DefaultWindow;
        }

        return value > MaxWindow ? MaxWindow : value;
    }

    /// <summary>
    ///     Cancel the presenter's token without letting a misbehaving
    ///     cancellation callback break the outcome.
    /// </summary>
    /// <param name="cts">The source to cancel.</param>
    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (AggregateException)
        {
            // A callback the presenter registered threw. The window is what
            // matters here, and it has already closed; the throw is not the
            // caller's problem.
        }
    }

    /// <summary>
    ///     Observe a task nobody awaits any more, so its failure is logged
    ///     instead of surfacing as an unobserved task exception.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    private void Observe(Task<UserQuestionOutcome> task)
    {
        _ = task.ContinueWith(
            t => UserQuestionServiceLog.PresenterFailed(_logger, t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="UserQuestionService" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class UserQuestionServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "ask_user_question: no presenter attached, {QuestionCount} question(s) cannot be asked")]
    public static partial void NoPresenter(ILogger logger, int questionCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ask_user_question: no answer within {WindowSeconds:0}s, {QuestionCount} question(s) left open")]
    public static partial void WindowExpired(ILogger logger, double windowSeconds, int questionCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "ask_user_question: the presenter failed after the wait window closed")]
    public static partial void PresenterFailed(ILogger logger, Exception ex);
}
