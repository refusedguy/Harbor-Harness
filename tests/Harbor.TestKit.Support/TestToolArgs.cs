// TestToolArgs.cs — the tool-argument payloads the test tree serializes.
//
// WHY NAMED RECORDS AND NOT ANONYMOUS TYPES
// -----------------------------------------
// A source-generated context registers TYPES BY NAME: `[JsonSerializable]`
// takes a `Type`, and an anonymous type has no name to give it. The runtime
// cannot be asked for one either — the source generator's "unspeakable type"
// support walks up to the nearest ANCESTOR it knows, and an anonymous type's
// only ancestor is `object`, so its members would be silently dropped.
//
// So the payloads the tests used to build inline
// (`new { path = ..., content = ... }`) became named records. The wire shape
// is pinned member by member with `[JsonPropertyName]`, in the order the
// anonymous type declared them, so the bytes the tools receive are the bytes
// they received before.
//
// The names are the TOOL's argument names, not the tests': `path`, `content`,
// `oldString`, `newString`, `expectedVersion`, `command`, `pattern`, `limit`,
// `patch`, `input` are what `ReadTool`, `WriteTool`, `EditTool`, `PatchTool`,
// `BashTool` and the grep tool declare in their parameter schemas.

using System.Text.Json.Serialization;

namespace Harbor.TestKit;

/// <summary>     The <c>path</c> argument, alone. </summary>
public sealed record ToolPathArgs(
    [property: JsonPropertyName("path")] string Path);

/// <summary>     The <c>path</c> + <c>content</c> arguments. </summary>
public sealed record ToolPathContentArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

/// <summary>     The <c>path</c> + <c>oldString</c> + <c>newString</c> arguments. </summary>
public sealed record ToolPathEditArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldString")] string OldString,
    [property: JsonPropertyName("newString")] string NewString);

/// <summary>     The <c>path</c> + <c>content</c> + <c>expectedVersion</c> arguments. </summary>
public sealed record ToolPathContentVersionArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("expectedVersion")] string ExpectedVersion);

/// <summary>     The <c>path</c> + <c>oldString</c> + <c>newString</c> + <c>expectedVersion</c> arguments. </summary>
public sealed record ToolPathEditVersionArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldString")] string OldString,
    [property: JsonPropertyName("newString")] string NewString,
    [property: JsonPropertyName("expectedVersion")] string ExpectedVersion);

/// <summary>     The <c>path</c> + <c>limit</c> arguments. </summary>
public sealed record ToolPathLimitArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>     The <c>path</c> + <c>patch</c> arguments. </summary>
public sealed record ToolPathPatchArgs(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("patch")] string Patch);

/// <summary>     The <c>command</c> argument. </summary>
public sealed record ToolCommandArgs(
    [property: JsonPropertyName("command")] string Command);

/// <summary>     The <c>pattern</c> + <c>path</c> arguments. </summary>
public sealed record ToolPatternPathArgs(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("path")] string Path);

/// <summary>     The <c>input</c> argument. </summary>
public sealed record ToolInputArgs(
    [property: JsonPropertyName("input")] string Input);
