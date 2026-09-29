// CfeControlPositiveControlTests.cs — proves the CFE0001 analyzer is not vacuous.
//
// THE PROBLEM THIS SOLVES
// -----------------------
// A build-time guard that cannot fire is worse than no guard, because it buys
// confidence it has not earned. This repo has already been bitten by exactly
// that shape: NetArchTest's `NotHaveDependencyOn(name)` is SATISFIED by a name
// that matches nothing, so a rule naming a typo'd or deleted assembly passes
// forever — green, and enforcing nothing (see PresentationCapabilityRules.cs,
// "MECHANISM — AND THE TRAP THIS FILE IS BUILT AROUND").
//
// The same failure mode is reachable for an analyzer package in at least four
// ways, none of which a "the build is green" check can see:
//   1. the version pin resolves to a build that contains no rules,
//   2. a NoWarn swallows every diagnostic,
//   3. .editorconfig sets the severity to none,
//   4. the analyzer throws on every input (this repo's actual experience with
//      CSharpFunctionalExtensions.Analyzers 1.4.1 — see docs/ROP-API-INVENTORY.md §5,
//      where AD0001 masked the real counts and made the guard look clean).
//
// CfeValueBaselineTests guards the WIRING. This file guards the BEHAVIOUR, by
// compiling a known-bad snippet and requiring the diagnostic to appear.
//
// HOW IT WORKS
// ------------
// analyzers/CfeControl/ is a throwaway project, deliberately NOT in
// Harbor.slnx, holding two files:
//   KnownBadValueAccess.cs   — an unguarded Result<T>.Value read. MUST report.
//   KnownGoodValueAccess.cs  — a correctly guarded read.        MUST NOT report.
//
// The test shells out to `dotnet build` on it and asserts on the output. Both
// halves matter: reporting the bad file proves sensitivity, staying quiet on
// the good file proves the analyzer is discriminating rather than a noise
// machine — which matters a great deal here, because 21 of the 22 production
// sites this analyzer reported on landing were false positives.
//
// WHY IT IS NOT PART OF THE ROUTINE TEST SHARDS' FAST PATH
// -------------------------------------------------------
// It shells out to the build tool, so it is slow and needs a writable temp dir
// and a resolvable SDK. It self-skips when no `dotnet` is on PATH, so a
// developer machine (or any environment without an SDK) reports "skipped"
// rather than a false red. In CI the SDK is always present, so CI is where this
// actually runs — and CI is the only place the CFE0001 counts were measurable
// in the first place.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Compiles a deliberately non-compliant snippet and requires CFE0001 to
///     fire, and a compliant one and requires it to stay silent.
/// </summary>
public sealed class CfeControlPositiveControlTests
{
    private const string DiagnosticId = "CFE0001";
    private const string ControlProject = "analyzers/CfeControl/Harbor.CfeControl.csproj";
    private const string BadFile = "KnownBadValueAccess.cs";
    private const string GoodFile = "KnownGoodValueAccess.cs";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     What a probe run reports: the combined output plus the build's exit
    ///     code. The exit code is the locale-independent signal — MSBuild's own
    ///     "build succeeded" line is translated, so asserting on that text made
    ///     this test fail on any non-English SDK for a reason that had nothing
    ///     to do with the analyzer.
    /// </summary>
    private sealed record ProbeResult(string Output, int ExitCode);

