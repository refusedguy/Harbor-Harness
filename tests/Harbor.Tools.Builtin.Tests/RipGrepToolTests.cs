using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="RipGrepTool" />. The tool wraps the external <c>rg</c> binary;
///     when <c>rg</c> is not installed (CI sandbox without ripgrep) the "_WhenRgInstalled"
///     tests are skipped via <see cref="SkipWhenRgMissingAttribute" />; when <c>rg</c> IS
///     installed the "MissingRg" test is skipped via <see cref="SkipWhenRgPresentAttribute" />.
/// </summary>
/// <remarks>
///     <para>
///         §ARCH-007: TUnit 0.50.0's in-test <c>Skip.Test(string)</c> throws
///         <c>SkipTestException</c> but the engine reports it as a failure rather than a
///         skip in this environment (verified via a standalone probe). The robust path is
///         a custom <see cref="SkipAttribute" /> subclass overriding
///         <c>ShouldSkip(TestRegisteredContext)</c> — the engine honours that at discovery
///         time and reports the test as "skipped" with the attribute's reason.
///     </para>
/// </remarks>
public class RipGrepToolTests
{
    internal const string RgMissingReason = "ripgrep (rg) is not on PATH — skipping integration test.";
    internal const string RgPresentReason = "ripgrep (rg) is installed — skipping the 'rg missing' path.";

    internal static bool IsRgAvailable()
    {
        string? overridePath = Environment.GetEnvironmentVariable("RG_PATH");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return true;

        string name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), name))) return true;
            }
            catch
            { /* skip malformed entries */
            }
        }
        return false;
    }

    [Test]
    public async Task Name_IsRipGrep()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("ripgrep");
    }

    [Test]
    public async Task ExecutionMode_IsParallel()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
    }

    [Test]
    public async Task ValidateArguments_MissingPattern_ReturnsFailure()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    [SkipWhenRgMissing]
    public async Task ExecuteAsync_FindsMatches_WhenRgInstalled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-rg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "foo\nbar\nbaz");
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "another foo here");

        try
        {
            var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"pattern":"foo","path":"{{root.Replace("\\", "\\\\")}}"}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("foo");
            await Assert.That(result.Output).Contains("a.txt");
            await Assert.That(result.Output).Contains("b.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    [SkipWhenRgMissing]
    public async Task ExecuteAsync_NoMatches_ReturnsEmptyResult_WhenRgInstalled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-rg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "foo");

        try
        {
            var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"pattern":"zzz-not-present","path":"{{root.Replace("\\", "\\\\")}}"}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("No matches");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    [SkipWhenRgMissing]
    public async Task ExecuteAsync_PathNotFound_ReturnsError()
    {
        // The tool checks rg availability BEFORE the path, so without rg the
        // call returns the "not installed" hint (covered by
        // ExecuteAsync_MissingRg_ReturnsHelpfulError) and this test would
        // assert against the wrong error. Gate on rg so the path branch is
        // deterministic on runners without ripgrep.
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        var args = JsonDocument.Parse(
            $$"""{"pattern":"foo","path":"/tmp/harbor-rg-missing-{{Guid.NewGuid():N}}"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    [SkipWhenRgMissing]
    public async Task ExecuteAsync_GlobFilterLimitsFileTypes_WhenRgInstalled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-rg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "foo");
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "foo");

        try
        {
            var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"pattern":"foo","path":"{{root.Replace("\\", "\\\\")}}","glob":"*.cs"}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Output).Contains("a.cs");
            await Assert.That(result.Output.Contains("b.txt")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    [SkipWhenRgPresent]
    public async Task ExecuteAsync_MissingRg_ReturnsHelpfulError()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        string root = Path.Combine(Path.GetTempPath(), $"harbor-rg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var args = JsonDocument.Parse(
                $$"""{"pattern":"foo","path":"{{root.Replace("\\", "\\\\")}}"}""").RootElement;
            var result = await tool.ExecuteAsync(args, CreateContext());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("rg");
            await Assert.That(result.Output).Contains("grep");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}

/// <summary>
///     Skip the test when <c>rg</c> is NOT on PATH. Used by the "WhenRgInstalled" tests.
/// </summary>
internal sealed class SkipWhenRgMissingAttribute : SkipAttribute
{
    public SkipWhenRgMissingAttribute() : base(RipGrepToolTests.RgMissingReason) { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!RipGrepToolTests.IsRgAvailable());
}

/// <summary>
///     Skip the test when <c>rg</c> IS on PATH. Used by the "MissingRg" test.
/// </summary>
internal sealed class SkipWhenRgPresentAttribute : SkipAttribute
{
    public SkipWhenRgPresentAttribute() : base(RipGrepToolTests.RgPresentReason) { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(RipGrepToolTests.IsRgAvailable());
}
