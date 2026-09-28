namespace Harbor.Ui.Framework.State;

/// <summary>
///     Turns an <see cref="AsyncData{T}" /> snapshot into view-model state. A
///     view-model that binds a list to an <see cref="AsyncFeed{T}" /> implements
///     <see cref="IAsyncDataSink{TItem}" /> and forwards every
///     <see cref="AsyncFeed{T}.Changed" /> payload here, so the status → action
///     mapping (including <i>where</i> a failure is surfaced) is written exactly
///     once instead of copy-pasted per view-model.
/// </summary>
/// <remarks>
///     The feed's item type is always a list, hence the <c>IReadOnlyList</c>
///     constraint: the sink swaps one collection for another and never has to
///     re-check a <c>HasValue</c> guard that the status already implies.
/// </remarks>
public static class AsyncDataBinder
{
    /// <summary>
    ///     Text handed to the sink when a feed reports
    ///     <see cref="AsyncStatus.Error" /> without a message.
    /// </summary>
    public const string UnknownError = "Unknown error";

    /// <summary>Route a snapshot to the matching <see cref="IAsyncDataSink{TItem}" /> arm.</summary>
    /// <param name="data">The snapshot published by the feed.</param>
    /// <param name="sink">The view-model surface to update.</param>
    /// <typeparam name="TItem">Row type held by the bound collection.</typeparam>
    public static void Apply<TItem>(AsyncData<IReadOnlyList<TItem>> data, IAsyncDataSink<TItem> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        switch (data.Status)
        {
            case AsyncStatus.Loading:
            case AsyncStatus.Refreshing:
                sink.OnLoading();
                break;
            case AsyncStatus.Success:
                sink.OnLoaded(data.Value ?? Array.Empty<TItem>());
                break;
            case AsyncStatus.Error:
                sink.OnError(data.Error is { } error && error.Trim().Length > 0 ? error : UnknownError);
                break;
            case AsyncStatus.Idle:
            case AsyncStatus.None:
                break;
        }
    }
}

/// <summary>
///     Receives the arms <see cref="AsyncDataBinder.Apply{TItem}" /> routes.
///     Each view-model decides what "busy", "loaded" and "failed" mean for its
///     own collections; the status → arm mapping stays in one place.
/// </summary>
/// <typeparam name="TItem">Row type held by the bound collection.</typeparam>
public interface IAsyncDataSink<in TItem>
{
    /// <summary>
    ///     A load started (<see cref="AsyncStatus.Loading" />) or a reload
    ///     started over existing rows (<see cref="AsyncStatus.Refreshing" />).
    ///     Drop stale rows so the surface cannot show last load's data as if
    ///     it were current.
    /// </summary>
    void OnLoading();

    /// <summary>
    ///     The load succeeded. Replace the visible rows with
    ///     <paramref name="items" /> and clear any previous error — an empty
    ///     list is a real answer, not a failure, and belongs in the rows'
    ///     own empty state.
    /// </summary>
    /// <param name="items">The rows to display (possibly empty).</param>
    void OnLoaded(IReadOnlyList<TItem> items);

    /// <summary>
    ///     The load failed. Make the failure visible — a provider that could
    ///     not be reached must never look like a provider with no models.
    /// </summary>
    /// <param name="message">Non-empty failure text; never <see langword="null" />.</param>
    void OnError(string message);
}
