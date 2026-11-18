namespace Harbor.Abstractions.Dsl;

/// <summary>One recorded builder call: a name and its arguments.</summary>
public sealed class SpecOp
{
    /// <summary>Creates an operation with the given name and arguments.</summary>
    /// <param name="name">Operation name, for example "new" or "height".</param>
    /// <param name="arguments">Arguments in source order.</param>
    /// <remarks>The source span is unknown and recorded as 0:0.</remarks>
    public SpecOp(string name, IReadOnlyList<SpecValue> arguments)
        : this(name, arguments, 0, 0)
    {
    }

    /// <summary>Creates an operation with a name, arguments and a 1-based source span.</summary>
    /// <param name="name">Operation name, for example "new" or "height".</param>
    /// <param name="arguments">Arguments in source order.</param>
    /// <param name="sourceLine">Line in the markup file, one-based.</param>
    /// <param name="sourceColumn">Column of the operation name, one-based.</param>
    public SpecOp(
        string name,
        IReadOnlyList<SpecValue> arguments,
        int sourceLine,
        int sourceColumn)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        SourceLine = sourceLine;
        SourceColumn = sourceColumn;
    }

    /// <summary>Operation name.</summary>
    public string Name { get; }

    /// <summary>Arguments in source order.</summary>
    public IReadOnlyList<SpecValue> Arguments { get; }

    /// <summary>Line in the markup file, one-based. Zero when constructed without a span.</summary>
    public int SourceLine { get; }

    /// <summary>Column of the operation name in the markup file, one-based. Zero when constructed without a span.</summary>
    public int SourceColumn { get; }
}
