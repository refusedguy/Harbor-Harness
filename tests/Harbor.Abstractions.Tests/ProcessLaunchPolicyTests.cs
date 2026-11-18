using System.Diagnostics;
using Harbor.Abstractions.Extensions;
using Harbor.TestKit;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #2595: the one owner of the child-process environment allowlist. These
///     pins make sure the clear-and-copy mechanism still produces the exact
///     key list the old per-site copies produced (sorted, deduplicated,
///     parent-present keys only) and that the "inherit all" declaration names
///     every key in the parent while it is held.
/// </summary>
[NotInParallel("process-env")]
public class ProcessLaunchPolicyTests
{
    private const string CanaryKey = "HARBOR_PROCESS_LAUNCH_POLICY_CANARY";

    [Test]
    public async Task ApplyEnvironment_CopiesOnlyDeclaredParentKeysSorted()
    {
        using var canary = new EnvScope(CanaryKey);
        canary.Set(CanaryKey, "planted-secret");

        var psi = new ProcessStartInfo("unused");
        psi.Environment["HARBOR_UNDECLARED_CANARY"] = "must-not-reach-child";

        IReadOnlyList<string> keys = ProcessLaunchPolicy.ApplyEnvironment(
            psi, ["PATH", CanaryKey, CanaryKey, " ", "HARBOR_ABSENT_CANARY"]);

        await Assert.That(keys.Contains("PATH")).IsTrue();
        await Assert.That(keys.Contains(CanaryKey)).IsTrue();
        await Assert.That(keys.Contains("HARBOR_ABSENT_CANARY")).IsFalse();
        await Assert.That(keys.Count).IsEqualTo(2);
        await Assert.That(keys[0]).IsEqualTo(CanaryKey);
        await Assert.That(keys[1]).IsEqualTo("PATH");
        await Assert.That(psi.Environment[CanaryKey]).IsEqualTo("planted-secret");
        await Assert.That(psi.Environment.ContainsKey("HARBOR_UNDECLARED_CANARY")).IsFalse();
    }

    [Test]
    public async Task EffectiveEnvironmentKeys_MatchesAppliedKeysWithoutAProcess()
    {
        using var canary = new EnvScope(CanaryKey);
        canary.Set(CanaryKey, "value");
        string[] allowed = ["PATH", CanaryKey, "HARBOR_ABSENT_CANARY"];

        var psi = new ProcessStartInfo("unused");
        IReadOnlyList<string> applied = ProcessLaunchPolicy.ApplyEnvironment(psi, allowed);
        IReadOnlyList<string> effective = ProcessLaunchPolicy.EffectiveEnvironmentKeys(allowed);

        await Assert.That(effective.SequenceEqual(applied)).IsTrue();
    }

    [Test]
    public async Task InheritedEnvironmentKeys_NamesEveryParentKey()
    {
        using var canary = new EnvScope(CanaryKey);
        canary.Set(CanaryKey, "value");

        IReadOnlyList<string> keys = ProcessLaunchPolicy.InheritedEnvironmentKeys();

        await Assert.That(keys.Contains(CanaryKey)).IsTrue();
        await Assert.That(keys.Contains("PATH")).IsTrue();
    }

    [Test]
    public async Task ApplyEnvironment_EmptyAllowlist_ClearsTheWholeBlock()
    {
        var psi = new ProcessStartInfo("unused");
        psi.Environment["PATH"] = "kept-only-if-declared";

        IReadOnlyList<string> keys = ProcessLaunchPolicy.ApplyEnvironment(psi, []);

        await Assert.That(keys.Count).IsEqualTo(0);
        await Assert.That(psi.Environment.Count).IsEqualTo(0);
    }
}
