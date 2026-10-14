using Harbor.Terminal.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     One-line permission footer density ([steal/opencode] #1182):
///     title + first body line + key hint on a single terminal-safe line,
///     with a mono fallback for pipes.
/// </summary>
public class PermissionMiniLineTests
{
    [Test]
    public async Task Format_FullLine_HasMarkTitleSnippetAndHint()
    {
        string line = PermissionMiniLine.Format("run shell: make build", "$ make build");

        await Assert.That(line).IsEqualTo("⚠ run shell: make build — $ make build [y/a/n]");
    }

    [Test]
    public async Task Format_Mono_UsesAsciiMarkSeparatorAndHint()
    {
        string line = PermissionMiniLine.Format("run shell: make build", "$ make build", mono: true);

        await Assert.That(line).IsEqualTo("! run shell: make build - $ make build [y/a/n]");
    }

    [Test]
    public async Task Format_NoSnippet_OmitsSeparator()
    {
        string line = PermissionMiniLine.Format("edit foo.cs", null);

        await Assert.That(line).IsEqualTo("⚠ edit foo.cs [y/a/n]");
    }

    [Test]
    public async Task Format_BlankTitle_FallsBackToQuestion()
    {
        string line = PermissionMiniLine.Format("  ", "x", mono: true);

        await Assert.That(line).IsEqualTo("! ? - x [y/a/n]");
    }

    [Test]
    public async Task Format_LongLine_TruncatesToWidthWithEllipsis()
    {
        string line = PermissionMiniLine.Format(new string('a', 100), null, 20);

        await Assert.That(line.Length).IsEqualTo(20);
        await Assert.That(line.EndsWith("…", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Format_MonoTruncation_UsesAsciiEllipsis()
    {
        string line = PermissionMiniLine.Format(new string('a', 100), null, 20, mono: true);

        await Assert.That(line.Length).IsEqualTo(20);
        await Assert.That(line.EndsWith("...", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Format_ControlChars_BecomeOneInertLine()
    {
        string line = PermissionMiniLine.Format("t\nt", "a\u001bb", mono: true);

        await Assert.That(line).IsEqualTo("! t t - ab [y/a/n]");
        await Assert.That(line.Contains('\n')).IsFalse();
    }

    [Test]
    public async Task Format_NonPositiveWidth_FallsBackToDefault()
    {
        string line = PermissionMiniLine.Format("t", null, 0);

        await Assert.That(line).IsEqualTo(PermissionMiniLine.Format("t", null, PermissionMiniLine.DefaultMaxWidth));
    }
}
