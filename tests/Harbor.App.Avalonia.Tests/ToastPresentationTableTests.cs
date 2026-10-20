// ToastPresentationTableTests.cs — #1543: the Avalonia toast converters read
// the shared table, they do not carry their own copy of it.
//
// WHAT THIS ASSERTS
// -----------------
// The icon converter and the brush converter used to each hold a switch that
// mapped a toast KIND to a theme resource key. The keys were right, and the
// two switches could still drift apart from each other and from the terminal
// renderer's glyph — nothing tied them together, and a kind added to the enum
// would land in one of them and miss the others.
//
// So the assertion is not "the key is StateSuccessBrush" (the table's own test
// says that). It is "the converter names what the TABLE names" — which is the
// property that fails the day somebody re-introduces a second opinion.

using System.Globalization;
using Avalonia.Media;
using Harbor.App.Avalonia.Tests.Support;
using Harbor.App.Avalonia.Views;
using Harbor.Ui.Framework.Services;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #1543 — the Avalonia toast converters are adapters over the shared
///     toast presentation table.
/// </summary>
public class ToastPresentationTableTests
{
    /// <summary>
    ///     The icon converter names the icon the table names, for every kind.
    /// </summary>
    [Test]
    public async Task The_Icon_Converter_Names_The_Icon_The_Table_Names()
    {
        foreach ((ToastKind kind, ToastPresentation presentation) in ToastPresentations.All)
        {
            await Assert.That(ToastIconConverter.IconResourceKeyFor(kind)).IsEqualTo(presentation.IconResourceKey)
                .Because($"{kind} must paint the icon the shared table names");
        }
    }

    /// <summary>
    ///     A value that is not a kind at all still names an icon — the
    ///     informational one, as it did before the table existed.
    /// </summary>
    [Test]
    public async Task The_Icon_Converter_Falls_Back_To_The_Informational_Icon()
    {
        await Assert.That(ToastIconConverter.IconResourceKeyFor(null))
            .IsEqualTo(ToastPresentations.IconResourceKey(ToastKind.Info))
            .Because("a binding that carries no kind still has to name an icon");
    }

    /// <summary>
    ///     The brush converter resolves the brush the table names, for every
    ///     kind. Resolved against the live theme, so a key the theme does not
    ///     declare fails here too.
    /// </summary>
    [Test]
    [Retry(3)] // Retention(#1087): headless-Avalonia resolve flake (dispatcher/compositor wobble); rerun, not fix.
    public async Task The_Brush_Converter_Resolves_The_Brush_The_Table_Names()
    {
        List<string> mismatches = await AvaloniaSession.RunAsync(() =>
        {
            var wrong = new List<string>();

            foreach ((ToastKind kind, ToastPresentation presentation) in ToastPresentations.All)
            {
                object? resolved = ToastBrushConverter.Instance.Convert(
                    kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
                object? expected = ThemeBrushResolver.Resolve(presentation.BrushResourceKey);

                if (!Equals(resolved, expected))
                {
                    wrong.Add(
                        $"{kind} resolved {resolved?.GetType().Name ?? "null"} while the table names "
                        + $"{presentation.BrushResourceKey}");
                }
            }

            return wrong;
        });

        await Assert.That(mismatches).IsEmpty()
            .Because("the brush a toast wears is the table's row, not a second switch's opinion");
    }
}
