// ProjectFileTreeScannerTests.cs — issue #492.
//
// WHAT THESE TESTS ARE FOR
// ------------------------
// Before #492 the scan lived in `MainViewModel` as a private recursive method
// over `System.IO`, so there was nothing to point a fake at: the only way to ask
// "what happens when a directory cannot be read" was to make a real directory
// unreadable, and the only way to ask "what happens when the walk is huge" was to
// have a huge walk. The whole point of the refactor is that the questions are
// now cheap, so the questions are what is asked here.
//
// The fake lister is an IN-MEMORY TREE, not a temp directory. That is the claim
// the issue's "done looks like" makes — unit tests over scan + ignore + icon
// mapping against an in-memory tree — and a test that still needed the disk
// would be evidence that the seam does not exist.
//
// ONE DEVIATION FROM THE PORT CONTRACT, ON PURPOSE
// ------------------------------------------------
// `IDirectoryLister` forbids returning a synchronously completed task, because
// the real implementation must not enumerate on its caller's thread. This fake
// returns one, which keeps the recursion fully deterministic and the assertions
// free of timing. "The port ran somewhere else" is a property of the port, not
// of its caller, and it is covered where the port is tested.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Harbor.App.Avalonia.Services;
using Harbor.Application.Filesystem;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Issue #492 — the desktop file tree is built from the Domain ports, and
///     every decision it used to make privately is observable from here.
/// </summary>
public sealed class ProjectFileTreeScannerTests
{
    private const string Root = "/p";

    // ── the walk goes through the port ────────────────────────────────────

    [Test]
    public async Task Scan_AsksThePortForTheRootAndForEveryDirectoryItKeeps()
    {
        InMemoryDirectoryLister lister = New(
            Dir(Root, "src"),
            File(Root, "README.md"));

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(scan.IsSuccess).IsTrue();
        await Assert.That(lister.Requested).IsEquivalentTo(new[] { Root, Root + "/src" })
            .Because(
                "the walk must be the port's. A scanner that read the disk itself would answer this with "
                + "an empty request list, and the one nested directory it reached is the evidence that it "
                + "really descended rather than trusting a name.");
    }

    [Test]
    public async Task Scan_NamesTheRootAfterTheDirectoryItWasGiven()
    {
        InMemoryDirectoryLister lister = New(Dir(Root, "src"), File(Root, "a.md"));

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(scan.Value.Name).IsEqualTo("p");
        await Assert.That(scan.Value.FullPath).IsEqualTo(Root);
        await Assert.That(scan.Value.IsDirectory).IsTrue();
        await Assert.That(scan.Value.IsExpanded).IsTrue()
            .Because("the root row is the tree's anchor; a collapsed root is an empty sidebar");
    }

    [Test]
    public async Task Scan_ForAnEmptyRoot_UsesTheWorkingDirectoryAndNamesTheRowAfterIt()
    {
        string cwd = Environment.CurrentDirectory;
        InMemoryDirectoryLister lister = New();
        lister.Set(cwd, [File(cwd, "a.md")]);

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(string.Empty);

        await Assert.That(lister.Requested[0]).IsEqualTo(cwd)
            .Because("an empty root is the port's \"means CWD\" marker and has to reach it as one");
        await Assert.That(scan.Value.Name).IsEqualTo(Path.GetFileName(cwd))
            .Because("a root row labelled \"\" is a row the user cannot orient themselves by");
    }

    // ── the tree is built from the listing, in the listing's order ────────

    [Test]
    public async Task Scan_NestsDirectoriesAndFilesUnderTheRightParent()
    {
        InMemoryDirectoryLister lister = New(
            Dir(Root, "src"),
            File(Root, "README.md"),
            Dir(Root, "tests"),
            File(Root + "/src", "Program.cs"),
            File(Root + "/tests", "A.cs"));

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(Names(scan.Value)).IsEqualTo("src, README.md, tests");
        await Assert.That(Names(scan.Value.Children[0])).IsEqualTo("Program.cs");
        await Assert.That(scan.Value.Children[0].IsDirectory).IsTrue();
        await Assert.That(scan.Value.Children[1].IsDirectory).IsFalse()
            .Because("the file/directory distinction is what the expander and the editor both branch on");
        await Assert.That(scan.Value.Children[0].FullPath).IsEqualTo(Root + "/src")
            .Because("FullPath is what the view opens on click — a wrong one opens the wrong file");
    }

