namespace Harbor.Ipc.Protocol;

/// <summary>
///     Adapter that exposes the RPC client's event channel as an
///     <see cref="IAsyncEnumerable{HarborEvent}" /> for
///     <see cref="IHarborClient.SubscribeToEventsAsync" />.
/// </summary>
public sealed class EventSubscription
{
    private readonly MessagePackRpcClient _client;

    /// <summary>
    ///     Construct an event subscription over the given RPC client.
    /// </summary>
    public EventSubscription(MessagePackRpcClient client)
    {
        _client = client;
    }

    /// <summary>
    ///     Enumerate events from the client's event channel until
    ///     <paramref name="ct" /> is cancelled or the channel completes.
    /// </summary>
    public async IAsyncEnumerable<HarborEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _client.EventReader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}
