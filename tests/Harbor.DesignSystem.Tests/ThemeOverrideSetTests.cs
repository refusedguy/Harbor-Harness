using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

public class ThemeOverrideSetTests
{
    private static RgbColor R(byte v) => new(v, v, v);

    [Test]
    public async Task Merge_EmptyPatch_KeepsBase()
    {
        var merged = PartialTheme.None.Merge(HarborTheme.HarborDark);
        await Assert.That(merged.Accent).IsEqualTo(HarborTheme.HarborDark.Accent);
        await Assert.That(merged.Text).IsEqualTo(HarborTheme.HarborDark.Text);
    }

    [Test]
    public async Task Merge_PatchedSlots_Override_OthersInherit()
    {
        var patch = new PartialTheme(Accent: R(0x11), Border: R(0x22));
        var merged = patch.Merge(HarborTheme.HarborDark);

        await Assert.That(merged.Accent).IsEqualTo(R(0x11));
        await Assert.That(merged.Border).IsEqualTo(R(0x22));
        await Assert.That(merged.Text).IsEqualTo(HarborTheme.HarborDark.Text);
        await Assert.That(merged.Surface).IsEqualTo(HarborTheme.HarborDark.Surface);
    }

    [Test]
    public async Task With_InstallsScope_CaseInsensitive()
    {
        var set = new ThemeOverrideSet()
            .With("Sidebar", new PartialTheme(Panel: R(0x33)));

        await Assert.That(set.Has("sidebar")).IsTrue();
        await Assert.That(set.Has("SIDEBAR")).IsTrue();
        await Assert.That(set.PatchFor("sidebar")).IsNotNull();
        await Assert.That(set.PatchFor("composer")).IsNull();
    }

    [Test]
    public async Task Merge_UnknownScope_ReturnsBaseInstance()
    {
        var set = new ThemeOverrideSet().With("composer", new PartialTheme(Accent: R(0x44)));

        var effective = set.Merge("status", HarborTheme.HarborDark);
        await Assert.That(ReferenceEquals(effective, HarborTheme.HarborDark)).IsTrue();
    }

    [Test]
    public async Task Merge_KnownScope_ReturnsPatchedTheme()
    {
        var set = new ThemeOverrideSet().With("status", new PartialTheme(Accent: R(0x55)));

        var effective = set.Merge("status", HarborTheme.HarborDark);
        await Assert.That(effective.Accent).IsEqualTo(R(0x55));
        await Assert.That(effective.Text).IsEqualTo(HarborTheme.HarborDark.Text);
    }

    [Test]
    public async Task With_IsImmutable_OriginalUnchanged()
    {
        var original = new ThemeOverrideSet();
        var updated = original.With("sidebar", new PartialTheme(Accent: R(0x66)));

        await Assert.That(original.Has("sidebar")).IsFalse();
        await Assert.That(updated.Has("sidebar")).IsTrue();
        await Assert.That(updated.Scopes.Count()).IsEqualTo(1);
    }
}
