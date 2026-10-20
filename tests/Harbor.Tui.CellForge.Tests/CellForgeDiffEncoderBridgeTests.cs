using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

// #436: the two cell vocabularies have the same simple names, so every use
// in this file is qualified: EngineCells is the engine's copy of the
// vocabulary, UIR is the shared rendering layer's. The bare names the test
// project pins globally (Cell, ScreenBuffer, CellStyle) are the shared
// layer's.
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     The bridge between the CellForge engine's front buffer and the
///     portable cell-diff protocol (issue #1519): the diff-directive table,
///     the cell-for-cell crossing, and the drift check that the old numeric
///     cast could not make.
/// </summary>
/// <remarks>
///     The bridge used to transfer the per-cell diff directive by numeric
///     cast, <c>(UIR.CellDiffOption)(byte)</c>, which is a claim that the two
///     enums declare the same members in the same order. They are separate
///     types in separate assemblies (the engine is a leaf and cannot borrow,
///     #436), so the claim was unchecked: a member added to one of them
///     crossed as a DIFFERENT member of the other, silently. The table is
///     explicit now, and the checks below are what keep it honest.
/// </remarks>
public class CellForgeDiffEncoderBridgeTests
{
    /// <summary>
    ///     The diff-directive table. The rows ARE the table, and the guard
    ///     below proves the table names every member of the engine's enum.
    /// </summary>
    public static (string Name, EngineCells.CellDiffOption Engine, UIR.CellDiffOption Expected)[] DiffOptionCases =>
    [
        ("None", EngineCells.CellDiffOption.None, UIR.CellDiffOption.None),
        ("Skip", EngineCells.CellDiffOption.Skip, UIR.CellDiffOption.Skip),
        ("AlwaysUpdate", EngineCells.CellDiffOption.AlwaysUpdate, UIR.CellDiffOption.AlwaysUpdate),
        ("ForcedWidth", EngineCells.CellDiffOption.ForcedWidth, UIR.CellDiffOption.ForcedWidth),
    ];

    /// <summary>
    ///     Every declared member crosses as itself.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(DiffOptionCases))]
    public async Task The_Diff_Directive_Crosses_Member_By_Member(
        string name,
        EngineCells.CellDiffOption engine,
        UIR.CellDiffOption expected)
    {
        await Assert.That(EngineFrameSource.MapDiffOption(engine)).IsEqualTo(expected)
            .Because($"{name} must cross the bridge as itself, not as whatever shares its ordinal");
    }

    /// <summary>
    ///     The totality check: a member added to the engine's enum has no row
    ///     in the table until it gets one.
    /// </summary>
    [Test]
    public async Task Every_Engine_Diff_Directive_Has_A_Row_In_The_Table()
    {
        await Assert.That(DiffOptionCases.Length).IsGreaterThan(0)
            .Because("the table has rows to check");

        Exhaustive.AssertAllCases<EngineCells.CellDiffOption>(
            DiffOptionCases.Select(c => c.Engine).ToHashSet(),
            "every engine diff directive needs a row in the bridge's table: a member added to "
            + "the engine's enum has no Rendering counterpart until somebody says which one it is");
    }

    /// <summary>
    ///     The drift check the numeric cast could not make: the two
    ///     vocabularies are separate types in separate assemblies, so a
    ///     member added to one of them is invisible to the other unless
    ///     something compares them.
    /// </summary>
    [Test]
    public async Task The_Two_Vocabularies_Declare_The_Same_Members()
    {
        await Assert.That(Enum.GetNames<UIR.CellDiffOption>())
            .IsEquivalentTo(Enum.GetNames<EngineCells.CellDiffOption>())
            .Because(
                "the bridge translates member by member, so the two vocabularies must declare the "
                + "same members. A member on one side only is a silent mistranslation, and the "
                + "numeric cast the table replaced would have carried it unnoticed.");
    }

    /// <summary>
    ///     The two vocabularies keep the same ordinals. The engine's file is
    ///     the source of truth and the shared one is a verbatim copy of it
    ///     (#1621), so the ordinals agree; the bridge no longer depends on
    ///     that, and this check says so out loud.
    /// </summary>
    [Test]
    public async Task The_Two_Vocabularies_Agree_On_Every_Members_Ordinal()
    {
        foreach (EngineCells.CellDiffOption engine in Enum.GetValues<EngineCells.CellDiffOption>())
        {
            UIR.CellDiffOption rendering = EngineFrameSource.MapDiffOption(engine);

            await Assert.That((byte)rendering).IsEqualTo((byte)engine)
                .Because($"{engine} keeps its ordinal across the bridge");
        }
    }

    /// <summary>
    ///     A value outside the enum is a programming error, not a directive
    ///     to carry across: the table's discard arm throws.
    /// </summary>
    [Test]
    public async Task An_Undeclared_Directive_Is_Rejected_Rather_Than_Invented()
    {
        await Assert.That(() => EngineFrameSource.MapDiffOption((EngineCells.CellDiffOption)7))
            .Throws<ArgumentOutOfRangeException>()
            .Because(
                "a value outside the enum has no counterpart to cross as. The numeric cast the "
                + "table replaced invented one silently, which is how a mistranslation shipped.");
    }

