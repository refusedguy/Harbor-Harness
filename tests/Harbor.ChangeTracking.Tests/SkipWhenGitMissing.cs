// SkipWhenGitMissing.cs — class-targeted skip attribute for the git-probing
// ChangeTracking suites (applier / freezer / materializer / chain / report).
//
// WHY IT LIVES HERE, AGAIN
// ------------------------
// Every Harbor test project that probes a PATH tool carries its own
// `SkipWhen*Missing` attribute (Harbor.Tools.Builtin.Tests owns
// SkipWhenRgMissing, Harbor.Application.Tests owns this same git variant). It
// is deliberately NOT in Harbor.TestKit: the skip requires a probe process, a
// 10 s cap and a named reason — three details the pipeline suites control for
// themselves, and a shared assembly would grow one more knob nobody agreed
// on. When this file was left behind in Harbor.Application.Tests during the
// #1616 move, the renamed namespace produced CS0246 on five suite files, so
// this mirror restores the convention at the new address.

using System.Diagnostics;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     Skip the decorated test — or, with <c>[SkipWhenGitMissing]</c> on the
///     class, the whole suite — when <c>git</c> is not on PATH. Mirrors the
///     Harbor.Application.Tests variant of the same name.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
internal sealed class SkipWhenGitMissingAttribute : SkipAttribute
{
    public SkipWhenGitMissingAttribute() : base("git is not available on PATH") { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!Probe.Available());

    /// <summary>One-shot PATH probe (mirrors WorkspaceInspectorTests.GitAvailable).</summary>
    internal static class Probe
    {
        internal static bool Available()
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
}
