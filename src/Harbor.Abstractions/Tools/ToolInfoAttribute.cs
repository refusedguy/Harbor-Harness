namespace Harbor.Abstractions.Tools;

/// <summary>
///     Declarative ITool glue for the SG3 source generator (issue 1244).
///     A partial tool class carrying this attribute gets its scalar members
///     (<c>Name</c>, <c>DisplayName</c>, <c>Glyph</c>, <c>Description</c>,
///     <c>ExecutionMode</c>, <c>PromptSnippet</c>, <c>PromptGuidelines</c>)
///     emitted by <c>Harbor.CodeGen.ToolGlueGenerator</c> instead of
///     hand-written. <c>SafetyProfile</c>, <c>ParameterSchema</c>,
///     <c>ValidateArguments</c> and <c>ExecuteAsync</c> stay hand-written:
///     schema-from-attribute is follow-up work, not this spike.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ToolInfoAttribute : Attribute
{
    /// <summary>
    ///     Declares the tool's scalar glue.
    /// </summary>
    /// <param name="name">Stable lowercase tool name (e.g. "bash").</param>
    /// <param name="displayName">Human-readable name shown in /tools.</param>
    /// <param name="description">One-line model-facing description.</param>
    public ToolInfoAttribute(string name, string displayName, string description)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
    }

    /// <summary>Stable lowercase tool name (e.g. "bash").</summary>
    public string Name { get; }

    /// <summary>Human-readable name shown in /tools.</summary>
    public string DisplayName { get; }

    /// <summary>One-line model-facing description.</summary>
    public string Description { get; }

    /// <summary>Glyph beside the tool name. Defaults to ToolGlyphs.Default.</summary>
    public string Glyph { get; set; } = ToolGlyphs.Default;

    /// <summary>Parallel vs sequential execution. Defaults to Parallel.</summary>
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Parallel;

    /// <summary>One-line snippet for the system prompt's tool list.</summary>
    public string? PromptSnippet { get; set; }

    /// <summary>Longer-form guidelines injected under the tool's entry.</summary>
    public string[] PromptGuidelines { get; set; } = [];
}
