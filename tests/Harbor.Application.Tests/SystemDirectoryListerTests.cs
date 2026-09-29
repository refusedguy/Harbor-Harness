using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Harbor.Application.Filesystem;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #667 — the Infrastructure half of the file-tree seam. The panel used
///     to do this walk itself; these tests pin the three properties the port's
///     contract actually promises, because each of them is a way a directory
///     listing can be worse than useless on a render thread: it can block, it can
///     be unbounded, and it can throw.
/// </summary>
public sealed class SystemDirectoryListerTests
{
    private static SystemDirectoryLister Lister(int maxEntries = SystemDirectoryLister.DefaultMaxEntries) =>
        new(maxEntries, NullLogger<SystemDirectoryLister>.Instance);

    private static string Sandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harbor-667-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task ListAsync_ListsDirectoriesAndFiles()
    {
        string dir = Sandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");

            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            await Assert.That(result.IsSuccess).IsTrue();
            var names = result.Value.Entries.Select(static e => e.Name).ToList();
            await Assert.That(names).Contains("sub");
            await Assert.That(names).Contains("a.txt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_PutsDirectoriesFirst()
    {
        string dir = Sandbox();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "aaa.txt"), "a");
            Directory.CreateDirectory(Path.Combine(dir, "zzz"));

            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            await Assert.That(result.Value.Entries[0].IsDirectory).IsTrue()
                .Because("the view renders directories first, and the contract says the "
                       + "order is decided here rather than on every frame");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_MarksDirectoriesAndFiles()
    {
        string dir = Sandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");

            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            DirectoryEntry sub = result.Value.Entries.First(static e => e.Name == "sub");
            DirectoryEntry file = result.Value.Entries.First(static e => e.Name == "a.txt");
            await Assert.That(sub.IsDirectory).IsTrue();
            await Assert.That(sub.FullPath).EndsWith("sub");
            await Assert.That(file.IsDirectory).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_EmptyDirectory_SucceedsWithNoEntries()
    {
        string dir = Sandbox();
        try
        {
            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Entries.Count).IsEqualTo(0);
            await Assert.That(result.Value.Truncated).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_MissingDirectory_IsAFailureNotAnException()
    {
        // The panel follows the user around the filesystem; a directory that
        // vanished is an expected outcome, and the only caller has to render
        // something either way.
        string missing = Path.Combine(Path.GetTempPath(), "harbor-667-missing-" + Guid.NewGuid().ToString("N"));

        Result<DirectoryListing> result = await Lister().ListAsync(missing);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(string.IsNullOrWhiteSpace(result.Error)).IsFalse()
            .Because("a failure the panel cannot render is the one thing worse than a throw");
    }

    [Test]
    public async Task ListAsync_TruncatesAtTheCap_AndSaysSo()
    {
        string dir = Sandbox();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, $"f{i:00}.txt"), "x");
            }

            Result<DirectoryListing> result = await Lister(maxEntries: 5).ListAsync(dir);

            await Assert.That(result.Value.Entries.Count).IsEqualTo(5);
            await Assert.That(result.Value.Truncated).IsTrue()
                .Because("a silently shortened tree is indistinguishable from a complete "
                       + "one, and that is a lie the user cannot detect");
            await Assert.That(result.Value.TotalCount).IsEqualTo(-1)
                .Because("a capped walk never finished counting; -1 says so instead of "
                       + "fabricating a total");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_UnderTheCap_ReportsAnExactTotal()
    {
        string dir = Sandbox();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, $"f{i}.txt"), "x");
            }

            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            await Assert.That(result.Value.Truncated).IsFalse();
            await Assert.That(result.Value.TotalCount).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_DoesNotCompleteSynchronously()
    {
        // The contract's reason for being async, asserted as a fact about the
        // returned Task rather than as a comment. A "fast path" that ran the walk
        // inline and returned Task.FromResult would put the stall straight back
        // on the render thread — the exact defect this issue removes — and would
        // pass every other test in this file.
        string dir = Sandbox();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");

            Task<Result<DirectoryListing>> task = Lister().ListAsync(dir);

            await Assert.That(task.IsCompleted).IsFalse()
                .Because("a completed-on-return Task means the walk ran on the caller's "
                       + "thread; the caller is a render frame");

            await task;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_AlreadyCancelled_Throws()
    {
        // Cancellation is allowed to propagate, and it must propagate rather than
        // being folded into a failure the panel would paint as "cannot read".
        string dir = Sandbox();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.That(async () => await Lister().ListAsync(dir, cts.Token))
                .Throws<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ListAsync_MarkADotDirectoryAsHidden()
    {
        string dir = Sandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));

            Result<DirectoryListing> result = await Lister().ListAsync(dir);

            DirectoryEntry git = result.Value.Entries.First(static e => e.Name == ".git");
            await Assert.That(git.IsHidden).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
