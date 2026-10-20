namespace Harbor.TestKit.Tests;

/// <summary>
///     The scope helpers own OS-global state — a directory on disk and a
///     process-wide environment — so their disposal semantics are a
///     correctness matter, not a detail. These tests pin the three
///     properties that matter: the resource exists while the scope is alive,
///     it is gone after the scope is disposed, and it is gone EVEN WHEN the
///     body threw.
/// </summary>
public class TempDirScopeTests
{
    [Test]
    public async Task Creates_The_Directory_And_Owns_It_Until_Dispose()
    {
        string path;
        using (var scope = new TempDirScope("testkit"))
        {
            path = scope.Path;
            await Assert.That(Directory.Exists(path)).IsTrue()
                .Because("the scope's whole contract is that the directory is there when the test asks for it");
        }

        await Assert.That(Directory.Exists(path)).IsFalse()
            .Because("a temp directory that outlives its scope is a leak, and the leak is what the scope exists to prevent");
    }

    [Test]
    public async Task Gives_Every_Instance_Its_Own_Path()
    {
        using var first = new TempDirScope("testkit");
        using var second = new TempDirScope("testkit");

        await Assert.That(first.Path).IsNotEqualTo(second.Path)
            .Because("two tests running in parallel must not share a directory: one of them would delete the other's files");
    }

    [Test]
    public async Task Deletes_The_Directory_Even_When_The_Body_Throws()
    {
        string path = string.Empty;
        try
        {
            using var scope = new TempDirScope("testkit");
            path = scope.Path;
            throw new InvalidOperationException("the test body failed");
        }
        catch (InvalidOperationException)
        {
            // The failure is the point of the test; the cleanup is the subject.
        }

        await Assert.That(Directory.Exists(path)).IsFalse()
            .Because("the compiler emits the dispose for a `using` even when the body throws — that is the whole reason this is a scope object and not a string plus a finally");
    }

    [Test]
    public async Task Dispose_Is_Idempotent()
    {
        var scope = new TempDirScope("testkit");
        scope.Dispose();

        // The second dispose must not throw: a test that disposes twice (or a
        // using plus an explicit dispose) is a test that should pass.
        scope.Dispose();

        await Assert.That(Directory.Exists(scope.Path)).IsFalse();
    }

    [Test]
    public async Task Combine_Builds_A_Path_Inside_The_Scope()
    {
        using var scope = new TempDirScope("testkit");

        string file = scope.Combine("nested", "file.txt");

        await Assert.That(file.StartsWith(scope.Path, StringComparison.Ordinal)).IsTrue();
        await Assert.That(file.EndsWith("file.txt", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Lives_Under_The_User_Private_Cache_Not_The_Shared_Temp()
    {
        using var scope = new TempDirScope("testkit");

        await Assert.That(scope.Path.StartsWith(TempDirScope.BaseDirectory, StringComparison.Ordinal)).IsTrue()
            .Because("a predictable path under a world-writable directory is a symlink attack surface, and the shared /tmp is exactly that");
    }
}
