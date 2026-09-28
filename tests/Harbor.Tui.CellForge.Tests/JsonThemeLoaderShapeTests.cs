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
///     crashed the theme at runtime. It is now a static <c>Parse</c> /
///     <c>LoadFile</c> helper: a static class cannot be used as a generic type
///     argument, so the broken registration is a compile error, not a trap.
/// </summary>
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
    public async Task JsonThemeLoader_Exposes_ParseAndLoadFile()
    {
        await Assert.That(typeof(JsonThemeLoader).GetMethod("Parse", [typeof(string)])).IsNotNull();
        await Assert.That(typeof(JsonThemeLoader).GetMethod("Parse", [typeof(string), typeof(HarborTheme)])).IsNotNull();
        await Assert.That(typeof(JsonThemeLoader).GetMethod("LoadFile", [typeof(string)])).IsNotNull();
    }
}
