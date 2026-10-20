// SharedSourceTreeSnapshotTests.cs — the guard for the shared source snapshot
// (issue #1791).
//
// WHAT THIS FILE GUARDS
// ---------------------
// The shared snapshot is the one place the architecture suite reads the tree
// from. Three things have to hold for it to be a SHARED SNAPSHOT rather than
// a cache that hides changes:
//
//   1. There is ONE of it. A second snapshot means some rule kept a private
//      copy of the tree, which is the defect the snapshot exists to remove.
//   2. It is IMMUTABLE. A rule that could edit the shared view would change
//      every other rule's verdict, and the change would be invisible — the
//      worst possible failure for a gate.
//   3. It still describes the TREE. A snapshot that outlived a change to the
//      tree is a gate measuring a tree that no longer exists.
//
// Each of the three is a CONTROL, and a control that cannot fail is a
// comment. The last test in this file therefore plants a defect and proves
// the checker reports it — so the green in the other three means something.
//
// The assembly-scoped hook at the bottom is the order-independent half of
// control (1): the test above can only see a second snapshot that already
// existed when it ran, and the hook runs after EVERY test in the assembly.

using System.Collections.Immutable;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The guard for the shared source snapshot: one snapshot, immutable, and
///     still describing the tree.
/// </summary>
public sealed class SharedSourceTreeSnapshotTests
{
    /// <summary>
    ///     The directory the guard reads: this project's own sources. It is
    ///     bounded on purpose — the guard is about the MECHANISM, and the
    ///     whole tree is read by the rules that need it, not by the guard.
    /// </summary>
    /// <returns>The directory, once the repository root is known to exist.</returns>
    private static async Task<string> GuardDirectoryAsync()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull().Because(
            "the guard reads the working tree; with no Harbor.slnx above the test bin directory "
            + "the walk finds nothing and every check below would pass for the wrong reason.");

