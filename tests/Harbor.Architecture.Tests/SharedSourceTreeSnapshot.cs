// SharedSourceTreeSnapshot.cs — the ONE snapshot of the repository's source
// tree that the architecture rules read from (issue #1791).
//
// WHY THIS FILE EXISTS
// --------------------
// The architecture suite was measured at 738.3 s of summed test duration
// (953 tests) inside a 1128.5 s whole-suite run, and three rule classes
// accounted for 422 s of it:
//
//   PanelKeyRouteReachabilityRule   200.1 s
//   ProcessEnvIsolationRule         172.7 s
//   ApprovalGateWaiterRaceRules      49.7 s
//
// The cause was never the rules' verdicts. It was the layer UNDER them:
//
//   * every rule kept its OWN `static readonly Lazy<...>` copy of the tree, so
//     the same megabytes were read and comment-stripped once per rule; and
//   * two of the three re-derived the SAME answer once per TEST, so one rule
//     parsed the whole test tree six times over and another twice over.
//
// A shared snapshot fixes the first half, and it is what lets the second half
// be fixed without every rule inventing its own cache: the bytes are read
// once, and a rule that memoises its own verdict over those bytes is
// memoising a pure function of an immutable input.
//
// THE CONTRACT
// ------------
//   1. ONE instance per process. `Shared` is how a rule reaches it, and the
//      constructor COUNTS its instances, so a second one is detected rather
//      than tolerated (see SharedSourceTreeSnapshotTests).
//   2. IMMUTABLE. What the snapshot hands out is an `ImmutableArray<T>` over
//      data it owns, and the file views it hands out are read-only. A caller
//      that tries to mutate either gets an exception, not a corrupted view.
//   3. THE BYTES ARE THE BYTES. What is stored is what `File.ReadAllText`
//      returns, and the comment-stripped view is exactly
//      `SourceScan.StripComments(raw)`. Nothing is normalised, trimmed or
//      re-encoded, because a rule that used to see a byte must still see it:
//      a snapshot that "tidies" the source is a snapshot that can turn a
//      finding into silence.
//   4. THE FILE SET IS THE FILE SET. The snapshot caches the DIRECTORY walk
//      and each rule still applies its OWN path filter on top of it, so a
//      rule's scope is exactly the scope it had before — including the
//      filters that disagree with each other on purpose (see the note on
//      `SourceScan.IsBuildOutput` below).
//
// WHY IT LIVES HERE AND NOT IN Harbor.TestKit
// --------------------------------------------
// The issue suggested Harbor.TestKit, and TestKit is the right home for a
// helper that many test projects share. This is not that: the snapshot is
// about the architecture suite's own I/O layer, it is worth tens of
// megabytes of resident memory, and every project that referenced TestKit
// would carry it for nothing. It also must not reach Harbor.Abstractions —
// the contract gate (`tools/check-abstractions-contract.py`) freezes that
// assembly's dependency set, and a test-only cache has no business in it.
//
// The one thing TestKit could have carried is the measurement, and that is
// what `SourceTreeScanMeasurement.cs` is: the numbers, in the project that
// produces them.
//
// THE `SourceScan.IsBuildOutput` TRAP, AND WHY THE FILTER IS NOT SHARED
// ---------------------------------------------------------------------
// `SourceScan.IsBuildOutput` returns true for any path containing "/tests/",
// which is correct for a gate judging product source and fatal for a gate
// judging the test tree. Two rules in this project hit that trap and worked
// around it with their own walkers (see `PanelKeyRouteProbe.ReadTestTrees`).
// The shared snapshot therefore caches the walk and the filter that EVERY
// rule agrees on — build output and sibling worktrees — and leaves the
// rest of each rule's filter where it is. A rule that wants a different
// scope still gets a different scope; it just no longer re-walks the disk to
// find it.

