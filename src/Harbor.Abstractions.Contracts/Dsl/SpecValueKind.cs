namespace Harbor.Abstractions.Dsl;

/// <summary>Literal forms an operation argument can take in markup.</summary>
public enum SpecValueKind
{
    /// <summary>A quoted string.</summary>
    String,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>The literal true or false.</summary>
    Boolean,

    /// <summary>A bare name.</summary>
    Identifier
}
