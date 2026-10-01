using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Harbor.Application.Filesystem;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #934 — the Infrastructure half of the text-file seam.
///     <c>CodeEditorViewModel</c> used to call <c>File.*</c> itself. These tests pin
///     the properties the port's contract actually promises, because each is a way
///     the port can be worse than the code it replaced: it can throw where the
///     caller has to render something, it can report "no such file" for a file it
///     simply could not reach, and it can quietly change what a saved file looks
///     like on disk.
/// </summary>
/// <remarks>
///     The contract's OTHER load-bearing promise — that no member runs its syscall
///     on the calling thread — is deliberately NOT unit-tested here. It was
///     attempted and removed: distinguishing "ran on a pool thread" from "ran on
///     the caller's thread" needs a timing window, and a pool thread can reuse the
///     caller's id, so the test would have been a coin flip dressed as a guarantee.
///     The evidence is the diff instead: the synchronous <c>File.Exists</c> became
///     <c>Task.Run</c> inside <see cref="SystemTextFileStore" />, and the
///     obligation is written into the contract text for the next implementer.
/// </remarks>
public sealed class SystemTextFileStoreTests
{
    private static SystemTextFileStore Store() => new(NullLogger<SystemTextFileStore>.Instance);

    private static string Sandbox()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harbor-934-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── the round trip ────────────────────────────────────────────────────

    [Test]
    public async Task WriteThenRead_ReturnsWhatWasWritten()
    {
        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "note.txt");
            SystemTextFileStore store = Store();

            Result written = await store.WriteAsync(path, "hello\nworld");
            Result<string> read = await store.ReadAsync(path);

