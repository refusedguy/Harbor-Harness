// #2680 F141. Behaviour tests for the Conventional Commits primitive: these
// pin the observable parse result and canonical rendering, without calling an
// LLM, starting a process or touching a repository.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class ConventionalCommitMessageTests
{
    [Test]
    public async Task ParsesSimpleHeader()
    {
        var message = ConventionalCommitMessage.Parse("feat: add login");

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Type).IsEqualTo("feat");
        await Assert.That(message.Scope).IsNull();
        await Assert.That(message.Breaking).IsFalse();
        await Assert.That(message.Description).IsEqualTo("add login");
        await Assert.That(message.Body).IsNull();
    }

    [Test]
    public async Task ParsesScopeAndBreakingBang()
    {
        var message = ConventionalCommitMessage.Parse("fix(auth)!: tighten token checks");

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Type).IsEqualTo("fix");
        await Assert.That(message.Scope).IsEqualTo("auth");
        await Assert.That(message.Breaking).IsTrue();
        await Assert.That(message.Description).IsEqualTo("tighten token checks");
    }

    [Test]
    public async Task ParsesBodyAfterBlankLine()
    {
        var message = ConventionalCommitMessage.Parse("docs: explain hooks\n\nOne line.\nSecond line.");

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Body).IsEqualTo("One line.\nSecond line.");
    }

    [Test]
    public async Task DetectsBreakingFooterInBody()
    {
        var message = ConventionalCommitMessage.Parse("feat: new api\n\nBREAKING CHANGE: the old endpoint is gone.");

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Breaking).IsTrue();
    }

    [Test]
    [Arguments("feat add login")]
    [Arguments(": add login")]
    [Arguments("feat:")]
    [Arguments("Feat: add login")]
    [Arguments("feat(bad scope): add login")]
    [Arguments("feat(scope: add login")]
    [Arguments("feat(): add login")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RejectsMalformedHeaders(string input)
    {
        await Assert.That(ConventionalCommitMessage.Parse(input)).IsNull();
    }

    [Test]
    [Arguments("```\nfeat: add login\n```")]
    [Arguments("```text\nfeat: add login\n```")]
    [Arguments("~~~\nfeat: add login\n~~~")]
    [Arguments("\n\n  feat: add login  \n")]
    public async Task SkipsFencesAndBlankLines(string input)
    {
        var message = ConventionalCommitMessage.Parse(input);

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Description).IsEqualTo("add login");
        await Assert.That(message.Body).IsNull();
    }

    [Test]
    public async Task NormalisesCrlfAndTrimsDescription()
    {
        var message = ConventionalCommitMessage.Parse("chore:   tidy imports   \r\n\r\nBody line.\r\n");

        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Description).IsEqualTo("tidy imports");
        await Assert.That(message.Body).IsEqualTo("Body line.");
    }

    [Test]
    public async Task RendersCanonicalHeaderAndFullMessage()
    {
        var withBreakingScope = ConventionalCommitMessage.Create("feat", "auth", true, "add login");

        await Assert.That(withBreakingScope).IsNotNull();
        await Assert.That(withBreakingScope!.Header).IsEqualTo("feat(auth)!: add login");
        await Assert.That(withBreakingScope.ToString()).IsEqualTo("feat(auth)!: add login");

        var withBody = ConventionalCommitMessage.Create("fix", null, false, "repair crash", "Details here.");

        await Assert.That(withBody).IsNotNull();
        await Assert.That(withBody!.Header).IsEqualTo("fix: repair crash");
        await Assert.That(withBody.ToString()).IsEqualTo("fix: repair crash\n\nDetails here.");
    }

    [Test]
    public async Task CreateValidatesItsParts()
    {
        await Assert.That(ConventionalCommitMessage.Create("feat", null, false, "add login")).IsNotNull();
        await Assert.That(ConventionalCommitMessage.Create("Feat", null, false, "add login")).IsNull();
        await Assert.That(ConventionalCommitMessage.Create("feat", "bad scope", false, "add login")).IsNull();
        await Assert.That(ConventionalCommitMessage.Create("feat", null, false, "  ")).IsNull();
    }

    [Test]
    public async Task IsKnownTypeChecksTheRecommendedSet()
    {
        await Assert.That(ConventionalCommitMessage.IsKnownType("feat")).IsTrue();
        await Assert.That(ConventionalCommitMessage.IsKnownType("refactor")).IsTrue();
        await Assert.That(ConventionalCommitMessage.IsKnownType("deps")).IsFalse();
        await Assert.That(ConventionalCommitMessage.IsKnownType("Feat")).IsFalse();
        await Assert.That(ConventionalCommitMessage.IsKnownType(null)).IsFalse();
    }

    [Test]
    public async Task TryParseMatchesParseContract()
    {
        bool ok = ConventionalCommitMessage.TryParse("perf: speed up search", out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!.Type).IsEqualTo("perf");

        bool bad = ConventionalCommitMessage.TryParse("not a commit", out var missing);

        await Assert.That(bad).IsFalse();
        await Assert.That(missing).IsNull();
    }

    [Test]
    public async Task CanonicalFormRoundTrips()
    {
        var original = ConventionalCommitMessage.Parse("fix(db)!: drop legacy index\n\nBREAKING CHANGE: migrate first.\nMore detail.");

        await Assert.That(original).IsNotNull();
        var reparsed = ConventionalCommitMessage.Parse(original!.ToString());

        await Assert.That(reparsed).IsEqualTo(original);
    }
}