        return Path.Combine(root!, "tests", "Harbor.Architecture.Tests");
    }

    /// <summary>
    ///     There is exactly one snapshot for the whole process.
    /// </summary>
    /// <remarks>
    ///     A rule that builds its own copy of the tree is the defect #1791 is
    ///     about, and it is invisible from the outside: the rule still works,
    ///     the suite is still green, and the tree is still walked twice. The
    ///     counter makes it visible.
    /// </remarks>
    [Test]
    public async Task Exactly_One_Snapshot_Exists_For_The_Whole_Process()
    {
        await Assert.That(SharedSourceTreeSnapshot.InstancesCreated).IsEqualTo(1).Because(
            "the architecture suite reads the source tree through ONE shared snapshot, and a "
            + "second one means a rule kept a private copy of the tree — the exact defect this "
            + "snapshot exists to remove. It would still be green, and the tree would still be "
            + "walked twice.");
    }

    /// <summary>
    ///     Nothing a rule can reach through the snapshot can be written to.
    /// </summary>
    /// <remarks>
    ///     The snapshot is shared, so a rule that could edit it would change
    ///     every other rule's verdict, and nothing would say so. This is the
    ///     control for that: the exposed collections are immutable, and a
    ///     mutation attempt through the read-only view is an EXCEPTION rather
    ///     than a silent edit.
    /// </remarks>
    [Test]
    public async Task The_Snapshot_Exposes_No_Mutable_State()
    {
        SharedSourceTreeSnapshot snapshot = SharedSourceTreeSnapshot.Shared;
        string guardDirectory = await GuardDirectoryAsync();

        ImmutableArray<string> files = snapshot.EnumerateCsFiles(guardDirectory);
        await Assert.That(files.Length).IsGreaterThan(0).Because(
            "the guard reads this project's own sources; an empty list means the walk found "
            + "nothing and the mutation checks below would be checking nothing.");

        var fileList = (IList<string>)files;
        await Assert.That(() => { fileList.Add("tests/Harbor.Architecture.Tests/Intruder.cs"); return true; })
            .Throws<NotSupportedException>()
            .Because(
                "the file list the snapshot hands out is shared by every rule, so writing to it "
                + "would change what the other rules see. A rule that tries has to be told, not "
                + "silently ignored.");

        var capturedList = (IList<SourceTreeEntry>)snapshot.CapturedFiles;
        await Assert.That(() => { capturedList.Clear(); return true; })
            .Throws<NotSupportedException>()
            .Because(
                "the captured manifest is what the guard compares against the tree, so writing to "
                + "it would let a rule hide a change it made.");

        SourceTreeFile? file = snapshot.Get(files[0]);
        await Assert.That(file).IsNotNull().Because(
            "the guard reads a file it has just enumerated; a null here means the snapshot cannot "
            + "hand out what it walked.");

        var lines = (IList<string>)file!.StrippedNewlineLines;
        await Assert.That(() => { lines[0] = "mutated"; return true; })
            .Throws<NotSupportedException>()
            .Because(
                "the line view is shared by every rule that reads the file, so a rule that could "
                + "write to it would silently change another rule's scan — a green gate over a "
                + "different gate.");
    }

    /// <summary>
    ///     The snapshot still describes the tree it was taken from.
    /// </summary>
    /// <remarks>
    ///     This is the control for the failure the snapshot introduces by
    ///     existing: a cache that outlives a change to the tree is a gate
    ///     measuring a tree that is no longer there. The check is a pure
    ///     comparison between what the snapshot captured and what the disk
    ///     says now, so it can be made to fail on purpose — see
    ///     <see cref="The_Invariant_Checker_Reports_A_Planted_Defect" />.
    /// </remarks>
    [Test]
    public async Task The_Snapshot_Still_Describes_The_Tree_On_Disk()
    {
        SharedSourceTreeSnapshot snapshot = SharedSourceTreeSnapshot.Shared;
        string guardDirectory = await GuardDirectoryAsync();

        // Populate the snapshot with a bounded, real slice of the tree, so the
        // comparison below has something to compare.
        foreach (string path in snapshot.EnumerateCsFiles(guardDirectory))
        {
            _ = snapshot.Get(path);
        }

        await Assert.That(snapshot.CapturedFiles.Length).IsGreaterThan(0).Because(
            "the guard reads this project's own sources; with nothing captured there is nothing "
            + "to compare against the tree, and the check below would pass for the wrong reason.");

        ImmutableArray<string> violations = SourceTreeSnapshotInvariants.Verify(
            snapshot.CapturedEnumerations,
            snapshot.CapturedFiles,
            LiveEnumerations(snapshot),
            LiveFiles(snapshot));

        await Assert.That(violations.Length).IsEqualTo(0).Because(
            "the shared snapshot is a CACHE of the source tree, and a cache that no longer "
            + "matches the tree is a gate measuring something that is not there any more: every "
            + "rule reading the snapshot would be judging the old bytes and reporting them as "
            + "current. Drift: " + (violations.Length == 0 ? "(none)" : string.Join(" | ", violations)));
    }

    /// <summary>
    ///     The snapshot hands every rule the BYTES the rule used to read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the parity proof the whole change rests on. A rule that
    ///         used to read a file and comment-strip it itself must now be
    ///         handed the same bytes and the same stripped view — otherwise the
    ///         rule is not the same rule any more, and "the tests are still
    ///         green" says nothing, because a rule that reads nothing finds
    ///         nothing and that is green too.
    ///     </para>
    ///     <para>
    ///         Both of the rules' shapes are checked, because they are not the
    ///         same: the panel/overlay rule splits the stripped text on '\n',
    ///         and the process-environment rule indexes the lines the way
    ///         <c>File.ReadAllLines</c> produces them — one trailing empty
    ///         line apart. A snapshot that gave both rules the same shape
    ///         would shift a line number in one of them.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Snapshot_Hands_Out_The_Bytes_The_Rules_Used_To_Read()
    {
        SharedSourceTreeSnapshot snapshot = SharedSourceTreeSnapshot.Shared;
        string guardDirectory = await GuardDirectoryAsync();
        string[] paths = [.. snapshot.EnumerateCsFiles(guardDirectory)];

        await Assert.That(paths.Length).IsGreaterThan(0).Because(
            "the guard reads this project's own sources; with no file to compare, the check "
            + "below would pass for the wrong reason.");

        // The FILE SET, compared the way the rules used to build it: the same
        // walk, the same filter, sorted.
        string[] legacyWalk =
        [
            .. Directory
                .EnumerateFiles(guardDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(p => !SharedSourceTreeSnapshot.IsBuildOutputPath(p))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];

        await Assert.That(string.Join(
            "\n",
            snapshot.EnumerateCsFiles(guardDirectory).OrderBy(p => p, StringComparer.Ordinal)))
            .IsEqualTo(string.Join("\n", legacyWalk))
            .Because(
                "the snapshot's walk has to return the same file set the rules used to walk "
                + "themselves, or a rule's SCOPE changed — and a rule with a narrower scope "
                + "reports fewer violations without saying so.");

        foreach (string path in paths)
        {
            string raw = File.ReadAllText(path);
            SourceTreeFile? file = snapshot.Get(path);

            await Assert.That(file).IsNotNull().Because(
                $"the snapshot enumerated {path} and then could not hand it over.");

            if (file is null)
            {
                continue;
            }

            // Compared with string.Equals and reported as an INDEX, not as
            // two multi-kilobyte strings handed to an assertion: the
            // comparison is the check, and the message has to name the line
            // that differs, not reprint the file.
            if (!string.Equals(file.Text, raw, StringComparison.Ordinal))
            {
                await Assert.That(FirstDifference(file.Text, raw)).IsEqualTo(-1).Because(
                    $"the snapshot's raw view of {path} must be the file's bytes, unchanged: a rule "
                    + "that used to read this file must still see the same text.");
            }

            // The panel/overlay rule splits the comment-stripped text on '\n'.
            string[] expectedNewline = SourceScan.StripComments(raw).Split('\n');
            await Assert.That(FirstDifference(expectedNewline, file.StrippedNewlineLines))
                .IsEqualTo(-1)
                .Because(
                    $"the panel/overlay rule splits the comment-stripped text on '\\n', so the "
                    + $"snapshot's line view of {path} must be that text split the same way. A "
                    + "snapshot that 'tidied' the split would move every line number the rule "
                    + "reports.");

            // What the process-environment rule computes today, computed the
            // old way, and compared against what the snapshot hands it.
            string[] legacyLines = File.ReadAllLines(path);
            string[] legacyCode = SourceScan.StripComments(string.Join('\n', legacyLines)).Split('\n');

            await Assert.That(FirstDifference(legacyCode, file.StrippedReadLineLines))
                .IsEqualTo(-1)
                .Because(
                    $"the process-environment rule indexes the lines as File.ReadAllLines "
                    + $"produced them, so the snapshot's ReadLine-shaped view of {path} must be "
                    + "the same array — including the absence of a trailing empty line.");
        }
    }

    /// <summary>
    ///     The index of the first line that differs, or <c>-1</c> when the two
    ///     line arrays are the same.
    /// </summary>
    /// <param name="expected">The lines the rule used to compute.</param>
    /// <param name="actual">The lines the snapshot hands it.</param>
    private static int FirstDifference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        int shared = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return expected.Count == actual.Count ? -1 : shared;
    }

    /// <summary>
    ///     The index of the first character that differs, or <c>-1</c> when the
    ///     two texts are the same.
    /// </summary>
    /// <param name="expected">The text the rule used to read.</param>
    /// <param name="actual">The text the snapshot hands it.</param>
    private static int FirstDifference(string expected, string actual)
    {
        int shared = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < shared; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return expected.Length == actual.Length ? -1 : shared;
    }

    /// <summary>
    ///     The snapshot reads each file ONCE, and derives each line view ONCE.
    /// </summary>
    /// <remarks>
    ///     This is the constant budget the change is held to, and it is a COUNT
    ///     rather than a duration on purpose: a duration measures the runner, a
    ///     count measures the code. "One read per file" and "at most two line
    ///     views per file" are the two properties that make the shared snapshot
    ///     cheaper than the per-rule copies it replaces — the panel/overlay
    ///     rule alone used to re-split every file of the product tree once per
    ///     hand-off it looked up.
    /// </remarks>
    [Test]
    public async Task The_Snapshot_Reads_Each_File_Once_And_Derives_Each_Line_View_Once()
    {
        SharedSourceTreeSnapshot snapshot = SharedSourceTreeSnapshot.Shared;
        string guardDirectory = await GuardDirectoryAsync();
        string[] paths = [.. snapshot.EnumerateCsFiles(guardDirectory)];

        await Assert.That(paths.Length).IsGreaterThan(0).Because(
            "the guard reads this project's own sources; with no file to ask for twice, the check "
            + "below would pass for the wrong reason.");

        _ = snapshot.Get(paths[0]);
        _ = snapshot.Get(paths[0]);
        _ = snapshot.Get(paths[0]);

        await Assert.That(snapshot.ReadsOf(paths[0])).IsEqualTo(1).Because(
            "a file the snapshot already holds must not be read from disk again — three asks for "
            + "one file have to cost one read, or the snapshot is a cache with no cache in it.");

        (int reads, int files) = snapshot.ReadBudget;
        await Assert.That(reads).IsEqualTo(files).Because(
            "the budget is ONE read per captured file for the whole assembly, however many rules "
            + "ask for it. A larger number means a rule re-read the tree behind the snapshot's "
            + "back, which is the defect #1791 is about.");

        await Assert.That(SharedSourceTreeSnapshot.LineViewComputations)
            .IsLessThanOrEqualTo(2 * files)
            .Because(
                "each file has at most two line views — the '\\n' split and the ReadAllLines "
                + "split — so the whole assembly's line views are bounded by twice the file "
                + "count. A larger number means a rule re-derived a view the snapshot already had.");
    }

    /// <summary>
    ///     The invariant checker reports a defect that is planted on purpose.
    /// </summary>
    /// <remarks>
    ///     Without this, the three controls above are indistinguishable from
    ///     three assertions that cannot fail — which is the #591 shape the
    ///     repo already knows about. A checker that is fed a changed file, a
    ///     deleted file and a grown directory has to say so, and this test says
    ///     it does.
    /// </remarks>
    [Test]
    public async Task The_Invariant_Checker_Reports_A_Planted_Defect()
    {
        var capturedFile = new SourceTreeEntry("/tree/Changed.cs", 100, DateTime.UnixEpoch);

        // 1. A file whose size changed under the snapshot.
        ImmutableArray<string> resized = SourceTreeSnapshotInvariants.Verify(
            [],
            [capturedFile],
            [],
            [new SourceTreeEntry("/tree/Changed.cs", 101, DateTime.UnixEpoch)]);

        await Assert.That(resized.Length).IsGreaterThan(0).Because(
            "a file that changed size under the snapshot is exactly the drift the guard exists "
            + "to catch, and a checker that stayed quiet about it would make the green in "
            + "The_Snapshot_Still_Describes_The_Tree_On_Disk meaningless.");

        await Assert.That(string.Join(" | ", resized)).Contains("Changed.cs").Because(
            "the violation has to name the file it is about; a checker that reported nothing "
            + "identifiable would be a checker nobody could act on.");

        // 2. A file that is gone.
        ImmutableArray<string> deleted = SourceTreeSnapshotInvariants.Verify(
            [],
            [capturedFile],
            [],
            []);

        await Assert.That(deleted.Length).IsGreaterThan(0).Because(
            "a file the snapshot read and the tree no longer has is drift too, and it is the one "
            + "that changes a rule's SCOPE rather than its content.");

        // 3. A directory that grew a file.
        ImmutableArray<string> grown = SourceTreeSnapshotInvariants.Verify(
            [new SourceTreeEnumeration("/tree", ["a.cs"])],
            [],
            [new SourceTreeEnumeration("/tree", ["a.cs", "b.cs"])],
            []);

        await Assert.That(grown.Length).IsGreaterThan(0).Because(
            "a file that appeared in a directory the snapshot walked is the drift that would "
            + "silently narrow a rule's scope: the rule would keep judging the tree it saw and "
            + "never see the new file.");

        // 4. And the honest answer: an unchanged tree produces no violations.
        ImmutableArray<string> clean = SourceTreeSnapshotInvariants.Verify(
            [new SourceTreeEnumeration("/tree", ["a.cs"])],
            [capturedFile],
            [new SourceTreeEnumeration("/tree", ["a.cs"])],
            [capturedFile]);

        await Assert.That(clean.Length).IsEqualTo(0).Because(
            "the checker must not report drift where there is none, or the guard above would be "
            + "red on a tree that never changed — and a guard that is always red is a guard that "
            + "is always ignored.");
    }

    /// <summary>
    ///     The order-independent half of the "one snapshot" control: after every
    ///     test in this assembly has run, there is still exactly one.
    /// </summary>
    /// <remarks>
    ///     The test above can only see a second snapshot that already existed
    ///     when it ran. This hook runs after ALL of them, so a rule that built
    ///     a private snapshot late is still caught.
    /// </remarks>
    [After(HookType.Assembly)]
    public static void The_Whole_Run_Left_Exactly_One_Snapshot_Behind()
    {
        int created = SharedSourceTreeSnapshot.InstancesCreated;
        if (created != 1)
        {
            throw new InvalidOperationException(
                "The architecture suite must read the source tree through ONE shared snapshot, "
                + $"but {created} were created during this run. A rule that builds its own copy "
                + "of the tree is the defect #1791 is about: it is still green, and the tree is "
                + "still walked twice.");
        }
    }

    /// <summary>The directories the snapshot walked, walked again now.</summary>
    /// <param name="snapshot">The snapshot whose directories to re-walk.</param>
    private static ImmutableArray<SourceTreeEnumeration> LiveEnumerations(
        SharedSourceTreeSnapshot snapshot)
    {
        var live = ImmutableArray.CreateBuilder<SourceTreeEnumeration>();

        foreach (SourceTreeEnumeration captured in snapshot.CapturedEnumerations)
        {
            ImmutableArray<string> files = [];
            if (Directory.Exists(captured.Directory))
            {
                files =
                [
                    .. Directory
                        .EnumerateFiles(captured.Directory, "*.cs", SearchOption.AllDirectories)
                        .Where(p => !SharedSourceTreeSnapshot.IsBuildOutputPath(p))
                ];
            }

            live.Add(new SourceTreeEnumeration(captured.Directory, files));
        }

        return live.ToImmutable();
    }

    /// <summary>The files the snapshot read, stat'ed again now.</summary>
    /// <param name="snapshot">The snapshot whose files to re-stat.</param>
    private static ImmutableArray<SourceTreeEntry> LiveFiles(SharedSourceTreeSnapshot snapshot)
    {
        var live = ImmutableArray.CreateBuilder<SourceTreeEntry>();

        foreach (SourceTreeEntry captured in snapshot.CapturedFiles)
        {
            var info = new FileInfo(captured.Path);
            live.Add(info.Exists
                ? new SourceTreeEntry(captured.Path, info.Length, info.LastWriteTimeUtc)
                : new SourceTreeEntry(captured.Path, -1, DateTime.MinValue));
        }

        return live.ToImmutable();
    }
}
