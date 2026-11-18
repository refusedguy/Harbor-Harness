namespace Harbor.Abstractions.Dsl;

/// <summary>One name/value pair in a style payload.</summary>
internal readonly record struct MutationStyleProperty(string Name, string Value);