    /// <summary>
    ///     The cell crosses field by field: content, packed colours,
    ///     attributes and width, and the invisible tail of a wide glyph.
    /// </summary>
    [Test]
    public async Task The_Frame_Source_Answers_In_The_Rendering_Vocabulary()
    {
        var style = new EngineCells.CellStyle(
            EngineCells.PackedColor.Indexed(4),
            EngineCells.PackedColor.Indexed(200),
            EngineCells.StyleAttr.Bold);

        var frame = new EngineCells.ScreenBuffer(6, 2);
        frame.SetText(0, 0, "hello", EngineCells.CellStyle.Plain);
        frame.SetRune(0, 1, new Rune('世'), style);

        var source = new EngineFrameSource(frame);

        await Assert.That(source.Cols).IsEqualTo(6);
        await Assert.That(source.Rows).IsEqualTo(2);

        FrameCell plain = source.Get(0, 0);
        await Assert.That(plain.Cell).IsEqualTo(UIR.Cell.FromRaw('h', 0, 0, 0, UIR.Cell.Narrow))
            .Because("a plain cell crosses as the same five fields it was written with");
        await Assert.That(plain.Cell.Rune).IsEqualTo('h');
        await Assert.That(plain.Cell.Width).IsEqualTo(UIR.Cell.Narrow);

        // The styled wide cell: the packed colours (indexed 4 -> 5, indexed
        // 200 -> 201) and the attribute flag cross unchanged, and the width
        // says the glyph takes two columns.
        FrameCell wide = source.Get(0, 1);
        await Assert.That(wide.Cell).IsEqualTo(UIR.Cell.FromRaw('世', 5, 201, 1, UIR.Cell.Wide))
            .Because("colour, attributes and width cross unchanged");
        await Assert.That(wide.Cell.Fg).IsEqualTo(5u);
        await Assert.That(wide.Cell.Bg).IsEqualTo(201u);
        await Assert.That(wide.Cell.Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
        await Assert.That(wide.Cell.Width).IsEqualTo(UIR.Cell.Wide);

        // The wide glyph's invisible tail crosses as the tail.
        FrameCell tail = source.Get(1, 1);
        await Assert.That(tail.Cell.Width).IsEqualTo(UIR.Cell.WSkip);
        await Assert.That(tail.Cell.Rune).IsEqualTo(0);
    }

    /// <summary>
    ///     The diff directive crosses with the cell: the source reports the
    ///     engine's directive, and the forced advance rides beside it.
    /// </summary>
    [Test]
    public async Task The_Diff_Directive_Crosses_With_The_Cell()
    {
        var frame = new EngineCells.ScreenBuffer(4, 1);
        frame.SetText(0, 0, "abcd", EngineCells.CellStyle.Plain);
        frame.SetDiffOption(0, 0, EngineCells.CellDiffOption.Skip);
        frame.SetDiffOption(1, 0, EngineCells.CellDiffOption.AlwaysUpdate);
        frame.SetDiffOption(2, 0, EngineCells.CellDiffOption.ForcedWidth, 3);

        var source = new EngineFrameSource(frame);

        await Assert.That(source.Get(0, 0).DiffOption).IsEqualTo(UIR.CellDiffOption.Skip);
        await Assert.That(source.Get(1, 0).DiffOption).IsEqualTo(UIR.CellDiffOption.AlwaysUpdate);
        await Assert.That(source.Get(2, 0).DiffOption).IsEqualTo(UIR.CellDiffOption.ForcedWidth);
        await Assert.That(source.Get(3, 0).DiffOption).IsEqualTo(UIR.CellDiffOption.None);

        // The forced advance crosses beside the directive, and only there.
        await Assert.That(source.Get(2, 0).ForcedWidth).IsEqualTo((ushort)3);
        await Assert.That(source.Get(0, 0).ForcedWidth).IsEqualTo((ushort)0);
    }

    /// <summary>
    ///     The encoded diff is unchanged: the engine-linked path and the
    ///     portable path agree change for change, byte for byte.
    /// </summary>
    [Test]
    public async Task The_Engine_Linked_Batch_Equals_The_Portable_Batch()
    {
        var front = new EngineCells.ScreenBuffer(8, 3);
        for (int y = 0; y < 3; y++)
        {
            front.SetText(0, y, "bbbbbbbb", EngineCells.CellStyle.Plain);
        }

        var engine = new EngineCells.DiffEngine(front);

        var next = new UIR.ScreenBuffer(8, 3);
        for (int y = 0; y < 3; y++)
        {
            next.SetText(0, y, "bbbbbbbb", UIR.CellStyle.Plain);
        }

        next.SetRune(0, 0, new Rune('Z'), UIR.CellStyle.Plain);
        next.SetRune(3, 2, new Rune('世'), UIR.CellStyle.Plain);

        // The same frame in the Rendering vocabulary: what the portable
        // encoder diffs when the caller holds both frames itself.
        var portableFront = new UIR.ScreenBuffer(8, 3);
        for (int y = 0; y < 3; y++)
        {
            portableFront.SetText(0, y, "bbbbbbbb", UIR.CellStyle.Plain);
        }

        CellDiffBatch viaBridge = CellForgeDiffEncoder.EncodeFromEngineFront(engine, next, hints: null, sequence: 7);
        CellDiffBatch viaPortable = new RowHashDiffEncoder().Encode(portableFront, next, hints: null, sequence: 7);

        await Assert.That(viaBridge.Changes.Length).IsEqualTo(viaPortable.Changes.Length)
            .Because("the crossing must not change what the diff says");
        await Assert.That(viaBridge.Changes.Length).IsGreaterThan(0)
            .Because("the frame really changed, so the batch is not vacuously equal");

        for (int i = 0; i < viaPortable.Changes.Length; i++)
        {
            await Assert.That(viaBridge.Changes[i]).IsEqualTo(viaPortable.Changes[i])
                .Because($"change {i} must cross unchanged");
        }
    }
}
