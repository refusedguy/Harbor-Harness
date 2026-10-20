// GitProbe.cs — the ONE git-on-PATH probe for the test tree.
//
// WHY IT LIVES HERE
// -----------------
// Two projects shipped the same probe, each a copy of the other under a
// different name: `WorkspaceInspectorTests.GitAvailable` in
// Harbor.Application.Tests and the nested `SkipWhenGitMissing.Probe` in
// Harbor.ChangeTracking.Tests. A probe that starts `git --version` and waits
// for it is not a thing to keep in two places — the copies drift, and the
// drift shows up as "the suite skipped on one runner and not on another".
//
// It lives in the SUPPORT part of the kit because it needs no product
// assembly at all: `System.Diagnostics` and nothing else. A consumer of
// `Harbor.TestKit` reaches it transitively, in the same `Harbor.TestKit`
// namespace, without widening its closure.
//
// WHY IT IS NOT AN ATTRIBUTE
// --------------------------
// TUnit already owns the skip vocabulary: `Skip.Unless(condition, reason)`
// for a runtime decision and `[RunOn]` / `[ExcludeOn]` for an OS gate. A
// hand-written `SkipAttribute` subclass in every project is the shape this
// probe used to have; the probe itself is all that is left of it.

using System.Diagnostics;

namespace Harbor.TestKit;

/// <summary>
///     Answers whether the <c>git</c> executable is on PATH.
/// </summary>
/// <remarks>
///     The probe is one-shot per process: it starts a process and waits for
///     it, and a suite that re-ran it per test would pay that cost once per
///     test rather than once per run.
/// </remarks>
public static class GitProbe
{
    /// <summary>
    ///     The reason to hand to TUnit's <c>Skip.Unless</c> when git is absent.
    /// </summary>
    public const string SkipReason = "git is not available on PATH";

    /// <summary>
    ///     Whether <c>git --version</c> ran successfully.
    /// </summary>
    public static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            return proc is not null && proc.WaitForExit(10_000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            || ex is FileNotFoundException
            || ex is InvalidOperationException)
        {
            return false;
        }
    }
}