            await Assert.That(written.IsSuccess).IsTrue();
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value).IsEqualTo("hello\nworld");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Write_OverwritesRatherThanAppends()
    {
        // The editor writes the WHOLE buffer every time, so an appending
        // implementation would corrupt a file on the second save. This is the one
        // behaviour the old `File.WriteAllTextAsync` call gave for free and a
        // hand-rolled port could plausibly get wrong.
        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "note.txt");
            SystemTextFileStore store = Store();

            await store.WriteAsync(path, "first");
            await store.WriteAsync(path, "second");
            Result<string> read = await store.ReadAsync(path);

            await Assert.That(read.Value).IsEqualTo("second");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Write_CreatesTheFileWhenItIsNotThere()
    {
        // `SaveAsAsync` hands the port a path from the picker that has never
        // existed. A store that required the file to be there first would turn
        // "Save as" into a failure.
        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "brand-new.cs");
            SystemTextFileStore store = Store();

            Result written = await store.WriteAsync(path, "// new");
            Result<bool> present = await store.ExistsAsync(path);

            await Assert.That(written.IsSuccess).IsTrue();
            await Assert.That(present.Value).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the encoding promise ──────────────────────────────────────────────

    [Test]
    public async Task Write_EmitsNoByteOrderMark()
    {
        // `Encoding.UTF8` has `encoderShouldEmitUTF8Identifier == true`, and the
        // BCL's `WriteAllTextAsync(string, string, Encoding)` overload would put
        // three invisible bytes at the head of every file the editor saved. Nothing
        // in the UI would show it; a build script keying on the first line would.
        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "note.txt");
            SystemTextFileStore store = Store();

            await store.WriteAsync(path, "abc");
            byte[] bytes = await File.ReadAllBytesAsync(path);

            await Assert.That(bytes.Length).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Write_ProducesTheSameBytesAsTheBOMlessEncoding()
    {
        string dir = Sandbox();
        try
        {
            const string Content = "naïve — ünicode";
            string path = Path.Combine(dir, "note.txt");
            SystemTextFileStore store = Store();

            await store.WriteAsync(path, Content);
            byte[] actual = await File.ReadAllBytesAsync(path);

            // Hex rather than a byte-by-byte comparer: one differing byte names
            // itself in the failure message instead of printing two arrays.
            await Assert.That(Convert.ToHexString(actual))
                .IsEqualTo(Convert.ToHexString(new UTF8Encoding(false).GetBytes(Content)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Read_HonoursAByteOrderMarkWithoutLeavingItInTheContent()
    {
        // A file saved by another editor may carry a BOM. The BCL's own overload
        // detects and strips it; a port that read raw bytes would hand the caller a
        // leading U+FEFF, which then gets written back out on the next save.
        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "bom.txt");
            await File.WriteAllTextAsync(path, "content", new UTF8Encoding(true));

            Result<string> read = await Store().ReadAsync(path);

            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value).IsEqualTo("content");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── expected failures are results, not exceptions ─────────────────────

    [Test]
    public async Task Read_AMissingFileIsAFailedResult()
    {
        string dir = Sandbox();
        try
        {
            Result<string> read = await Store().ReadAsync(Path.Combine(dir, "absent.txt"));

            await Assert.That(read.IsFailure).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Exists_ReportsAMissingFileAsFalse_NotAsAFailure()
    {
        string dir = Sandbox();
        try
        {
            Result<bool> present = await Store().ExistsAsync(Path.Combine(dir, "absent.txt"));

            await Assert.That(present.IsSuccess).IsTrue();
            await Assert.That(present.Value).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Exists_ReportsAnUnreadablePathAsAFailure_NotAsFalse()
    {
        // The distinction the contract draws, and the reason `ExistsAsync` does not
        // simply forward `File.Exists`: that call swallows EVERY exception and
        // answers `false`, so a caller cannot tell "not there" from "I could not
        // look" — and a view that renders "file not found" for a malformed path is
        // making a claim about the user's disk it cannot support.
        string dir = Sandbox();
        try
        {
            Result<bool> present = await Store().ExistsAsync(Path.Combine(dir, "with\0nul", "x.txt"));

            await Assert.That(present.IsFailure).IsTrue()
                .Because(
                    "a malformed path must come back as a FAILED result, not as `false`. `File.Exists` "
                    + "answers false for it, and inheriting that behaviour is exactly what the contract "
                    + "forbids: `false` means 'not there', and the caller renders that as 'file not "
                    + "found'.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Read_ADirectoryPathIsAFailedResult()
    {
        // `File.ReadAllText` on a directory throws `UnauthorizedAccessException` on
        // Unix. The caller still has to render something, so it has to be a result.
        string dir = Sandbox();
        try
        {
            Result<string> read = await Store().ReadAsync(dir);

            await Assert.That(read.IsFailure).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Write_IntoAMissingDirectoryIsAFailedResult()
    {
        string dir = Sandbox();
        try
        {
            Result written = await Store().WriteAsync(Path.Combine(dir, "no-such-dir", "x.txt"), "c");

            await Assert.That(written.IsFailure).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Write_ToAReadOnlyPathIsAFailedResult_NotAnException()
    {
        // The other side of the contract's "never throws for an expected failure":
        // a permission denial on save must reach the user as a toast, not as an
        // unhandled exception out of a [RelayCommand].
        if (OperatingSystem.IsWindows())
        {
            // NTFS ACLs do not map onto the chmod bit this test sets, and the
            // directory-mode shortcut is unreliable on Windows. Skipped there
            // rather than asserted on a platform where it does not hold.
            return;
        }

        string dir = Sandbox();
        try
        {
            string path = Path.Combine(dir, "locked.txt");
            await File.WriteAllTextAsync(path, "x");
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Result written = await Store().WriteAsync(path, "y");

            await Assert.That(written.IsFailure).IsTrue();
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the seam is substitutable ─────────────────────────────────────────

    [Test]
    public async Task AFake_StandsInForTheRealStore()
    {
        // The reason the port exists at all: a view-model that opens a file can now
        // be tested without a byte on disk. Before #934 there was no seam, so this
        // assertion was not expressible — which is the same thing
        // AvaloniaTextFileIoRules checks structurally on the app side.
        ITextFileStore store = new InMemoryTextFileStore();

        Result written = await store.WriteAsync("/virtual/a.cs", "class A { }");
        Result<string> read = await store.ReadAsync("/virtual/a.cs");
        Result<bool> present = await store.ExistsAsync("/virtual/a.cs");
        Result<bool> absent = await store.ExistsAsync("/virtual/nope.cs");
        Result<string> missing = await store.ReadAsync("/virtual/nope.cs");

        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(read.Value).IsEqualTo("class A { }");
        await Assert.That(present.Value).IsTrue();
        await Assert.That(absent.Value).IsFalse();
        await Assert.That(missing.IsFailure).IsTrue();
    }

    /// <summary>A fake, and the thing a test of the view-model now uses instead of a temp file.</summary>
    private sealed class InMemoryTextFileStore : ITextFileStore
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(_files.ContainsKey(path)));

        public Task<Result<string>> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(path, out string? content)
                ? Result.Success(content)
                : Result.Failure<string>($"Cannot read '{path}': no such file."));

        public Task<Result> WriteAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            _files[path] = contents;
            return Task.FromResult(Result.Success());
        }
    }
}
