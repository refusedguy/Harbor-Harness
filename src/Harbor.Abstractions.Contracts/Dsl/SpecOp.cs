namespace Harbor.Abstractions.Dsl;

/// <summary>One recorded builder call: a name and its arguments.</summary>
public sealed class SpecOp
{
    /// <summary>Creates an operation with the given name and arguments.</summary>
    /// <param name="name">Operation name, for example "new" or "height".</param>
    /// <param name="arguments">Arguments in source order.</param>
    public SpecOp(string name, IReadOnlyList<SpecValue> arguments)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    /// <summary>Operation name.</summary>
    public string Name { get; }

    /// <summary>Arguments in source order.</summary>
    public IReadOnlyList<SpecValue> Arguments { get; }
}