using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Line splitting with <c>File.ReadAllLines</c> semantics, in one place,
///     because the two shapes are not interchangeable and getting them wrong
///     shifts every line number a rule reports.
/// </summary>
internal static class SourceTreeLines
{
    /// <summary>
    ///     Splits text on "\r\n", "\r" or "\n", and does NOT produce a
    ///     trailing empty line for a file that ends with a terminator — the
    ///     shape <c>File.ReadAllLines</c> produces.
    /// </summary>
    /// <param name="text">The text to split.</param>
    internal static string[] SplitLikeReadLine(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                lines.Add(text[start..i]);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    /// <summary>
    ///     Counts the lines of text the way <c>File.ReadAllLines</c> counts
    ///     them, without allocating the lines themselves.
    /// </summary>
    /// <param name="text">The text to count the lines of.</param>
    internal static int CountLikeReadLine(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                count++;
                start = i + 1;
            }
        }

        return start < text.Length ? count + 1 : count;
    }
}

/// <summary>
///     One file of the source tree, as the shared snapshot holds it: the raw
///     bytes, the comment-stripped view of them, and the two line-splittings
///     the rules in this project use.
/// </summary>
/// <remarks>
///     <para>
///         The two splittings are NOT interchangeable, and the difference is
///         one entry. <c>text.Split('\n')</c> on a file that ends with a
///         newline yields a trailing EMPTY line; <c>File.ReadAllLines</c>
///         does not. Two rules in this project depend on the difference — one
///         indexes lines as <c>File.ReadAllLines</c> produced them, the other
///         splits on '\n' — so the snapshot carries both rather than forcing
///         one of them onto the other's shape. A snapshot that "tidied" this
///         would silently shift every line number one rule reports.
///     </para>
/// </remarks>
internal sealed class SourceTreeFile
{
    private readonly Lazy<ImmutableArray<string>> _strippedNewlineLines;
    private readonly Lazy<ImmutableArray<string>> _strippedReadLineLines;

