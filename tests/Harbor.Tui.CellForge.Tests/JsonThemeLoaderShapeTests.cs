using System.Reflection;
using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #469 regression guard. <c>JsonThemeLoader</c> used to declare
///     <c>: IThemeService</c> while throwing <c>NotImplementedException</c> from
///     every apply member — so a
///     <c>services.AddSingleton&lt;IThemeService, JsonThemeLoader&gt;()</c>
///     crashed the theme at runtime. It is a static <c>Parse</c> helper: a static
///     class cannot be used as a generic type argument, so the broken
///     registration is a compile error, not a trap.
/// </summary>
/// <remarks>
///     The second test records the #668 narrowing: the class also used to carry a
///     static <c>LoadFile(string)</c> that read the disk, which made it a second
///     implementation of <c>IThemeStore</c>. Reading a file is not what this type
///     is for, so that member is gone and its absence is asserted.
/// </remarks>
public class JsonThemeLoaderShapeTests
{
    [Test]
    public async Task JsonThemeLoader_IsStatic_SoItCannotBeRegistered()
    {
        // abstract + sealed == static class.
        await Assert.That(typeof(JsonThemeLoader).IsAbstract).IsTrue();
        await Assert.That(typeof(JsonThemeLoader).IsSealed).IsTrue();
    }

    [Test]
    public async Task JsonThemeLoader_Implements_NoThemeRole()
    {
        await Assert.That(typeof(IThemeService).IsAssignableFrom(typeof(JsonThemeLoader))).IsFalse();
        await Assert.That(typeof(IThemeReader).IsAssignableFrom(typeof(JsonThemeLoader))).IsFalse();
        await Assert.That(typeof(IThemeApplier).IsAssignableFrom(typeof(JsonThemeLoader))).IsFalse();
        await Assert.That(typeof(IThemeWatcher).IsAssignableFrom(typeof(JsonThemeLoader))).IsFalse();
    }

    [Test]
    public async Task JsonThemeLoader_HasNoInstanceState()
    {
        MemberInfo[] members = typeof(JsonThemeLoader).GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        await Assert.That(members.Length).IsEqualTo(0);
    }

    [Test]
    public async Task JsonThemeLoader_Exposes_Parse_And_No_Longer_LoadFile()
    {
        await Assert.That(typeof(JsonThemeLoader).GetMethod("Parse", [typeof(string)])).IsNotNull();
        await Assert.That(typeof(JsonThemeLoader).GetMethod("Parse", [typeof(string), typeof(HarborTheme)])).IsNotNull();

        // #668: the disk half moved behind IThemeStore. A public static
        // LoadFile(string) here is the second implementation of what ThemeStore
        // already does, reachable from Presentation without naming anything —
        // and its presence is why the two NoFiles baseline rows could not be
        // deleted. Pinned as absent so it cannot quietly come back.
        //
        // ThemeStoreSeamRules.JsonThemeLoader_No_Longer_Reads_The_Disk_Itself
        // is the cross-assembly half; this one keeps the contract next to the
        // type it describes, where the #469 guard already lives.
        await Assert.That(typeof(JsonThemeLoader).GetMethod("LoadFile", [typeof(string)])).IsNull();
    }
}
