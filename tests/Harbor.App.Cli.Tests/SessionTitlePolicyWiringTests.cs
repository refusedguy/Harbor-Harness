// SessionTitlePolicyWiringTests.cs — issue #3414.
//
// SessionTitlePolicy was a pure leaf with zero product callers: the REPL's
// SessionTitleService built its naming request by hand, concatenating an
// instruction with a manually truncated first user message. The service now
// delegates the naming context to SessionTitlePolicy.BuildPrompt, so the
// prompt that produces a session title is bounded, blank-safe and ordered by
// the policy rather than by ad-hoc string code. These tests drive the exact
// helper the live naming request uses and observe the resulting prompt text.

using Harbor.App.Cli.Repl;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Tests;

public class SessionTitlePolicyWiringTests
{
    [Test]
    public async Task BuildNamingPrompt_ContextComesFromPolicy()
    {
        string[] messages = ["  ", "first prompt", "second prompt"];

        string naming = SessionTitleService.BuildNamingPrompt(messages);
        string policyContext = SessionTitlePolicy.BuildPrompt(messages);

        string expected =
            "Generate a short chat title (2-5 words) for a conversation that started " +
            "with these user messages. Reply with the title only — no quotes, no punctuation " +
            "around it, same language as the messages:\n" + policyContext;

        await Assert.That(naming).IsEqualTo(expected);
        await Assert.That(naming).Contains("- first prompt");
        await Assert.That(naming).Contains("- second prompt");
        await Assert.That(naming).DoesNotContain("-   ");
    }

    [Test]
    public async Task BuildNamingPrompt_RespectsPolicyBounds()
    {
        var messages = new List<string>();
        for (int i = 1; i <= 8; i++)
        {
            messages.Add($"message-{i}");
        }

        messages.Add(new string('x', SessionTitlePolicy.DefaultMessageChars + 100));

        string naming = SessionTitleService.BuildNamingPrompt(messages);
        string[] lines = naming.Split('\n');

        // Instruction line + at most DefaultPromptMessages bullets.
        await Assert.That(lines.Length - 1).IsEqualTo(SessionTitlePolicy.DefaultPromptMessages);
        await Assert.That(naming).DoesNotContain("- message-1\n");
        await Assert.That(naming).Contains("- message-8");

        // The longest message is truncated to the policy's per-message cap.
        string capped = new('x', SessionTitlePolicy.DefaultMessageChars);
        await Assert.That(naming).Contains("- " + capped);
        await Assert.That(naming).DoesNotContain("- " + capped + "x");
    }
}
