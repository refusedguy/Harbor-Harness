// Hosting layer — a loaded script instance with evaluation metadata. See ScriptHost.cs.
namespace Harbor.Scripting.Hosting;
/// <summary>
///     Represents a script that was loaded into the host: its source entry,
///     the compiled form (post-compiler), and the evaluation outcome.
/// </summary>
public sealed record ScriptInstance
{
    /// <summary>Source entry as read from the store (name, path, content, hash).</summary>
    public required ScriptEntry Source { get; init; }

    /// <summary>Compiled source after the compiler ran (may equal <see cref="ScriptEntry.Content" />).</summary>
    public required string Compiled { get; init; }

    /// <summary>True if the engine evaluation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Error message if <see cref="Succeeded" /> is false; otherwise <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>How long the engine spent evaluating the compiled source.</summary>
    public TimeSpan Elapsed { get; init; }
}
