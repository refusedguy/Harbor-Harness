// #2680 F152. Behaviour tests for the skill discovery path guard: these pin
// which untrusted fragments are accepted, without resolving a path, reading a
// file or touching the skill loader. A rejection here is the decision the
// loader acts on later.

using System.Text;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class SkillPathValidatorTests
{
    [Test]
    [Arguments("code-review")]
    [Arguments("my_skill.v2")]
    [Arguments("a")]
    [Arguments("A0")]
    [Arguments("x-y_z.9")]
    public async Task AcceptsOrdinarySkillSegments(string segment)
    {
        await Assert.That(SkillPathValidator.IsSafeSegment(segment)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments(".hidden")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("a b")]
    [Arguments("a:b")]
    [Arguments("a\nb")]
    [Arguments("a\tb")]
    [Arguments("a\u00e9")]
    public async Task RejectsUnsafeSegments(string? segment)
    {
        await Assert.That(SkillPathValidator.IsSafeSegment(segment)).IsFalse();
    }

    [Test]
    public async Task RejectsSegmentLongerThanBound()
    {
        string tooLong = new('a', SkillPathValidator.MaxSegmentLength + 1);
        string atBound = new('a', SkillPathValidator.MaxSegmentLength);

        await Assert.That(SkillPathValidator.IsSafeSegment(tooLong)).IsFalse();
        await Assert.That(SkillPathValidator.IsSafeSegment(atBound)).IsTrue();
    }

    [Test]
    [Arguments("code-review/SKILL.md")]
    [Arguments("skills/code-review/SKILL.md")]
    [Arguments("a/b/c")]
    public async Task AcceptsRelativePathsOfSafeSegments(string path)
    {
        await Assert.That(SkillPathValidator.IsSafeRelativePath(path)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("/etc/passwd")]
    [Arguments("a/../../b")]
    [Arguments("a/../b")]
    [Arguments("a/./b")]
    [Arguments("a//b")]
    [Arguments("a/")]
    [Arguments("a\\b")]
    [Arguments("C:\\skills\\SKILL.md")]
    [Arguments("a:stream")]
    [Arguments("a b/c")]
    [Arguments("./skills")]
    public async Task RejectsEscapingOrMalformedRelativePaths(string? path)
    {
        await Assert.That(SkillPathValidator.IsSafeRelativePath(path)).IsFalse();
    }

    [Test]
    public async Task RejectsRelativePathLongerThanBound()
    {
        var builder = new StringBuilder();
        while (builder.Length <= SkillPathValidator.MaxRelativePathLength)
        {
            if (builder.Length > 0) builder.Append('/');
            builder.Append("segment");
        }

        string tooLong = builder.ToString();
        await Assert.That(tooLong.Length).IsGreaterThan(SkillPathValidator.MaxRelativePathLength);
        await Assert.That(SkillPathValidator.IsSafeRelativePath(tooLong)).IsFalse();
    }

    [Test]
    public async Task EverySegmentOfAnAcceptedPathIsItselfSafe()
    {
        const string path = "skills/code-review/SKILL.md";

        await Assert.That(SkillPathValidator.IsSafeRelativePath(path)).IsTrue();
        foreach (string segment in path.Split('/'))
        {
            await Assert.That(SkillPathValidator.IsSafeSegment(segment)).IsTrue();
        }
    }
}