    /// <summary>
    ///     Creates a file view over content that is already read.
    /// </summary>
    /// <remarks>
    ///     Internal rather than private because the snapshot is the thing that
    ///     reads the disk: the file view itself is a value, and a value with a
    ///     private constructor that only its owner can build is a value the
    ///     owner cannot build either.
    /// </remarks>
    /// <param name="path">Absolute path of the file.</param>
    /// <param name="text">The file's content.</param>
    /// <param name="strippedText">The content with its comments blanked out.</param>
    /// <param name="rawLineCount">How many lines the raw content has.</param>
    /// <param name="length">The file's length in bytes.</param>
    /// <param name="lastWriteUtc">The file's last write time.</param>
    internal SourceTreeFile(
        string path,
        string text,
        string strippedText,
        int rawLineCount,
        long length,
        DateTime lastWriteUtc)
    {
        Path = path;
        Text = text;
        StrippedText = strippedText;
        RawLineCount = rawLineCount;
        Length = length;
        LastWriteUtc = lastWriteUtc;

        _strippedNewlineLines = new Lazy<ImmutableArray<string>>(
            () =>
            {
                SharedSourceTreeSnapshot.CountLineView();
                return [.. strippedText.Split('\n')];
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        _strippedReadLineLines = new Lazy<ImmutableArray<string>>(
            () =>
            {
                SharedSourceTreeSnapshot.CountLineView();
                return [.. SourceTreeLines.SplitLikeReadLine(strippedText)];
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Absolute path of the file.</summary>
    internal string Path { get; }

    /// <summary>The file's content, exactly as <c>File.ReadAllText</c> returned it.</summary>
    internal string Text { get; }

    /// <summary>The content with comments blanked out, line for line.</summary>
    internal string StrippedText { get; }

    /// <summary>How many lines the raw content has, the way <c>File.ReadAllLines</c> counts them.</summary>
    internal int RawLineCount { get; }

    /// <summary>The file's length in bytes, as it was when the snapshot read it.</summary>
    internal long Length { get; }

    /// <summary>The file's last write time, as it was when the snapshot read it.</summary>
    internal DateTime LastWriteUtc { get; }

    /// <summary>
    ///     The comment-stripped content split on '\n' — the shape a rule gets
    ///     from <c>text.Split('\n')</c>, trailing empty line included.
    /// </summary>
    internal ImmutableArray<string> StrippedNewlineLines => _strippedNewlineLines.Value;

    /// <summary>
    ///     The comment-stripped content split the way <c>File.ReadAllLines</c>
    ///     splits: on "\r\n", "\r" or "\n", with no trailing empty line.
    /// </summary>
    internal ImmutableArray<string> StrippedReadLineLines => _strippedReadLineLines.Value;

    /// <summary>
    ///     A file view over text that is ALREADY comment-stripped — the shape
    ///     a rule's synthetic control needs, where there is no file on disk.
    /// </summary>
    /// <param name="path">The path the source is filed under in a report.</param>
    /// <param name="strippedText">The comment-stripped source.</param>
    internal static SourceTreeFile FromStrippedText(string path, string strippedText) =>
        new(
            path,
            strippedText,
            strippedText,
            SourceTreeLines.CountLikeReadLine(strippedText),
            strippedText.Length,
            DateTime.MinValue);
}

/// <summary>One file the snapshot read: its path, its size and when it was last written.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Length">Size in bytes when the snapshot read it.</param>
/// <param name="LastWriteUtc">Last write time when the snapshot read it.</param>
internal readonly record struct SourceTreeEntry(string Path, long Length, DateTime LastWriteUtc);

/// <summary>One directory the snapshot walked: the directory, and the files it held.</summary>
/// <param name="Directory">Absolute path of the directory.</param>
/// <param name="Files">The <c>*.cs</c> files it held, build output excluded.</param>
internal readonly record struct SourceTreeEnumeration(string Directory, ImmutableArray<string> Files);

/// <summary>
///     The one snapshot of the repository's source tree that the architecture
///     rules read from: the file index, the file contents, and the two
///     line-splittings, all built once and shared by every rule that asks.
/// </summary>
internal sealed class SharedSourceTreeSnapshot
{
    private static int _instancesCreated;
    private static int _lineViewComputations;

    private readonly ConcurrentDictionary<string, Lazy<SourceTreeFile?>> _files =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<ImmutableArray<string>>> _enumerations =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SourceTreeEnumeration> _capturedEnumerations =
        new(StringComparer.Ordinal);

    // The capture bookkeeping is under ONE lock, so the read count and the
    // captured count can never disagree: the guard reads them as a pair and
    // compares them, and a torn read between two threads would otherwise make
    // the control flaky rather than strict.
    private readonly object _captureLock = new();

    private readonly Dictionary<string, SourceTreeEntry> _capturedFiles =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _readsPerFile = new(StringComparer.Ordinal);

    private int _fileReads;

    /// <summary>
    ///     The snapshot of this process. Built on first use; every rule that
    ///     reads the tree reads THIS one.
    /// </summary>
    internal static SharedSourceTreeSnapshot Shared { get; } = new();

    /// <summary>
    ///     How many snapshots this process has created. It must stay at 1: a
    ///     second one means some rule kept a private copy of the tree, which is
    ///     the defect this snapshot exists to remove.
    /// </summary>
    internal static int InstancesCreated
    {
        get
        {
            // Force the one snapshot into existence, so the answer is "how
            // many snapshots exist" and not "how many existed when somebody
            // last asked".
            _ = Shared;
            return Volatile.Read(ref _instancesCreated);
        }
    }

    /// <summary>
    ///     Creates a snapshot. Internal rather than private ON PURPOSE: a rule
    ///     that quietly built its own would then be detectable, and the guard
    ///     in <c>SharedSourceTreeSnapshotTests</c> is a control that has to be
    ///     able to fail.
    /// </summary>
    internal SharedSourceTreeSnapshot() => Interlocked.Increment(ref _instancesCreated);

    /// <summary>
    ///     Every <c>*.cs</c> file under a directory, minus build output and
    ///     sibling worktrees — the filter every rule in this project agrees on.
    ///     The walk itself is done once per directory.
    /// </summary>
    /// <param name="absoluteDirectory">The directory to walk.</param>
    internal ImmutableArray<string> EnumerateCsFiles(string absoluteDirectory)
    {
        Lazy<ImmutableArray<string>> lazy = _enumerations.GetOrAdd(
            absoluteDirectory,
            directory => new Lazy<ImmutableArray<string>>(
                () => WalkCsFiles(directory),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>
    ///     The snapshot's view of one file: its content, its comment-stripped
    ///     content and its line-splittings, read once and shared.
    /// </summary>
    /// <param name="absolutePath">The file to read.</param>
    /// <returns>The file view, or <c>null</c> when the file cannot be read.</returns>
    internal SourceTreeFile? Get(string absolutePath)
    {
        Lazy<SourceTreeFile?> lazy = _files.GetOrAdd(
            absolutePath,
            path => new Lazy<SourceTreeFile?>(
                () => ReadFile(path),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>The files this snapshot read, with the size and mtime it saw.</summary>
    internal ImmutableArray<SourceTreeEntry> CapturedFiles
    {
        get
        {
            lock (_captureLock)
            {
                return [.. _capturedFiles.Values.OrderBy(e => e.Path, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    ///     The read budget, as a PAIR read under one lock: how many times a
    ///     file was read from disk, and how many distinct files were read.
    /// </summary>
    /// <remarks>
    ///     The constant budget the snapshot is held to: ONE read per file, for
    ///     the whole assembly, however many rules ask. It is a COUNT and not a
    ///     duration on purpose — a duration measures the runner, a count
    ///     measures the code. The pair is read under the capture lock so the
    ///     control that compares the two cannot see a torn state.
    /// </remarks>
    internal (int Reads, int Files) ReadBudget
    {
        get
        {
            lock (_captureLock)
            {
                return (_fileReads, _capturedFiles.Count);
            }
        }
    }

    /// <summary>How many times ONE file was read from disk by this snapshot.</summary>
    /// <param name="absolutePath">The file to count the reads of.</param>
    internal int ReadsOf(string absolutePath)
    {
        lock (_captureLock)
        {
            return _readsPerFile.GetValueOrDefault(absolutePath);
        }
    }

    /// <summary>
    ///     How many times a file's line view was computed by this snapshot.
    /// </summary>
    /// <remarks>
    ///     Two per file at most: the '\n' split and the <c>ReadAllLines</c>
    ///     split. A third would mean a rule re-derived a view the snapshot
    ///     already had — the re-split that made the panel/overlay rule's caller
    ///     search cost a whole tree split per hand-off.
    /// </remarks>
    internal static int LineViewComputations => Volatile.Read(ref _lineViewComputations);

    /// <summary>Counts one computed line view. Called by the file view's lazy halves.</summary>
    internal static void CountLineView() => Interlocked.Increment(ref _lineViewComputations);

    /// <summary>The directories this snapshot walked, with the files each held.</summary>
    internal ImmutableArray<SourceTreeEnumeration> CapturedEnumerations =>
        [.. _capturedEnumerations.Values.OrderBy(e => e.Directory, StringComparer.Ordinal)];

    /// <summary>
    ///     Whether a path is build output or a sibling worktree — the filter
    ///     every rule in this project agrees on.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT <c>SourceScan.IsBuildOutput</c>: that one also
    ///     rejects any path containing "/tests/", which is right for a gate
    ///     judging product source and fatal for a gate judging the test tree.
    ///     The rules that want the test tree apply their own filter on top of
    ///     this one; they do not share it.
    /// </remarks>
    /// <param name="path">The path to test.</param>
    internal static bool IsBuildOutputPath(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.Ordinal)
            || normalised.Contains("/bin/", StringComparison.Ordinal)
            || normalised.Contains("/.worktrees/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Walks a directory for <c>*.cs</c> files, build output excluded.
    /// </summary>
    /// <param name="directory">The directory to walk.</param>
    private ImmutableArray<string> WalkCsFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (!IsBuildOutputPath(file))
            {
                found.Add(file);
            }
        }

        var files = found.ToImmutableArray();
        _capturedEnumerations[directory] = new SourceTreeEnumeration(directory, files);
        return files;
    }

    /// <summary>
    ///     Reads one file into a file view, recording what it saw so the guard
    ///     can prove the tree did not change underneath the snapshot.
    /// </summary>
    /// <param name="path">The file to read.</param>
    private SourceTreeFile? ReadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // A file that cannot be read is a discovery problem, not a rule
            // failure: every rule that reads files pairs the read with a
            // discoverability self-check, and `SourceScan.TryReadAllText`
            // returned null for the same case before this snapshot existed.
            return null;
        }

        var info = new FileInfo(path);

        lock (_captureLock)
        {
            _capturedFiles[path] = new SourceTreeEntry(path, info.Length, info.LastWriteTimeUtc);
            _readsPerFile[path] = _readsPerFile.GetValueOrDefault(path) + 1;
            _fileReads++;
        }

        return new SourceTreeFile(
            path,
            text,
            SourceScan.StripComments(text),
            SourceTreeLines.CountLikeReadLine(text),
            info.Length,
            info.LastWriteTimeUtc);
    }
}

/// <summary>
///     The invariant the shared snapshot has to hold, and the checker that
///     decides whether it does: a snapshot that no longer describes the tree
///     is a gate that is measuring something other than what it says.
/// </summary>
internal static class SourceTreeSnapshotInvariants
{
    /// <summary>
    ///     The violations between what the snapshot captured and what the tree
    ///     looks like now. Empty when the snapshot still describes the tree.
    /// </summary>
    /// <remarks>
    ///     A pure function over two sets of facts, deliberately: the guard
    ///     that runs it against the real tree and the self-test that proves it
    ///     can FAIL are the same code, so the self-test is evidence about the
    ///     guard rather than a restatement of it.
    /// </remarks>
    /// <param name="capturedEnumerations">The directories the snapshot walked, and what each held.</param>
    /// <param name="capturedFiles">The files the snapshot read, and what it saw.</param>
    /// <param name="liveEnumerations">The same directories, walked again now.</param>
    /// <param name="liveFiles">The same files, stat'ed again now.</param>
    /// <returns>One message per violation; empty when the snapshot still matches.</returns>
    internal static ImmutableArray<string> Verify(
        IReadOnlyList<SourceTreeEnumeration> capturedEnumerations,
        IReadOnlyList<SourceTreeEntry> capturedFiles,
        IReadOnlyList<SourceTreeEnumeration> liveEnumerations,
        IReadOnlyList<SourceTreeEntry> liveFiles)
    {
        var violations = ImmutableArray.CreateBuilder<string>();

        var liveByPath = liveFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (SourceTreeEntry captured in capturedFiles)
        {
            if (!liveByPath.TryGetValue(captured.Path, out SourceTreeEntry live))
            {
                violations.Add(
                    $"{captured.Path} — the snapshot read this file and it is no longer in the tree; "
                    + "a rule's verdict was computed over bytes the tree no longer has.");
                continue;
            }

            if (live.Length != captured.Length)
            {
                violations.Add(
                    $"{captured.Path} — the snapshot read {captured.Length} B and the file is now "
                    + $"{live.Length} B; the tree changed under the shared snapshot.");
            }

            if (live.LastWriteUtc != captured.LastWriteUtc)
            {
                violations.Add(
                    $"{captured.Path} — the snapshot read a file last written at "
                    + $"{captured.LastWriteUtc:O} and it is now {live.LastWriteUtc:O}; the tree "
                    + "changed under the shared snapshot.");
            }
        }

        var liveDirectories = liveEnumerations.ToDictionary(e => e.Directory, StringComparer.Ordinal);
        foreach (SourceTreeEnumeration captured in capturedEnumerations)
        {
            if (!liveDirectories.TryGetValue(captured.Directory, out SourceTreeEnumeration live))
            {
                violations.Add(
                    $"{captured.Directory} — the snapshot walked this directory and it is no longer "
                    + "there; a rule's scope was computed over a directory the tree no longer has.");
                continue;
            }

            var capturedSet = captured.Files.ToHashSet(StringComparer.Ordinal);
            var liveSet = live.Files.ToHashSet(StringComparer.Ordinal);

            foreach (string added in liveSet.Except(capturedSet).OrderBy(p => p, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{added} — this file is in the directory the snapshot walked but was not in the "
                    + "snapshot; a rule that reads the tree would have seen it before the snapshot existed.");
            }

            foreach (string removed in capturedSet.Except(liveSet).OrderBy(p => p, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{removed} — this file was in the directory the snapshot walked and is no longer "
                    + "there; a rule's scope is now wider than the tree it was measured against.");
            }
        }

        return violations.ToImmutable();
    }
}