    [Test]
    public async Task Scan_ExpandsTheFirstLevelAndCollapsesTheRest()
    {
        InMemoryDirectoryLister lister = New(
            Dir(Root, "src"),
            Dir(Root + "/src", "deep"));

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(scan.Value.Children[0].IsExpanded).IsTrue();
        await Assert.That(scan.Value.Children[0].Children[0].IsExpanded).IsFalse()
            .Because("the shape the sidebar has always drawn; changing it would be a UX change riding "
                   + "along inside a refactor");
    }

    // ── the policy is asked, not assumed ──────────────────────────────────

    [Test]
    public async Task Scan_OmitsADirectoryThePolicyRejects_AndNeverListsIt()
    {
        InMemoryDirectoryLister lister = New(
            Dir(Root, "node_modules"),
            Dir(Root, "src"),
            File(Root + "/node_modules", "index.js"),
            File(Root + "/src", "Program.cs"));

        Result<FileTreeNode> scan = await Scanner(
            lister,
            new StubPolicy { Ignored = { "node_modules" }, Icon = "policy-icon" }).ScanAsync(Root);

        await Assert.That(Names(scan.Value)).IsEqualTo("src");
        await Assert.That(lister.Requested).IsEquivalentTo(new[] { Root, Root + "/src" })
            .Because(
                "the policy gates the WALK, not the rendering. A scanner that listed first and filtered "
                + "afterwards would spend the syscall — and the port's 5s timeout — on exactly the "
                + "directories the policy exists to skip, which is the cost the old view-model paid too.");
    }

    [Test]
    public async Task Scan_AsksThePolicyForEveryFilesIcon()
    {
        InMemoryDirectoryLister lister = New(
            File(Root, "a.cs"),
            File(Root, "b.png"),
            Dir(Root, "src"));

        var policy = new StubPolicy { Icon = "asked-the-policy" };
        Result<FileTreeNode> scan = await Scanner(lister, policy).ScanAsync(Root);

        await Assert.That(scan.Value.Children[0].IconPath).IsEqualTo("asked-the-policy");
        await Assert.That(scan.Value.Children[1].IconPath).IsEqualTo("asked-the-policy");
        await Assert.That(policy.IconRequests).IsEquivalentTo(new[] { "a.cs", "b.png" })
            .Because("the extension→icon decision is policy, and the audit's point was that it was a "
                   + "private switch nobody could reach or test");
    }

    [Test]
    public async Task Scan_LeavesTheIconOfADirectoryToTheView_NotToThePolicy()
    {
        InMemoryDirectoryLister lister = New(Dir(Root, "src"));

        var policy = new StubPolicy();
        Result<FileTreeNode> scan = await Scanner(lister, policy).ScanAsync(Root);

        await Assert.That(scan.Value.Children[0].IconPath).IsEqualTo("folder");
        await Assert.That(policy.IconRequests).IsEmpty()
            .Because("the policy classifies FILES; a directory that survived it has no classification "
                   + "left to receive, and asking would only make the contract grow a second member");
    }

    // ── budgets ───────────────────────────────────────────────────────────

    [Test]
    public async Task Scan_StopsDescendingAtTheDepthBudget_ButStillShowsTheRow()
    {
        InMemoryDirectoryLister lister = New(
            Dir(Root, "l1"),
            Dir(Root + "/l1", "l2"),
            Dir(Root + "/l2", "l3"),
            File(Root + "/l3", "deep.cs"));

        Result<FileTreeNode> scan = await Scanner(lister, maxDepth: 1).ScanAsync(Root);

        await Assert.That(Names(scan.Value)).IsEqualTo("l1");
        await Assert.That(Names(scan.Value.Children[0])).IsEqualTo("l2")
            .Because("a row past the budget is still SHOWN — the user can see the directory exists even "
                   + "though this scan did not open it. Dropping it silently would make the tree lie "
                   + "about what is in the project.");
        await Assert.That(scan.Value.Children[0].Children[0].Children.Count).IsEqualTo(0);
        await Assert.That(lister.Requested).IsEquivalentTo(new[] { Root, Root + "/l1" })
            .Because(
                "l1 was opened and l2 was not, because l2 sits at the budget's edge. The ROW for l2 is "
                + "still on screen. That is the line the old `if (depth > 3) return;` drew — the depth "
                + "was a cap on the syscall, not on what the tree is allowed to show.");
    }

