using CSharpFunctionalExtensions;
using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

/// <summary>
///     S9 checks wiring (#397): the operator's <c>--checks</c> file parses
///     into shell <see cref="CheckSpec" /> values with derived names. Pure
///     file read — no git, no processes, runs on every OS.
/// </summary>
public class CheckSpecParserTests
{
    [Test]
    public async Task NullPath_IsZeroDeclaredChecks()
    {
        Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(null);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CommentsAndBlanks_AreSkipped_NamesAreOneBased()
    {
        string path = NewTempFile("# a comment\n\n  echo hello\n# another\n\ndate -u\n");

        try
        {
            Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

            await Assert.That(parsed.IsSuccess).IsTrue();
            await Assert.That(parsed.Value.Count).IsEqualTo(2);
            await Assert.That(parsed.Value[0].Name).IsEqualTo("check-1");
            await Assert.That(parsed.Value[0].FileName).IsEqualTo("echo hello");
            await Assert.That(parsed.Value[0].Shell).IsTrue();
            await Assert.That(parsed.Value[1].Name).IsEqualTo("check-2");
            await Assert.That(parsed.Value[1].FileName).IsEqualTo("date -u");
            await Assert.That(parsed.Value[1].Shell).IsTrue();
        }
        finally
        {
            DeleteQuiet(path);
        }
    }

    [Test]
    public async Task EmptyFile_IsZeroDeclaredChecks()
    {
        string path = NewTempFile("# only a comment\n\n");

        try
        {
            Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

            await Assert.That(parsed.IsSuccess).IsTrue();
            await Assert.That(parsed.Value.Count).IsEqualTo(0);
        }
        finally
        {
            DeleteQuiet(path);
        }
    }

    [Test]
    public async Task MissingFile_NamesThePath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-nochecks-{Guid.NewGuid():N}.txt");

        Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains(path)).IsTrue();
    }

    [Test]
    public async Task OverLongLine_NamesTheCap()
    {
        string path = NewTempFile(new string('x', CheckSpecParser.MaxLineChars + 1) + "\n");

        try
        {
            Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

            await Assert.That(parsed.IsFailure).IsTrue();
            await Assert.That(parsed.Error.Contains("per-line cap")).IsTrue();
        }
        finally
        {
            DeleteQuiet(path);
        }
    }

    [Test]
    public async Task NullCharacter_IsRejected()
    {
        string path = NewTempFile("echo a\0b\n");

        try
        {
            Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

            await Assert.That(parsed.IsFailure).IsTrue();
            await Assert.That(parsed.Error.Contains("null character")).IsTrue();
        }
        finally
        {
            DeleteQuiet(path);
        }
    }

    [Test]
    public async Task OverCheckLimit_IsRejected()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i <= CheckSpecParser.MaxChecks; i++)
            sb.Append("true\n");
        string path = NewTempFile(sb.ToString());

        try
        {
            Result<IReadOnlyList<CheckSpec>> parsed = CheckSpecParser.ParseFile(path);

            await Assert.That(parsed.IsFailure).IsTrue();
            await Assert.That(parsed.Error.Contains(CheckSpecParser.MaxChecks.ToString())).IsTrue();
        }
        finally
        {
            DeleteQuiet(path);
        }
    }

    private static string NewTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-checks-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
