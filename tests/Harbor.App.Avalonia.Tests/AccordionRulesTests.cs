// AccordionRulesTests.cs — K25: the expansion policy behind the HDS Accordion.
//
// The control and this file call the same pure methods, so these assertions
// pin the behaviour a click produces without standing up an Avalonia app. The
// last three tests drive the control itself through its public Toggle method
// to prove the control actually wires the policy to its rows.

using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views.Components;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

[NotInParallel("avalonia-headless")]
public class AccordionRulesTests
{
    private static HashSet<string> Set(IReadOnlySet<string> set) => new(set, StringComparer.Ordinal);

    [Test]
    public async Task Single_OpeningAnotherSection_LeavesOnlyTheNewOne()
    {
        var expanded = new HashSet<string>(["first"], StringComparer.Ordinal);

        var next = AccordionRules.NextExpanded(expanded, AccordionMode.Single, "second");

        await Assert.That(Set(next).SetEquals(["second"])).IsTrue();
        await Assert.That(Set(expanded).SetEquals(["first"])).IsTrue();
    }

    [Test]
    public async Task Single_TogglingTheOpenSection_ClosesIt()
    {
        var expanded = new HashSet<string>(["first"], StringComparer.Ordinal);

        var next = AccordionRules.NextExpanded(expanded, AccordionMode.Single, "first");

        await Assert.That(next.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Single_TogglingAnUnknownId_ReplacesTheOpenOne()
    {
        var next = AccordionRules.NextExpanded(AccordionRules.Empty(), AccordionMode.Single, "new");
        await Assert.That(Set(next).SetEquals(["new"])).IsTrue();
    }

    [Test]
    public async Task Multiple_TogglesEachSectionIndependently()
    {
        var expanded = new HashSet<string>(["first"], StringComparer.Ordinal);

        var opened = AccordionRules.NextExpanded(expanded, AccordionMode.Multiple, "second");
        await Assert.That(Set(opened).SetEquals(["first", "second"])).IsTrue();

        var closed = AccordionRules.NextExpanded(opened, AccordionMode.Multiple, "first");
        await Assert.That(Set(closed).SetEquals(["second"])).IsTrue();
        await Assert.That(Set(expanded).SetEquals(["first"])).IsTrue();
    }

    [Test]
    public async Task DisabledOrEmptyId_LeavesTheSetUnchanged()
    {
        var expanded = new HashSet<string>(["first"], StringComparer.Ordinal);

        var disabled = AccordionRules.NextExpanded(expanded, AccordionMode.Single, "second", disabled: true);
        var empty = AccordionRules.NextExpanded(expanded, AccordionMode.Multiple, "");

        await Assert.That(Set(disabled).SetEquals(["first"])).IsTrue();
        await Assert.That(Set(empty).SetEquals(["first"])).IsTrue();
        await Assert.That(Set(AccordionRules.NextExpanded(null, AccordionMode.Multiple, "x")).SetEquals(["x"])).IsTrue();
    }

    [Test]
    public async Task Normalize_SingleKeepsFirstNonEmptyId_MultipleKeepsAll()
    {
        var ids = new[] { "", "a", "b", "a" };

        await Assert.That(Set(AccordionRules.Normalize(ids, AccordionMode.Single)).SetEquals(["a"])).IsTrue();
        await Assert.That(Set(AccordionRules.Normalize(ids, AccordionMode.Multiple)).SetEquals(["a", "b"])).IsTrue();
        await Assert.That(AccordionRules.Normalize(["", ""], AccordionMode.Single).Count).IsEqualTo(0);
    }

    [Test]
    public async Task IsExpanded_HandlesNullAndEmptyIds()
    {
        var expanded = new HashSet<string>(["a"], StringComparer.Ordinal);
        await Assert.That(AccordionRules.IsExpanded(expanded, "a")).IsTrue();
        await Assert.That(AccordionRules.IsExpanded(expanded, "b")).IsFalse();
        await Assert.That(AccordionRules.IsExpanded(null, "a")).IsFalse();
        await Assert.That(AccordionRules.IsExpanded(expanded, "")).IsFalse();
    }

    [Test]
    public async Task Control_Defaults_AreSingleAndNotBordered()
    {
        var accordion = new Accordion();
        await Assert.That(accordion.Mode).IsEqualTo(AccordionMode.Single);
        await Assert.That(accordion.IsBordered).IsFalse();
        await Assert.That(accordion.IsDisabled).IsFalse();
        await Assert.That(accordion.Sections.Count).IsEqualTo(0);
        await Assert.That(accordion.ExpandedIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Control_SingleMode_ClosesThePreviousSection()
    {
        var accordion = new Accordion
        {
            Sections =
            [
                new AccordionSection("a", "A", "body A"),
                new AccordionSection("b", "B", "body B"),
            ],
        };

        accordion.Toggle("a");
        accordion.Toggle("b");

        await Assert.That(Set(accordion.ExpandedIds).SetEquals(["b"])).IsTrue();
    }

    [Test]
    public async Task Control_MultipleMode_AndDisabledSection_AreHonoured()
    {
        var accordion = new Accordion
        {
            Mode = AccordionMode.Multiple,
            Sections =
            [
                new AccordionSection("a", "A", "body A"),
                new AccordionSection("b", "B", "body B"),
                new AccordionSection("c", "C", "body C", IsDisabled: true),
            ],
        };

        accordion.Toggle("a");
        accordion.Toggle("b");
        accordion.Toggle("c");

        await Assert.That(Set(accordion.ExpandedIds).SetEquals(["a", "b"])).IsTrue();
    }
}