    [Test]
    public async Task Scan_StopsProducingRowsAtTheNodeBudget()
    {
        // A wide, shallow tree: the shape the old `if (depth > 3)` cap did nothing
        // about, because it bounded DEPTH and not COUNT.
        InMemoryDirectoryLister lister = New(Wide(Root, 20).ToArray());

        Result<FileTreeNode> scan = await Scanner(lister, maxDepth: 3, maxNodes: 5).ScanAsync(Root);

        // One root row plus four children, and the walk stopped there.
        await Assert.That(scan.Value.Children.Count).IsEqualTo(4)            .Because("the port bounds ONE directory at 4096 entries; only a budget on the WALK bounds a "
                   + "project with thousands of directories. Without it, a depth cap is not a limit, it "
                   + "is a suggestion.");
    }

    [Test]
    public async Task Scan_DoesNotChargeTheNodeBudgetForAPolicyRejectedDirectory()
    {
        List<DirectoryEntry> entries = [Dir(Root, "node_modules"), .. Wide(Root, 3)];
        InMemoryDirectoryLister lister = New(entries.ToArray());

        Result<FileTreeNode> scan = await Scanner(lister, maxDepth: 0, maxNodes: 4).ScanAsync(Root);

        await Assert.That(scan.Value.Children.Count).IsEqualTo(3)
            .Because("a budget of four is one root plus three rows, and all three are VISIBLE rows. Had "
                   + "the policy-rejected directory been charged first, the tree would have stopped at "
                   + "two and the user would have lost a directory they can see to one they cannot.");
    }

    // ── failure and cancellation are different things ─────────────────────

    [Test]
    public async Task Scan_ReportsAFailureWhenTheRootItselfCannotBeListed()
    {
        InMemoryDirectoryLister lister = New(); // knows nothing, the root included

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(scan.IsFailure).IsTrue()
            .Because("there is nothing to draw and the caller has to be told why — a successful scan "
                   + "with an empty tree would be a claim that the project is empty");
        await Assert.That(scan.Error).Contains(Root);
    }

    [Test]
    public async Task Scan_DegradesOneUnreadableDirectory_AndKeepsTheRestOfTheTree()
    {
        // `src` is known to the lister but its listing fails — the shape a
        // permission denial or a vanished path produces. `docs` is fine and must
        // still be there afterwards.
        InMemoryDirectoryLister lister = New(
            Dir(Root, "src"),
            Dir(Root, "docs"),
            File(Root + "/src", "Program.cs"),
            File(Root + "/docs", "a.md"));
        lister.Fail(Root + "/src", "permission denied");

        Result<FileTreeNode> scan = await Scanner(lister).ScanAsync(Root);

        await Assert.That(scan.IsSuccess).IsTrue()
            .Because("one unreadable folder does not make the project unreadable");
        await Assert.That(Names(scan.Value)).IsEqualTo("src, docs");
        await Assert.That(scan.Value.Children[0].Children.Count).IsEqualTo(0);
        await Assert.That(Names(scan.Value.Children[1])).IsEqualTo("a.md")
            .Because("this is the specific thing the old code got wrong: it wrapped a whole directory's "
                   + "contents in ONE catch, so a single unreadable file cost that directory every child "
                   + "it had. Per-directory degradation is what makes a partial tree still useful.");
    }

