using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

/// <summary>
///     S7 slice 1 (#385): the reject router and the out-of-reach inventory.
///     The mode flag is required (no default), <c>--all-effects</c> prints
///     the single constant and exits 5 without touching anything, and the
///     frozen-set modes fail closed until their producer slices land. The
///     manifest is planted by hand because S3 (#377) has not landed, and no
///     test here needs git — only files under an isolated
///     <c>HARBOR_HOME</c>.
/// </summary>
[NotInParallel("harbor-home")]
public class RunRejectTests
{
    [Test]
    public async Task Inventory_HasEightEntries_CoveringTheIssueList()
    {
        string[] expected =
        [
            "commits already pushed",
            "PRs or issues created",
            "network calls",
            "external API writes",
            "database migrations",
            "files written outside the repository",
            "~/.harbor session and log records",
            "anything already consumed by the operator's downstream tooling",
        ];

        await Assert.That(RunRejectEffects.OutOfReach.Count).IsEqualTo(expected.Length);
        foreach (string entry in expected)
            await Assert.That(RunRejectEffects.OutOfReach.Contains(entry)).IsTrue();
    }

    [Test]
    public async Task RenderInventory_ContainsEveryEntry_SingleSourceOfTruth()
    {
        string rendered = RunRejectEffects.RenderInventory("r1");

        await Assert.That(rendered.Contains("r1")).IsTrue();
        foreach (string entry in RunRejectEffects.OutOfReach)
            await Assert.That(rendered.Contains(entry)).IsTrue();
        await Assert.That(rendered.Contains("read-only")).IsTrue();
    }

