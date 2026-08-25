namespace Harbor.Abstractions.Results;

/// <summary>
///     Canonical error handler for all <c>Try*</c> conversions (CSE bible §4.5):
///     cancellation is NOT an error — it propagates so Esc semantics stay
///     intact; everything else collapses to <see cref="Exception.Message" />.
/// </summary>
public static class ResultErrors
{
    /// <summary>Rethrow OCE (Esc ≠ domain failure), else the exception message.</summary>
    public static string Message(Exception ex) =>
        ex is OperationCanceledException ? throw ex : ex.Message;
}
