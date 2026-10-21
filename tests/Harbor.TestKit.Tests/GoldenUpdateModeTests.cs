namespace Harbor.TestKit.Tests;

/// <summary>
///     The update path — the one flag that turns a compare into a rewrite.
/// </summary>
/// <remarks>
///     <para>
///         NotInParallel: the flag is a process-wide environment variable, and
///         the architecture guard <c>ProcessEnvIsolationRule</c> exists for
///         exactly that. The class also restores the value it READ, which is
///         the other half of that guard's contract.
///     </para>
///     <para>
///         The BARE form of the attribute, not a keyed one: a key excludes
///         only the peers that hold the same key, and the flag this class
///         writes is read by every other golden test in the assembly. Only
///         the bare form keeps the whole assembly off the flag while it is
///         set.
///     </para>
/// </remarks>
[NotInParallel]
public class GoldenUpdateModeTests
{
    /// <summary>The variable the flag is read from — a local name so the write and the restore read the same text.</summary>
    private const string Variable = Golden.UpdateEnvironmentVariable;

    [Test]
    public async Task Update_Mode_Rewrites_The_Baseline()
    {
        string? ambient = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, "1");
            using var scope = new TempDirScope("golden");

            string returned = Golden.VerifyText(scope.Path, "sample", "produced\n");

            await Assert.That(returned).IsEqualTo("produced\n")
                .Because("in update mode the expected text IS the text that was just written — the caller has nothing to compare");
            await Assert.That(File.ReadAllText(Path.Combine(scope.Path, "sample.golden.txt"))).IsEqualTo("produced\n")
                .Because("the whole point of the flag is that the baseline on disk changes");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, ambient);
        }
    }

    [Test]
    public async Task Update_Mode_Is_Off_Without_The_Flag()
    {
        string? ambient = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);

            await Assert.That(Golden.IsUpdateMode).IsFalse()
                .Because(
                    "CI never regenerates a baseline: the flag is the only thing that may rewrite one, and it is off "
                    + "unless somebody asks for it");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, ambient);
        }
    }
}
