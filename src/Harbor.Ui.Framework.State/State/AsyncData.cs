using CSharpFunctionalExtensions;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Immutable snapshot of an async operation state. Combines status, value,
///     and error into a single value type — eliminates the fragile trio
///     <c>IsLoading</c> / <c>ErrorMessage</c> / <c>Count == 0</c>.
/// </summary>
public readonly record struct AsyncData<T>(
    AsyncStatus Status,
    T? Value = default,
    string? Error = null)
{
    public static readonly AsyncData<T> Idle = new(AsyncStatus.Idle);

    public AsyncData<T> ToLoading() => Status is AsyncStatus.Success
        ? new(AsyncStatus.Refreshing, Value)
        : new(AsyncStatus.Loading);

    public static AsyncData<T> Success(T value) => new(AsyncStatus.Success, value);
    public static AsyncData<T> None() => new(AsyncStatus.None);
    public static AsyncData<T> Failed(string e) => new(AsyncStatus.Error, default, e);

    public static AsyncData<T> From(Result<T> r) => r.IsSuccess ? Success(r.Value) : Failed(r.Error);

    public bool IsBusy => Status is AsyncStatus.Loading or AsyncStatus.Refreshing;
    public bool HasValue => Status is AsyncStatus.Success or AsyncStatus.Refreshing && Value is not null;
}

/// <summary>Lifecycle of an async data operation.</summary>
public enum AsyncStatus : byte
{
    Idle,
    Loading,
    Refreshing,
    Success,
    None,
    Error
}