    [Test]
    public async Task Decide_WithoutMode_PrintsThreeModes_Exit2()
    {
        RejectResult result = RunReject.Decide("r1", modeFlag: null);

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.Refused);
        await Assert.That(result.Message.Contains("--patch")).IsTrue();
        await Assert.That(result.Message.Contains("--worktree")).IsTrue();
        await Assert.That(result.Message.Contains("--all-effects")).IsTrue();
    }

    [Test]
    public async Task Decide_UnknownMode_NamesUsage_Exit2()
    {
        RejectResult result = RunReject.Decide("r1", "--delete-everything");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.Refused);
        await Assert.That(result.Message.Contains("Unknown reject mode")).IsTrue();
    }

    [Test]
    public async Task Decide_UnknownRun_AllEffects_Exit2()
    {
        RejectResult result = RunReject.Decide("no-such-run-385", "--all-effects");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.Refused);
    }

    [Test]
    public async Task Decide_AllEffects_KnownRun_PrintsInventory_Exit5()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");

            RejectResult result = RunReject.Decide(runId, "--all-effects");

            await Assert.That(result.ExitCode).IsEqualTo(5);
            await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.InventoryShown);
            await Assert.That(result.Message.Contains(runId)).IsTrue();
            foreach (string entry in RunRejectEffects.OutOfReach)
                await Assert.That(result.Message.Contains(entry)).IsTrue();
        });
    }

    [Test]
    public async Task Decide_AllEffects_Twice_SameOutput_ManifestUnchanged()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");
            string manifestPath = Path.Combine(home, "runs", runId, "manifest.json");
            byte[] before = await File.ReadAllBytesAsync(manifestPath);

            RejectResult first = RunReject.Decide(runId, "--all-effects");
            RejectResult second = RunReject.Decide(runId, "--all-effects");
            byte[] after = await File.ReadAllBytesAsync(manifestPath);

            await Assert.That(first.ExitCode).IsEqualTo(5);
            await Assert.That(second.Message).IsEqualTo(first.Message);
            await Assert.That(after.SequenceEqual(before)).IsTrue();
        });
    }

    [Test]
    public async Task Decide_AllEffects_WithForce_Refused_Exit2()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");

            RejectResult result = RunReject.Decide(runId, "--all-effects", force: true);

            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.Refused);
            await Assert.That(result.Message.Contains("--force")).IsTrue();
        });
    }

    [Test]
    public async Task Decide_Patch_PendingSlice_FailClosed_Exit4()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");

            RejectResult result = RunReject.Decide(runId, "--patch");

            await Assert.That(result.ExitCode).IsEqualTo(4);
            await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.NotImplemented);
            await Assert.That(result.Message.Contains("#377")).IsTrue();
            await Assert.That(result.Message.Contains("Nothing was deleted")).IsTrue();
        });
    }

    [Test]
    public async Task Decide_Worktree_PendingSlice_LeavesCopy_Exit4()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");

            RejectResult result = RunReject.Decide(runId, "--worktree");

            await Assert.That(result.ExitCode).IsEqualTo(4);
            await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.NotImplemented);
            await Assert.That(result.Message.Contains("left untouched")).IsTrue();
        });
    }

    [Test]
    public async Task Decide_UndoApply_PendingSlice_FailClosed_Exit4()
    {
        await WithIsolatedHome(async home =>
        {
            string runId = PlantManifest(home, "Reported");

            RejectResult result = RunReject.Decide(runId, "--undo-apply");

            await Assert.That(result.ExitCode).IsEqualTo(4);
            await Assert.That(result.Outcome).IsEqualTo(RejectOutcome.NotImplemented);
            await Assert.That(result.Message.Contains("#382")).IsTrue();
        });
    }

    [Test]
    public async Task RejectTouchesNothing_DestructiveApisAbsent()
    {
        string path = RejectSourcePath();
        string text = await File.ReadAllTextAsync(path);

        // Non-vacuity: the matcher fires on a planted offender, and the file is real.
        await Assert.That(text.Length > 0).IsTrue();
        await Assert.That(ContainsDestructive("git reset --hard HEAD")).IsTrue();

        string[] offenders =
        [
            "reset --hard",
            "checkout .",
            "git clean",
            "Directory.Delete",
            "ProcessStartInfo",
            "worktree remove",
            "git apply",
        ];
        foreach (string offender in offenders)
            await Assert.That(ContainsDestructive(text, offender)).IsFalse();
    }

    private static bool ContainsDestructive(string text, string? needle = null)
    {
        string[] banned =
        [
            "reset --hard",
            "checkout .",
            "git clean",
            "Directory.Delete",
            "ProcessStartInfo",
            "worktree remove",
            "git apply",
        ];
        if (needle is not null)
            return text.Contains(needle, StringComparison.Ordinal);
        return banned.Any(text.Contains);
    }

    private static string RejectSourcePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "src", "Harbor.Application", "Sessions", "RunReject.cs");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("RunReject.cs not found above the test output directory.");
    }

    /// <summary>
    ///     Hand-planted manifest (S3 has not landed, so no producer exists):
    ///     the exact fields <c>WorkspaceMaterializer</c> writes, with the
    ///     given lifecycle state. No git involved.
    /// </summary>
    private static string PlantManifest(string home, string state)
    {
        string runId = $"r385{Guid.NewGuid():N}"[..12];
        string runDir = Path.Combine(home, "runs", runId);
        Directory.CreateDirectory(runDir);
        string createdAt = DateTimeOffset.UtcNow.ToString("o");
        string json = "{\n" +
            "  \"version\": 1,\n" +
            $"  \"runId\": \"{runId}\",\n" +
            $"  \"repoRoot\": \"{ home.Replace('\\', '/')}/repo\",\n" +
            "  \"baseRevision\": \"0123456789abcdef0123456789abcdef01234567\",\n" +
            "  \"baseBranch\": null,\n" +
            "  \"isolation\": \"Worktree\",\n" +
            "  \"timeoutSeconds\": 60,\n" +
            "  \"maxSteps\": 10,\n" +
            $"  \"worktreePath\": \"{home.Replace('\\', '/')}/runs/{runId}/worktree\",\n" +
            $"  \"state\": \"{state}\",\n" +
            $"  \"createdAtUtc\": \"{createdAt}\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(runDir, "manifest.json"), json);
        return runId;
    }

    private static async Task WithIsolatedHome(Func<string, Task> body)
    {
        string? savedHarborHome = Environment.GetEnvironmentVariable("HARBOR_HOME");
        string home = Path.Combine(Path.GetTempPath(), $"harbor-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HARBOR_HOME", home);
        try
        {
            await body(home).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", savedHarborHome);
            DeleteDir(home);
        }
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            // Test cleanup is best-effort.
            _ = ex;
        }
    }
}
