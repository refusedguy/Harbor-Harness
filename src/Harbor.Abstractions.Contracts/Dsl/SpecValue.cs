namespace Harbor.Abstractions.Dsl;

/// <summary>One argument of a <see cref="SpecOp"/>, already classified by the parser.</summary>
public readonly struct SpecValue
{
    /// <summary>Creates an empty value.</summary>
    public SpecValue()
    {
        Kind = default;
        Text = string.Empty;
    }

    /// <summary>Creates a classified literal.</summary>
    /// <param name="kind">Literal form the parser read.</param>
    /// <param name="text">Canonical text of the literal, without surrounding quotes.</param>
    public SpecValue(SpecValueKind kind, string text)
    {
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>Literal form the parser read.</summary>
    public SpecValueKind Kind { get; }

    /// <summary>Canonical text of the literal, without surrounding quotes.</summary>
    public string Text { get; }
}