    /// <summary>
    ///     Runs <c>dotnet build</c> over the control project and returns what it
    ///     reported, or <c>null</c> when no SDK is available (the test then
    ///     self-skips rather than reporting a false failure).
    /// </summary>
    private static async Task<ProbeResult?> BuildControlAsync()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return null;
        }

        string projectPath = Path.Combine(root, ControlProject);
        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException(
                $"[cfe-positive-control] control project not found at '{projectPath}'. Without it "
                + "there is no way to prove the analyzer fires, and every other CFE0001 assertion "
                + "in this suite becomes unfalsifiable.");
        }

        // Build into a scratch directory so the probe never touches obj/ or
        // bin/ of anything the real build owns, and so concurrent runs on the
        // same machine cannot collide.
        string scratch = Path.Combine(Path.GetTempPath(), "harbor-cfe-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("minimal");
        psi.ArgumentList.Add("-p:BaseIntermediateOutputPath=" + Path.Combine(scratch, "obj") + Path.DirectorySeparatorChar);
        psi.ArgumentList.Add("-p:BaseOutputPath=" + Path.Combine(scratch, "bin") + Path.DirectorySeparatorChar);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        // Belt and braces: the exit code is the real signal (asserted below), but
        // MSBuild also prints a localized "build succeeded" line, and pinning the
        // language keeps any operator reading this log from chasing a translation
        // difference that the assertions do not depend on.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(BuildTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A hung probe is a broken probe, not a passing one. Kill it and
            // report the output so far rather than silently skipping.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            return new ProbeResult(
                "TIMEOUT: the CFE0001 control build did not finish within " + BuildTimeout
                + Environment.NewLine + output.ToString(),
                ExitCode: -1);
        }

        return new ProbeResult(output.ToString(), process.ExitCode);
    }

    /// <summary>
    ///     The positive control: a known-bad <c>Result&lt;T&gt;.Value</c> read
    ///     MUST produce CFE0001. If this fails, the analyzer resolved nothing,
    ///     was silenced, or stopped firing, and every "the guard is armed"
    ///     claim elsewhere in this suite is worthless.
    /// </summary>
    [Test]
    public async Task PositiveControl_AnalyzerFiresOnKnownBadValueAccess()
    {
        ProbeResult? probe = await BuildControlAsync();
        if (probe is null)
        {
            // No repository checkout (e.g. a published test host): there is
            // nothing to prove here. Mirrors RepoPaths' documented behaviour.
            return;
        }

        string output = probe.Output;

        await Assert.That(output).Contains(DiagnosticId)
            .Because($"the analyzer must report {DiagnosticId} for the unguarded .Value in {BadFile}. "
                   + "No diagnostic means the guard is not actually loaded — a package that resolves "
                   + "to no rules, a NoWarn that eats everything, or a version whose rules changed. "
                   + "A green build would otherwise be indistinguishable from a working guard.");

        await Assert.That(output).Contains(BadFile)
            .Because($"the {DiagnosticId} diagnostic must be attributed to {BadFile}; a diagnostic "
                   + "pointing somewhere else would mean the control is not exercising the code it "
                   + "claims to");

        // The snippet must COMPILE. If the known-bad file failed to build for an
        // ordinary reason, "no CFE0001" would be trivially true and the control
        // would be asserting nothing — which is the vacuous pass this file
        // exists to rule out. TreatWarningsAsErrors is deliberately OFF in
        // Harbor.CfeControl.csproj so the diagnostic surfaces as a WARNING on a
        // successful build; a failure here means the probe itself is broken.
        //
        // Asserted on the EXIT CODE, not on MSBuild's success line: that line is
        // localized ("Build succeeded" / "Сборка успешно завершена"), so a text
        // match failed this test on a non-English SDK even when the probe was
        // perfectly healthy. Harbor.CfeControl.csproj sets
        // TreatWarningsAsErrors=false precisely so CFE0001 surfaces as a warning
        // and the build still exits 0.
        await Assert.That(probe.ExitCode).IsEqualTo(0)
            .Because("the control snippet must COMPILE, otherwise the absence of "
                   + $"{DiagnosticId} would be an artefact of a broken probe rather than a "
                   + "measurement. A non-zero exit means the probe itself failed to build "
                   + "and every diagnostic it did print proves nothing." + Environment.NewLine
                   + "probe output:" + Environment.NewLine + output);
    }

    /// <summary>
    ///     The negative control: a correctly guarded read must NOT be reported.
    ///     Without this half, a hypothetical analyzer that flags every
    ///     <c>.Value</c> in the repo would pass the positive control while
    ///     being useless — and uselessness is exactly what made 21 of the 22
    ///     production hits on landing false positives.
    /// </summary>
    [Test]
    public async Task NegativeControl_AnalyzerStaysSilentOnGuardedValueAccess()
    {
        ProbeResult? probe = await BuildControlAsync();
        if (probe is null)
        {
            return;
        }

        // A diagnostic naming the good file would mean the guard cannot tell
        // guarded code from unguarded code, i.e. every future hit is noise.
        var offending = probe.Output
            .Split('\n')
            .Where(line => line.Contains(DiagnosticId, StringComparison.Ordinal)
                        && line.Contains(GoodFile, StringComparison.Ordinal))
            .ToList();

        await Assert.That(offending).IsEmpty()
            .Because($"{GoodFile} guards its .Value with an IsFailure early return and must not be "
                   + "reported. If it is, the analyzer cannot distinguish guarded from unguarded "
                   + "code, and the production baseline in CfeValueBaselineTests should be deleted "
                   + "rather than maintained." + Environment.NewLine + string.Join(Environment.NewLine, offending));
    }
}
