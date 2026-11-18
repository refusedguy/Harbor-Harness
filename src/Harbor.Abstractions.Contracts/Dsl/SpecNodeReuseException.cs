namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Thrown when a node is attached or claimed a second time. A reuse is a
///     programming error, not a recoverable result: silently accepting it
///     would leave two parents or two consumers pointing at one node.
/// </summary>
public sealed class SpecNodeReuseException : InvalidOperationException
{
    /// <summary>Creates the error without a message.</summary>
    public SpecNodeReuseException()
    {
    }

    /// <summary>Creates the error with a message that names the reused node.</summary>
    /// <param name="message">Description of the reuse.</param>
    public SpecNodeReuseException(string message) : base(message)
    {
    }

    /// <summary>Creates the error with a message and an inner exception.</summary>
    /// <param name="message">Description of the reuse.</param>
    /// <param name="innerException">Cause of the reuse.</param>
    public SpecNodeReuseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
