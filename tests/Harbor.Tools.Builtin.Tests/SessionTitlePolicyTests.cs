// #2680 F114. Behaviour tests for the pure automatic-title policy: when a
// title is due, how the retry cap bites and how the bounded naming prompt is
// built. Nothing here reads a store, asks a model or touches product wiring.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class SessionTitlePolicyTests
{
    [Test]
    public async Task ShouldGenerate_NoTitle_EnoughMessages_True()
    {
        bool due = SessionTitlePolicy.ShouldGenerate(null, SessionTitlePolicy.DefaultMinUserMessages);

        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task ShouldGenerate_ExistingTitle_False()
    {
        bool due = SessionTitlePolicy.ShouldGenerate("Already named", 10);

        await Assert.That(due).IsFalse();
    }

    [Test]
    public async Task ShouldGenerate_WhitespaceTitle_CountsAsMissing()
    {
        bool due = SessionTitlePolicy.ShouldGenerate("   ", SessionTitlePolicy.DefaultMinUserMessages);

        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task ShouldGenerate_TooFewMessages_False()
    {
        bool due = SessionTitlePolicy.ShouldGenerate(null, SessionTitlePolicy.DefaultMinUserMessages - 1);

        await Assert.That(due).IsFalse();
    }

    [Test]
    public async Task ShouldGenerate_InvalidBounds_FailClosed()
    {
        await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, minUserMessages: 0)).IsFalse();
        await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, maxAttempts: 0)).IsFalse();
    }

    [Test]
    public async Task ShouldGenerate_RetryBudget_StopsAtCap()
    {
        TitleAttempts attempts = TitleAttempts.None;
        for (int i = 0; i < SessionTitlePolicy.DefaultMaxAttempts - 1; i++)
        {
            await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, attempts)).IsTrue();
            attempts = attempts.RecordFailure();
        }

        await Assert.That(attempts.Failed).IsEqualTo(SessionTitlePolicy.DefaultMaxAttempts - 1);
        await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, attempts)).IsTrue();

        attempts = attempts.RecordFailure();
        await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, attempts)).IsFalse();
    }

    [Test]
    public async Task TitleAttempts_Clear_ResetsTheBudget()
    {
        TitleAttempts spent = TitleAttempts.None.RecordFailure().RecordFailure().RecordFailure();
        await Assert.That(spent.IsExhausted(SessionTitlePolicy.DefaultMaxAttempts)).IsTrue();

        TitleAttempts cleared = spent.Clear();

        await Assert.That(cleared.Failed).IsEqualTo(0);
        await Assert.That(cleared.IsExhausted(SessionTitlePolicy.DefaultMaxAttempts)).IsFalse();
        await Assert.That(SessionTitlePolicy.ShouldGenerate(null, 10, cleared)).IsTrue();
    }

    [Test]
    public async Task TitleAttempts_IsImmutable()
    {
        TitleAttempts first = TitleAttempts.None.RecordFailure();

        await Assert.That(first.Failed).IsEqualTo(1);
        await Assert.That(TitleAttempts.None.Failed).IsEqualTo(0);
    }

    [Test]
    public async Task BuildPrompt_UsesMostRecentUserMessagesInOrder()
    {
        string prompt = SessionTitlePolicy.BuildPrompt(["oldest", "middle", "newest"], promptMessages: 2);

        await Assert.That(prompt).IsEqualTo("- middle\n- newest");
    }

    [Test]
    public async Task BuildPrompt_SkipsBlankMessages()
    {
        string prompt = SessionTitlePolicy.BuildPrompt(["  ", "first", "", "second"]);

        await Assert.That(prompt).IsEqualTo("- first\n- second");
    }

    [Test]
    public async Task BuildPrompt_TruncatesEachMessage()
    {
        string prompt = SessionTitlePolicy.BuildPrompt([new string('x', 520)], messageChars: 8);

        await Assert.That(prompt).IsEqualTo($"- {new string('x', 8)}");
    }

    [Test]
    public async Task BuildPrompt_EmptyOrUnusableInput_ReturnsEmpty()
    {
        await Assert.That(SessionTitlePolicy.BuildPrompt(null)).IsEqualTo(string.Empty);
        await Assert.That(SessionTitlePolicy.BuildPrompt([])).IsEqualTo(string.Empty);
        await Assert.That(SessionTitlePolicy.BuildPrompt(["   "])).IsEqualTo(string.Empty);
        await Assert.That(SessionTitlePolicy.BuildPrompt(["text"], promptMessages: 0)).IsEqualTo(string.Empty);
        await Assert.That(SessionTitlePolicy.BuildPrompt(["text"], messageChars: 0)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPrompt_IsBoundedByMessageCount()
    {
        string[] messages = Enumerable.Range(0, 50).Select(i => $"message {i}").ToArray();

        string prompt = SessionTitlePolicy.BuildPrompt(messages);

        await Assert.That(prompt.Split('\n').Length).IsEqualTo(SessionTitlePolicy.DefaultPromptMessages);
        await Assert.That(prompt).DoesNotContain("message 43");
        await Assert.That(prompt).Contains("message 49");
    }
}
