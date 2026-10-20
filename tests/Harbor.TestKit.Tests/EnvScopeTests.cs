namespace Harbor.TestKit.Tests;

/// <summary>
///     The environment scope restores what it read. The asymmetry the
///     architecture guard <c>ProcessEnvIsolationRule</c> exists for is the
///     whole subject here: a scope that restores a value it TYPED instead of
///     the one it READ leaves the process in a state the next test inherits.
/// </summary>
/// <remarks>
///     NotInParallel: every case in this class writes a process-wide
///     environment variable.
/// </remarks>
[NotInParallel("testkit-env")]
public class EnvScopeTests
{
    private const string Variable = "HARBOR_TESTKIT_ENV_PROBE";

    [Test]
    public async Task Restores_The_Value_It_Read()
    {
        Environment.SetEnvironmentVariable(Variable, "before");

        using (var scope = new EnvScope(Variable))
        {
            scope.Set(Variable, "during");
            await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("during");
        }

        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
            .Because("the scope read the value before it wrote it, so the value it puts back is the one that was there — not the one it made up");
    }

    [Test]
    public async Task Restores_A_Variable_That_Did_Not_Exist_By_Removing_It()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        using (var scope = new EnvScope(Variable))
        {
            scope.Set(Variable, "during");
            await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("during");
        }

        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsNull()
            .Because("restoring 'absent' means removing the variable, not setting it to the empty string — an empty string is a value, and a provider would read it as one");
    }

    [Test]
    public async Task Restores_Even_When_The_Body_Throws()
    {
        Environment.SetEnvironmentVariable(Variable, "before");

        try
        {
            using var scope = new EnvScope(Variable);
            scope.Set(Variable, "during");
            throw new InvalidOperationException("the test body failed");
        }
        catch (InvalidOperationException)
        {
            // The failure is the point; the restore is the subject.
        }

        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
            .Because("the compiler emits the dispose for a `using` even when the body throws, and the restore is what makes the NEXT test's environment the one it expects");
    }

    [Test]
    public async Task Restores_A_Variable_It_Was_Not_Told_About()
    {
        Environment.SetEnvironmentVariable(Variable, "before");

        using (var scope = new EnvScope())
        {
            scope.Set(Variable, "during");
        }

        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
            .Because("the scope reads the value at the moment of the write, so a variable it was not told about is still restored — a scope that only restored its ctor list would leak every other write");
    }

    [Test]
    public async Task Reports_The_Original_Value()
    {
        Environment.SetEnvironmentVariable(Variable, "before");

        using var scope = new EnvScope(Variable);

        await Assert.That(scope.OriginalValue(Variable)).IsEqualTo("before");
    }

    [Test]
    public async Task Dispose_Is_Idempotent()
    {
        Environment.SetEnvironmentVariable(Variable, "before");

        var scope = new EnvScope(Variable);
        scope.Set(Variable, "during");
        scope.Dispose();
        scope.Dispose();

        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
            .Because("a second dispose must not restore the ORIGINAL over a value a later write put there");
    }
}
