using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     A plugin inventory that records what it was asked to do (#1738).
/// </summary>
/// <remarks>
///     <para>
///         The tool's behaviour is about what it asks BEFORE it changes
///         anything, so the double has to be able to say "nothing was
///         applied" as loudly as "the change was applied".
///     </para>
/// </remarks>
internal sealed class FakePluginInventory : IPluginInventory
{
    /// <summary>What <see cref="List" /> reports.</summary>
    internal List<PluginInventoryEntry> Entries { get; } = [];

    /// <summary>Every change that reached <see cref="ApplyAsync" />, in order.</summary>
    internal List<PluginInventoryChange> Applied { get; } = [];

    /// <summary>Every source that reached <see cref="DescribeSource" />, in order.</summary>
    internal List<string> Described { get; } = [];

    /// <summary>What <see cref="List" /> fails with, when it should fail.</summary>
    internal string? ListError { get; set; }

    /// <summary>What <see cref="ApplyAsync" /> fails with, when it should fail.</summary>
    internal string? ApplyError { get; set; }

    /// <summary>The declared facts <see cref="DescribeSource" /> reports.</summary>
    internal PluginInventoryEntry? DescribedEntry { get; set; }

    public Result<IReadOnlyList<PluginInventoryEntry>> List()
        => ListError is { } error
            ? Result.Failure<IReadOnlyList<PluginInventoryEntry>>(error)
            : Result.Success<IReadOnlyList<PluginInventoryEntry>>(Entries);

    public Result<PluginInventoryEntry> DescribeSource(string sourcePath, PluginScope scope)
    {
        Described.Add(sourcePath);
        return DescribedEntry is { } entry
            ? Result.Success(entry)
            : Result.Success(new PluginInventoryEntry(
                Path.GetFileName(sourcePath),
                scope,
                Path.Combine("/plugins", Path.GetFileName(sourcePath)),
                Enabled: true,
                DeclaredVersion: "1.0.0",
                DeclaredAxes: ["IToolPlugin"],
                RequiredHarborVersion: "0.4.0",
                Compatible: true));
    }

    public Task<Result<PluginInventoryChangeResult>> ApplyAsync(
        PluginInventoryChange change,
        CancellationToken ct = default)
    {
        Applied.Add(change);
        if (ApplyError is { } error)
            return Task.FromResult(Result.Failure<PluginInventoryChangeResult>(error));

        return Task.FromResult(Result.Success(new PluginInventoryChangeResult(
            change.Action,
            change.Name,
            Path.Combine("/plugins", change.Name),
            $"Applied {change.Action} to {change.Name}.")));
    }
}
