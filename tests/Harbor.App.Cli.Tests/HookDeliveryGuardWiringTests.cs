// HookDeliveryGuardWiringTests.cs — issue #3378.
//
// HookDeliveryGuard used to be a dead type: its only callers were its own unit
// tests. The CLI now wires guard.TryAccept into HookRunner as the advisory
// delivery admission policy. These tests drive the real runner and count the
// hook processes it spawns, so dedupe and the 60/min ceiling are observable
// end to end, not just on the guard in isolation.

using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Application.Hooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

public class HookDeliveryGuardWiringTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hook-guard-tests-" + Guid.NewGuid().ToString("N"));

    private static (HookRunner Runner, string Counter) NewGatedRunner(string dir)
    {
        Directory.CreateDirectory(dir);
        string counter = Path.Combine(dir, "count");
        string configPath = Path.Combine(dir, "hooks.json");
        // Each spawned hook appends one line; the line count is the number of
        // deliveries that reached a hook process (as opposed to being dropped
        // by the guard before GetHooks/RunOneAsync).
        File.WriteAllText(configPath,
            "{\"hooks\":{\"PostToolUse\":[{\"command\":\"echo x >> " + counter + "\"}]}}");
        var guard = new HookDeliveryGuard();
        var runner = new HookRunner(
            configPath,
            NullLogger<HookRunner>.Instance,
            TimeSpan.FromSeconds(10),
            guard.TryAccept);
        return (runner, counter);
    }

    private static int Count(string file) =>
        File.Exists(file) ? File.ReadAllLines(file).Length : 0;

    [Test]
    public async Task AdvisoryHook_RepeatedDeliveryIsSuppressed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (runner, counter) = NewGatedRunner(NewTempDir());
        using var doc = JsonDocument.Parse("""{"n":1}""");

        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");
        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");

        // Same session + same delivery id -> one hook spawn; the retry is
        // dropped before a process is created.
        await Assert.That(Count(counter)).IsEqualTo(1);
    }

    [Test]
    public async Task AdvisoryHook_SixtyFirstDeliveryInAMinuteIsRejected()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var (runner, counter) = NewGatedRunner(NewTempDir());
        using var doc = JsonDocument.Parse("""{"n":1}""");

        for (int i = 0; i < HookDeliveryGuard.MaxPerMinute + 1; i++)
        {
            await runner.RunPostToolUseAsync(
                $"tool-{i}", doc.RootElement, ToolResult.Success("ok"), "s1");
        }

        await Assert.That(Count(counter)).IsEqualTo(HookDeliveryGuard.MaxPerMinute);
    }
}
