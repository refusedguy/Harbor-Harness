using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F115. <see cref="BranchNameGenerator" /> is a pure parser over a
///     proposed branch name. These tests pin its observable output (case,
///     separators, decoration, length cap and rejection cases) without calling
///     an LLM, starting a process or touching a repository.
/// </summary>
public class BranchNameGeneratorTests
{
    [Test]
    public async Task NormalisesSpacesUnderscoresAndCase()
    {
        await Assert.That(BranchNameGenerator.Parse("Add Login Flow")).IsEqualTo("add-login-flow");
        await Assert.That(BranchNameGenerator.Parse("add_login_flow")).IsEqualTo("add-login-flow");
        await Assert.That(BranchNameGenerator.Parse("ADD Login-Flow")).IsEqualTo("add-login-flow");
    }

    [Test]
    public async Task FlattensNestedGitRefIntoKebabCase()
    {
        await Assert.That(BranchNameGenerator.Parse("feature/add-login")).IsEqualTo("feature-add-login");
        await Assert.That(BranchNameGenerator.Parse("refs/heads/fix.Ticket_42")).IsEqualTo("refs-heads-fix-ticket-42");
    }

    [Test]
    public async Task StripsDecorationAndPrefixes()
    {
        await Assert.That(BranchNameGenerator.Parse("`Add Login`")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("\"add login\"")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("'Add Login'")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("Branch name: Add Login")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("BRANCH: Add Login")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("Here is the branch name: Add Login")).IsEqualTo("add-login");
    }

    [Test]
    public async Task UsesFirstMeaningfulLineAndSkipsCodeFences()
    {
        await Assert.That(BranchNameGenerator.Parse("```\nAdd Login\n```")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("\n\n  Add Login  \nsecond line")).IsEqualTo("add-login");
        await Assert.That(BranchNameGenerator.Parse("~~~text\nAdd Login\n~~~")).IsEqualTo("add-login");
    }

    [Test]
    public async Task CollapsesSeparatorsAndTrimsEdges()
    {
        await Assert.That(BranchNameGenerator.Parse("  --Add___Login///Flow--  ")).IsEqualTo("add-login-flow");
        await Assert.That(BranchNameGenerator.Parse("...Add...Login...")).IsEqualTo("add-login");
    }

    [Test]
    public async Task TruncatesLongSlugAtWordBoundary()
    {
        var parts = new string[40];
        for (int i = 0; i < parts.Length; i++) parts[i] = $"word{i}";
        string longName = string.Join('-', parts);

        string? parsed = BranchNameGenerator.Parse(longName);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Length).IsLessThanOrEqualTo(BranchNameGenerator.MaxLength);
        await Assert.That(parsed!.EndsWith('-')).IsFalse();
        await Assert.That(parsed!.Contains("--")).IsFalse();
    }

    [Test]
    public async Task ReturnsNullForEmptyOrSymbolOnlyInput()
    {
        await Assert.That(BranchNameGenerator.Parse(null)).IsNull();
        await Assert.That(BranchNameGenerator.Parse("   ")).IsNull();
        await Assert.That(BranchNameGenerator.Parse("```\n```")).IsNull();
        await Assert.That(BranchNameGenerator.Parse("--- / ... ###")).IsNull();
        await Assert.That(BranchNameGenerator.Parse("Branch name:")).IsNull();
    }

    [Test]
    public async Task RejectsReservedHeadName()
    {
        await Assert.That(BranchNameGenerator.Parse("HEAD")).IsNull();
        await Assert.That(BranchNameGenerator.Parse("head")).IsNull();
    }

    [Test]
    public async Task TryParseReportsFailureWithEmptyOutParam()
    {
        bool failed = BranchNameGenerator.TryParse("   ", out string empty);
        await Assert.That(failed).IsFalse();
        await Assert.That(empty).IsEqualTo(string.Empty);

        bool succeeded = BranchNameGenerator.TryParse("Add Login", out string good);
        await Assert.That(succeeded).IsTrue();
        await Assert.That(good).IsEqualTo("add-login");
    }

    [Test]
    public async Task ParseIsIdempotent()
    {
        string? once = BranchNameGenerator.Parse("  Branch name: Fix___Add.Login Flow  ");

        await Assert.That(once).IsNotNull();
        await Assert.That(BranchNameGenerator.Parse(once)).IsEqualTo(once);
    }
}
