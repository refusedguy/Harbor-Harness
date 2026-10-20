// PanelRowsTodoTests.cs: #1575, the todo marker vocabulary is ONE table now
// (PanelRows.TodoMarkers), and it answers two questions at once: the icon a
// marker renders and the summary bucket it counts into.
//
// They used to be two parallel switches over the same string (one switch
// expression for the icon, one switch statement for the counter), so a
// marker added to one and not the other rendered an icon and counted
// nothing, with nothing in the tree to notice. One table cannot drift from
// itself, and this test pins both halves of the answer it gives.
//
// It also pins the fallback: a marker the table does not know renders "?" and
// counts into no bucket, which is what the two switches used to do.

using Harbor.Ui.Framework.Projection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #1575: the todo panel's marker table, icon and count bucket together.
/// </summary>
public class PanelRowsTodoTests
{
    [Test]
    public async Task KnownMarkers_Render_TheirIcon_And_Count_Into_TheirBucket()
    {
        List<TodoItem> todos =
        [
            new TodoItem("[x]", "done one"),
            new TodoItem("[X]", "done two"),
            new TodoItem("[~]", "doing"),
            new TodoItem("[ ]", "pending"),
        ];

        List<string> rows = PanelRows.TodoRows(todos, 60);

        await Assert.That(rows.Any(r => r.Contains("✓  done one"))).IsTrue();
        await Assert.That(rows.Any(r => r.Contains("✓  done two"))).IsTrue();
        await Assert.That(rows.Any(r => r.Contains("→  doing"))).IsTrue();
        await Assert.That(rows.Any(r => r.Contains("○  pending"))).IsTrue();
        await Assert.That(rows[^1]).IsEqualTo("✓ 2  → 1  ○ 1");
    }

    [Test]
    public async Task AnUnknownMarker_Renders_TheFallback_And_Counts_Nowhere()
    {
        List<TodoItem> todos = [new TodoItem("[?]", "mystery")];

        List<string> rows = PanelRows.TodoRows(todos, 60);

        await Assert.That(rows.Any(r => r.Contains("?  mystery"))).IsTrue();
        await Assert.That(rows[^1]).IsEqualTo("✓ 0  → 0  ○ 0");
    }
}
