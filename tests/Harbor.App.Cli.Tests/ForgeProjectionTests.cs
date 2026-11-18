using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 J02. The forge client's JSON projection is pure. These tests pin
///     the fields a caller sees for a pull request and how bad input behaves,
///     without starting a process.
/// </summary>
public class ForgeProjectionTests
{
    [Test]
    public async Task BuildList_ReadsNumberTitleStateAndUrl()
    {
        const string json = """
            [
              {"number":42,"title":"Fix parser","state":"OPEN","url":"https://github.com/o/r/pull/42"},
              {"number":7,"title":"Docs","state":"MERGED","url":"https://github.com/o/r/pull/7"}
            ]
            """;

        IReadOnlyList<ForgePullRequest> items = ForgeProjection.BuildList(json);

        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items[0].Number).IsEqualTo(42);
        await Assert.That(items[0].Title).IsEqualTo("Fix parser");
        await Assert.That(items[0].State).IsEqualTo("OPEN");
        await Assert.That(items[0].Url).IsEqualTo("https://github.com/o/r/pull/42");
        await Assert.That(items[1].State).IsEqualTo("MERGED");
    }

    [Test]
    public async Task BuildList_SkipsNonObjectsAndMissingFields()
    {
        const string json = """
            [null, 3, {"number":"not-a-number","title":5}, {"number":9,"title":"Ok","state":"OPEN","url":"u"}]
            """;

        IReadOnlyList<ForgePullRequest> items = ForgeProjection.BuildList(json);

        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items[0].Number).IsEqualTo(0);
        await Assert.That(items[0].Title).IsEqualTo(string.Empty);
        await Assert.That(items[1].Title).IsEqualTo("Ok");
    }

    [Test]
    public async Task BuildList_TruncatesAtTheOutputCap()
    {
        var rows = new List<string>();
        for (int i = 0; i < ForgeProjection.MaxPullRequests + 5; i++)
        {
            rows.Add("{\"number\":" + i
                + ",\"title\":\"t" + i
                + "\",\"state\":\"OPEN\",\"url\":\"u" + i + "\"}");
        }

        IReadOnlyList<ForgePullRequest> items = ForgeProjection.BuildList($"[{string.Join(',', rows)}]");

        await Assert.That(items.Count).IsEqualTo(ForgeProjection.MaxPullRequests);
        await Assert.That(items[^1].Number).IsEqualTo(ForgeProjection.MaxPullRequests - 1);
    }

    [Test]
    public async Task BuildList_EmptyMalformedOrWrongShapeReturnsNothing()
    {
        await Assert.That(ForgeProjection.BuildList(null)).IsEmpty();
        await Assert.That(ForgeProjection.BuildList("  ")).IsEmpty();
        await Assert.That(ForgeProjection.BuildList("{not json")).IsEmpty();
        await Assert.That(ForgeProjection.BuildList("{\"number\":1}")).IsEmpty();
    }

    [Test]
    public async Task BuildOne_ProjectsASinglePullRequest()
    {
        const string json = """
            {"number":11,"title":"Single","state":"CLOSED","url":"https://github.com/o/r/pull/11"}
            """;

        ForgePullRequest? item = ForgeProjection.BuildOne(json);

        await Assert.That(item).IsNotNull();
        await Assert.That(item!.Number).IsEqualTo(11);
        await Assert.That(item.Title).IsEqualTo("Single");
        await Assert.That(item.State).IsEqualTo("CLOSED");
    }

    [Test]
    public async Task BuildOne_UnreadableInputReturnsNull()
    {
        await Assert.That(ForgeProjection.BuildOne(null)).IsNull();
        await Assert.That(ForgeProjection.BuildOne("[]")).IsNull();
        await Assert.That(ForgeProjection.BuildOne("{\"number\":")).IsNull();
    }
}
