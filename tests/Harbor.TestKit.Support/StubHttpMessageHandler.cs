namespace Harbor.TestKit;

/// <summary>
///     An <see cref="HttpMessageHandler" /> that answers from a function the
///     test supplies, and records what it was asked.
/// </summary>
/// <remarks>
///     <para>
///         Five hand-written HTTP handlers existed across three projects, and
///         every one of them was the same object with a different reason to
///         exist: one recorded the requests, one counted them, one let the
///         responder throw to simulate a transport error, and one threw on
///         its own to prove that a code path never reached the network.
///     </para>
///     <para>
///         The responder IS the difference, so it stays the only parameter:
///         a handler that counts is a handler whose responder counts, and a
///         handler that explodes is a handler whose responder explodes.
///     </para>
/// </remarks>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>
    ///     Creates a handler that answers with the given responder.
    /// </summary>
    /// <param name="responder">
    ///     Produces the response for a request. It may throw — the throw is
    ///     surfaced as a faulted task, which is what a transport error looks
    ///     like to a caller.
    /// </param>
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>
    ///     The requests this handler was asked about, in order.
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> CapturedRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>How many requests this handler was asked about.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls++;
            _requests.Add(request);
        }

        try
        {
            return Task.FromResult(_responder(request));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
