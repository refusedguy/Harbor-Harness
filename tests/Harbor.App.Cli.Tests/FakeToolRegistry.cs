using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Empty <see cref="IToolRegistry" /> stub for slash-dispatcher tests: the
///     dispatcher resolves the registry eagerly when building its command
///     context, but no /command under test invokes tools.
/// </summary>
internal sealed class FakeToolRegistry : IToolRegistry
{
    public IReadOnlyList<ToolDescriptor> GetAllTools() => Array.Empty<ToolDescriptor>();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) =>
        Array.Empty<ToolDescriptor>();

    public Result<ITool> GetTool(ToolName name) =>
        Result.Failure<ITool>($"Unknown tool '{name.Value}'.");

    public Result Register(ITool tool) => Result.Success();

    public Result Unregister(ToolName name) => Result.Success();
}
