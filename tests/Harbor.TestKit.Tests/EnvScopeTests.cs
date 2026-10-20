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

    /// <summary>
    ///     Pins the variable to a value for the duration of the body and puts
    ///     back the value it read. The class writes the environment directly
    ///     here — and restores what it READ — because the architecture guard
    ///     requires exactly that shape of every class that writes, and this
    ///     file is one of them.
    /// </summary>
    private static async Task WithAmbientValue(string value, Func<Task> body)
    {
        string? ambient = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, value);
            await body().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, ambient);
        }
    }

    [Test]
    public async Task Restores_The_Value_It_Read()
    {
        await WithAmbientValue("before", async () =>
        {
            using (var scope = new EnvScope(Variable))
            {
                scope.Set(Variable, "during");
                await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("during");
            }

            await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
                .Because("the scope read the value before it wrote it, so the value it puts back is the one that was there — not the one it made up");
        });
    }

    [Test]
    public async Task Restores_A_Variable_That_Was_Absent_By_Removing_It()
    {
        string? ambient = Environment.GetEnvironmentVariable(Variable);
        try
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
        finally
        {
            Environment.SetEnvironmentVariable(Variable, ambient);
        }
    }

    [Test]
    public async Task Restores_Even_When_The_Body_Throws()
    {
        await WithAmbientValue("before", async () =>
        {
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
        });
    }

    [Test]
    public async Task Restores_A_Variable_It_Was_Not_Told_About()
    {
        await WithAmbientValue("before", async () =>
        {
            using (var scope = new EnvScope())
            {
                scope.Set(Variable, "during");
            }

            await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
                .Because("the scope reads the value at the moment of the write, so a variable it was not told about is still restored — a scope that only restored its ctor list would leak every other write");
        });
    }

    [Test]
    public async Task Reports_The_Original_Value()
    {
        await WithAmbientValue("before", async () =>
        {
            using var scope = new EnvScope(Variable);

            await Assert.That(scope.OriginalValue(Variable)).IsEqualTo("before");
        });
    }

    [Test]
    public async Task Dispose_Is_Idempotent()
    {
        await WithAmbientValue("before", async () =>
        {
            var scope = new EnvScope(Variable);
            scope.Set(Variable, "during");
            scope.Dispose();
            scope.Dispose();

            await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo("before")
                .Because("a second dispose must not restore the ORIGINAL over a value a later write put there");
        });
    }
}