    [Test]
    public async Task Scan_AnAlreadyCancelledScan_Throws_AndAsksForNothing()
    {
        InMemoryDirectoryLister lister = New(File(Root, "a.md"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var scanner = Scanner(lister);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scanner.ScanAsync(Root, cts.Token));

        await Assert.That(lister.Requested).IsEmpty()
            .Because("the token is checked before the first syscall — a cancellation that costs a "
                   + "directory walk to notice is a cancellation that cannot keep up with a user "
                   + "pressing Refresh");
    }

    [Test]
    public async Task TheDefaultBudgetsAreNamed_AndMatchThePortsOwnEntryCap()
    {
        await Assert.That(ProjectFileTreeScanner.DefaultMaxDepth).IsEqualTo(3)
            .Because("three levels is what the sidebar has always drawn; changing it here would be a "
                   + "silent UX change riding along in a refactor");
        await Assert.That(ProjectFileTreeScanner.DefaultMaxNodes)
            .IsEqualTo(SystemDirectoryLister.DefaultMaxEntries)
            .Because("the walk budget and the per-directory budget are the same number on purpose: the "
                   + "port caps one directory, a walk of N directories at that cap is N times it, and "
                   + "two constants for one idea drift apart silently");
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    private static ProjectFileTreeScanner Scanner(
        IDirectoryLister lister,
        IFileTreePolicy? policy = null,
        int? maxDepth = null,
        int? maxNodes = null)
    {
        if (maxDepth is null && maxNodes is null)
        {
            return new ProjectFileTreeScanner(
                lister,
                policy ?? new DefaultFileTreePolicy(),
                NullLogger<ProjectFileTreeScanner>.Instance);
        }

        return new ProjectFileTreeScanner(
            lister,
            policy ?? new DefaultFileTreePolicy(),
            NullLogger<ProjectFileTreeScanner>.Instance,
            maxDepth ?? ProjectFileTreeScanner.DefaultMaxDepth,
            maxNodes ?? ProjectFileTreeScanner.DefaultMaxNodes);
    }

    private static string Names(FileTreeNode node) =>
        string.Join(", ", node.Children.Select(static child => child.Name));

    /// <summary>
    ///     Build a fake tree out of a flat list of entries. Each entry's own
    ///     <c>FullPath</c> says which directory it belongs to, so a test describes a
    ///     tree by naming its contents and never has to repeat a parent.
    /// </summary>
    private static InMemoryDirectoryLister New(params DirectoryEntry[] entries)
    {
        var lister = new InMemoryDirectoryLister();
        foreach (IGrouping<string, DirectoryEntry> children in entries.GroupBy(ParentOf))
        {
            lister.Set(children.Key, [.. children]);
        }

        return lister;
    }

    /// <summary>
    ///     The directory an entry lives in, with any trailing separator trimmed so
    ///     a tree described against <c>Environment.CurrentDirectory</c> matches
    ///     whatever the scanner passes as the root.
    /// </summary>
    private static string ParentOf(DirectoryEntry entry)
    {
        string? parent = Path.GetDirectoryName(entry.FullPath);
        if (string.IsNullOrEmpty(parent))
        {
            return "/";
        }

        string trimmed = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? parent : trimmed;
    }

    private static DirectoryEntry Dir(string parent, string name) =>
        new(name, parent + "/" + name, true, false);

    private static DirectoryEntry File(string parent, string name) =>
        new(name, parent + "/" + name, false, false);

    /// <summary>Child directory entries named <c>d00</c>…<c>dNN</c> under one parent.</summary>
    private static List<DirectoryEntry> Wide(string parent, int count)
    {
        var entries = new List<DirectoryEntry>(count);
        for (int i = 0; i < count; i++)
        {
            string name = "d" + i.ToString("D2");
            entries.Add(Dir(parent, name));
        }

        return entries;
    }

    /// <summary>
    ///     The in-memory tree the whole point of the seam buys: a directory is a
    ///     key, its children are a value, and nothing here touches a disk.
    /// </summary>
    private sealed class InMemoryDirectoryLister : IDirectoryLister
    {
        private readonly Dictionary<string, IReadOnlyList<DirectoryEntry>> _tree = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

        /// <summary>Every directory the scanner asked for, in the order it asked.</summary>
        public List<string> Requested { get; } = [];

        /// <summary>Register the children of one directory.</summary>
        public void Set(string directory, IReadOnlyList<DirectoryEntry> entries) => _tree[directory] = entries;

        /// <summary>Make one registered directory fail its listing, the way a denial does.</summary>
        public void Fail(string directory, string reason) => _failures[directory] = reason;

        public Task<Result<DirectoryListing>> ListAsync(
            string directory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(directory);

            if (_failures.TryGetValue(directory, out string? reason))
            {
                return Task.FromResult(Result.Failure<DirectoryListing>(reason));
            }

            return Task.FromResult(_tree.TryGetValue(directory, out IReadOnlyList<DirectoryEntry>? entries)
                ? Result.Success(new DirectoryListing(directory, entries))
                : Result.Failure<DirectoryListing>($"Cannot list '{directory}': not in the fake tree"));
        }
    }

    /// <summary>
    ///     A policy that answers from a script, so a test can say "this one is
    ///     ignored" and "every icon comes back with a marker" and then assert the
    ///     scanner actually asked rather than deciding for itself.
    /// </summary>
    private sealed class StubPolicy : IFileTreePolicy
    {
        public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);

        public string Icon { get; init; } = "icon";

        public List<string> IconRequests { get; } = [];

        public bool IsIgnoredDirectory(string name) => Ignored.Contains(name);

        public string IconFor(string fileName)
        {
            IconRequests.Add(fileName);
            return Icon;
        }
    }
}
