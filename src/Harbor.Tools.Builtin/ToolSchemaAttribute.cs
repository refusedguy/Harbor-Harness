namespace Harbor.Tools.Builtin;

/// <summary>
///     Marks a tool whose parameter schema is declared by <see cref="ToolParamAttribute" />
///     attributes and rendered by <c>Harbor.CodeGen.ToolSchemaGenerator</c> (issue #1780).
/// </summary>
/// <remarks>
///     <para>
///         The parameter schema of a tool is the tool's contract with the model: the JSON
///         the model is allowed to send. Before #1780 every builtin tool carried its own
///         hand-written copy of that JSON — 21 files, 281 lines — and the test project
///         carried a second copy of all of them. Two copies of a contract is how a contract
///         drifts, so the declaration now lives once, here, and the generator renders it.
///     </para>
///     <para>
///         The rendered schema is hashed and compared with the pin in
///         <c>src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json</c>. A schema that
///         differs from its pin is a build error: changing a tool's arguments changes what
///         the model may send, and that has to be a deliberate act in the same commit.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
internal sealed class ToolSchemaAttribute : Attribute
{
}
