using Harbor.Tui.CellForge.Rendering;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2494: the pure bracket-pair search. Covers nesting across all three
/// bracket kinds, unmatched and mismatched cases, and matches that cross lines.
/// No screen buffer is involved here; DocumentViewTests covers the painting.
/// </summary>
public class BracketPairTests
{
    private static async Task At((int Row, int Col)? actual, int row, int col)
    {
        await Assert.That(actual.HasValue).IsTrue();
        await Assert.That(actual.Value.Row).IsEqualTo(row);
        await Assert.That(actual.Value.Col).IsEqualTo(col);
    }

    private static async Task Absent((int Row, int Col)? actual) =>
        await Assert.That(actual.HasValue).IsFalse();

    [Test]
    public async Task Find_Nested_Round_Brackets_Returns_Inner_And_Outer_Pairs()
    {
        string[] lines = ["((x))"];

        var outer = BracketPair.Find(lines, 0, 0);
        await Assert.That(outer.IsMatch).IsTrue();
        await At(outer.Open, 0, 0);
        await At(outer.Close, 0, 3);

        var inner = BracketPair.Find(lines, 0, 1);
        await Assert.That(inner.IsMatch).IsTrue();
        await At(inner.Open, 0, 1);
        await At(inner.Close, 0, 2);
    }

    [Test]
    public async Task Find_Mixed_Brackets_Match_By_Type()
    {
        string[] lines = ["([{}])"];

        await At(BracketPair.Find(lines, 0, 0).Close, 0, 5);
        await At(BracketPair.Find(lines, 0, 1).Close, 0, 4);
        await At(BracketPair.Find(lines, 0, 2).Close, 0, 3);

        // The symmetric path: a closing bracket resolves backwards.
        var close = BracketPair.Find(lines, 0, 5);
        await Assert.That(close.IsMatch).IsTrue();
        await At(close.Open, 0, 0);
        await At(close.Close, 0, 5);
    }

    [Test]
    public async Task Find_Unpaired_Bracket_Leaves_Missing_Side_Null()
    {
        var open = BracketPair.Find(["(x"], 0, 0);
        await Assert.That(open.IsMatch).IsFalse();
        await At(open.Open, 0, 0);
        await Absent(open.Close);

        var close = BracketPair.Find([")x"], 0, 0);
        await Assert.That(close.IsMatch).IsFalse();
        await Absent(close.Open);
        await At(close.Close, 0, 0);
    }

    [Test]
    public async Task Find_Mismatched_Nesting_Does_Not_Match_The_Outer_Bracket()
    {
        var result = BracketPair.Find(["([)]"], 0, 0);
        await Assert.That(result.IsMatch).IsFalse();
        await At(result.Open, 0, 0);
        await Absent(result.Close);
    }

    [Test]
    public async Task Find_Spans_Multiple_Lines()
    {
        string[] lines = ["(one", " two)"];

        await At(BracketPair.Find(lines, 0, 0).Close, 1, 4);
        var close = BracketPair.Find(lines, 1, 4);
        await Assert.That(close.IsMatch).IsTrue();
        await At(close.Open, 0, 0);

        string[] nested = ["{[", "]}"];
        await At(BracketPair.Find(nested, 0, 0).Close, 1, 1);
        await At(BracketPair.Find(nested, 0, 1).Close, 1, 0);
    }

    [Test]
    public async Task Find_NonBracket_And_OutOfRange_Return_None()
    {
        await Assert.That(BracketPair.Find(["abc"], 0, 1)).IsEqualTo(BracketPairResult.None);
        await Assert.That(BracketPair.Find(["abc"], -1, 0)).IsEqualTo(BracketPairResult.None);
        await Assert.That(BracketPair.Find(["abc"], 0, 3)).IsEqualTo(BracketPairResult.None);
        await Assert.That(BracketPair.Find([], 0, 0)).IsEqualTo(BracketPairResult.None);
    }
}
