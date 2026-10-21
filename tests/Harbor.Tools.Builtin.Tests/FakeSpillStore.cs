using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     An in-memory spill store for the tool tests (#1748).
/// </summary>
/// <remarks>
///     The tool's job is the read and the windowing; where the file lives, which
///     paths it accepts and what a failed write means are the file-backed
///     store's business, and they are covered where that store lives
///     (<c>tests/Harbor.Core.Tests/ToolOutputSpillTests.cs</c>). This double
///     keeps the tool tests free of the filesystem.
/// </remarks>
internal sealed class FakeSpillStore : IToolOutputSpillStore
{
    /// <summary>The spilled files, keyed by the path the model was pointed at.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public string Root => "/spill";

    public Result<ToolOutputSpill> Spill(string sessionId, string toolCallId, string content) =>
        Result.Success(new ToolOutputSpill("/spill/spill.txt", content.Length, content.Length));

    public Task<Result<string>> ReadAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(path, out string? content)
            ? Result.Success(content)
            : Result.Failure<string>($"Spill file not found: {path}"));

    public Result<int> Sweep() => Result.Success(0);
}
