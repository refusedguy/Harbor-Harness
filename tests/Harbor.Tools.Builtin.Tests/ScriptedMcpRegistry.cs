using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using System.Text.Json;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>Scriptable <see cref="IMcpRegistry" />: InvokeAsync answers from a handler, recording the last call.</summary>
internal sealed class ScriptedMcpRegistry : IMcpRegistry
{
    private readonly Func<string, string, JsonElement, Result<string>> _invoke;

    public ScriptedMcpRegistry(Func<string, string, JsonElement, Result<string>> invoke) => _invoke = invoke;

    public static ScriptedMcpRegistry Succeed(string payload) =>
        new((_, _, _) => Result.Success(payload));

    public static ScriptedMcpRegistry Fail(string error) =>
        new((_, _, _) => Result.Failure<string>(error));

    public string? LastServer { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastArgsJson { get; private set; }

    public Result Register(string name, string stdioCommand) => Result.Success();
    public Result Unregister(string name) => Result.Success();
    public IReadOnlyList<string> GetServerNames() => Array.Empty<string>();
    public IReadOnlyList<McpServerInstructions> GetInstructions() => Array.Empty<McpServerInstructions>();

    public Task<Result<string>> InvokeAsync(string server, string method, JsonElement args, CancellationToken cancellationToken = default)
    {
        LastServer = server;
        LastMethod = method;
        LastArgsJson = args.ValueKind == JsonValueKind.Undefined ? null : args.GetRawText();
        return Task.FromResult(_invoke(server, method, args));
    }
}
