namespace Harbor.Tools.Builtin;

/// <summary>
///     Declares one parameter of a tool's parameter schema (issue #1780).
/// </summary>
/// <remarks>
///     <para>
///         The attributes on a tool class are the schema's only declaration. The generator
///         renders them into the JSON Schema the model sees, in this order per property:
///         <c>type</c>, <c>enum</c>, <c>description</c>, <c>items</c>. The order is the
///         order the hand-written schemas had, because the schema is a contract with the
///         model and the bytes of it are pinned.
///     </para>
///     <para>
///         A parameter of an array's item object declares <see cref="ItemsOf" /> — the name
///         of the array parameter whose items it describes. The item object is an object
///         schema with its own properties and its own required list.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [ToolSchema]
///     [ToolParam("command", "string", Description = "Shell command to execute", Required = true)]
///     [ToolParam("timeout", "integer", Description = "Timeout in seconds (default: 30)")]
///     public sealed partial class BashTool : ITool;
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
internal sealed class ToolParamAttribute : Attribute
{
    /// <summary>Declare one parameter of the tool's schema.</summary>
    /// <param name="name">The property name the model sends.</param>
    /// <param name="type">The JSON type: string, integer, boolean, object or array.</param>
    public ToolParamAttribute(string name, string type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The property name the model sends.</summary>
    public string Name { get; }

    /// <summary>The JSON type: string, integer, boolean, object or array.</summary>
    public string Type { get; }

    /// <summary>The one-line description the model reads.</summary>
    public string? Description { get; set; }

    /// <summary>The allowed values, when the parameter is a closed set.</summary>
    public string[]? Enum { get; set; }

    /// <summary>Whether the model must send this parameter.</summary>
    public bool Required { get; set; }

    /// <summary>
    ///     The name of the array parameter whose item object this parameter belongs to.
    ///     A parameter without it is a property of the tool's own schema.
    /// </summary>
    public string? ItemsOf { get; set; }
}
