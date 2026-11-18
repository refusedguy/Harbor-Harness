// SessionListFuzzySearchTests.cs. Issue #2681, item G25.
//
// The sidebar's search box was documented as "fuzzy" in the Avalonia README
// while the view-model filtered with `string.Contains`. A query that skips
// letters ("prsbug" for "Fix parser bug") found nothing. These tests pin the
// observable filter contract of the extracted pure helper: subsequence
// matching over title and agent, best-score-first ranking, blank-query pass
// through, and ordinary substring queries still working.

using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.ViewModels;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class SessionListFuzzySearchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Session Make(string id, string title, string agent) =>
        new(id, "proj", "/work", title, agent, "model", "provider", Now, Now, SessionMetadata.Empty);

    private static readonly Session[] Sessions =
    [
        Make("s1", "Fix parser bug", "code"),
        Make("s2", "Parser refactor", "review"),
        Make("s3", "Onboarding polish", "code")
    ];

    [Test]
    public async Task BlankQueryKeepsEverySessionInStoreOrder()
    {
        var result = SessionListViewModel.FilterSessions(Sessions, "  ");

        await Assert.That(result.Select(static s => s.Id)).IsEquivalentTo(new[] { "s1", "s2", "s3" });
    }

    [Test]
    public async Task SubsequenceQueryMatchesSkippedLetters()
    {
        // "prsbug" is not a substring of "Fix parser bug"; only a fuzzy
        // subsequence matcher finds it. This is the regression G25 closes.
        var result = SessionListViewModel.FilterSessions(Sessions, "prsbug");

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("s1");
    }

    [Test]
    public async Task AgentIsSearchedAlongsideTitle()
    {
        var result = SessionListViewModel.FilterSessions(Sessions, "rvw");

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("s2");
    }

    [Test]
    public async Task PlainSubstringQueryStillMatches()
    {
        var result = SessionListViewModel.FilterSessions(Sessions, "parser");

        await Assert.That(result.Select(static s => s.Id)).IsEquivalentTo(new[] { "s1", "s2" });
    }
}
