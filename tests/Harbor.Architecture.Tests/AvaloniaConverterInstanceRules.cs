// AvaloniaConverterInstanceRules.cs — source-level guard for issue #1539:
// a view that declares its own converter instance allocates one per inflation.
//
// THE DEFECT THIS GUARDS
// ----------------------
// #1539 moved the twenty converters of the Avalonia shell onto
// `OneWayConverterBase<TSelf>`, which holds the singleton. The singleton only
// pays off if the VIEWS use it: eight of them declared a converter of their
// own in a resource dictionary instead, so every inflation of those views
// allocated a converter that nothing else could see:
//
//     <UserControl.Resources>
//         <vw:BrushKeyConverter x:Key="BrushKey" />
//     </UserControl.Resources>
//
// The converter is stateless, so those instances were identical to the shared
// one — pure allocation. For the list-shaped views it is one per row: a chat
// bubble, a tool-call card, a session row, a status badge.
//
// WHY A SOURCE SCAN AND NOT A RUNTIME CHECK
// ----------------------------------------
// The XAML is compiled, and a converter declared in a resource dictionary is
// an ordinary object in the inflated view's `Resources` — but the views that
// declare one are spread over eight files, several of which need a headless
// session and a data context to inflate at all. The source is where the
// declaration is, and `SourceScan.EnumerateXamlFiles` reaches it without a
// reference edge (this project references no `apps/` assembly, by design).
//
// WHAT IS FORBIDDEN, AND WHAT IS NOT
// ----------------------------------
// A converter declared in a resource dictionary is forbidden, with ONE
// exception: `BoolToWidthConverter`, whose expanded/collapsed widths are set
// by the XAML that declares it. A shared singleton would carry one rail's
// widths into every other rail, so that one stays a per-view instance — and
// it is named here, so the exception is a decision on the record rather than
// a gap in the rule.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard, and a
// guard whose PERIMETER silently emptied is worse still. The scan is checked
// against a floor of files AND against the one declaration that IS allowed:
// a rule that cannot see `BoolToWidthConverter` cannot see a new one either.

using System.Xml;
using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #1539): the Avalonia shell's views bind the shared
///     converter singletons, and the one converter that may not be shared is
///     named here with its reason.
/// </summary>
public class AvaloniaConverterInstanceRules
{
    /// <summary>The app project this rule walks.</summary>
    private const string AppProject = "apps/Harbor.App.Avalonia";

    /// <summary>
    ///     The converters a view may declare for itself, with the reason.
    /// </summary>
    private static readonly Dictionary<string, string> DeclaredConverters = new(StringComparer.Ordinal)
    {
        ["BoolToWidthConverter"] =
            "stateful: the rail's expanded/collapsed widths are set by the XAML that declares it, so a "
            + "shared singleton would carry one rail's widths into every other rail",
    };

    [Test]
    public async Task Views_Do_Not_Declare_Their_Own_Converter_Instance()
    {
        (List<(string File, string Converter)> declared, List<string> unreadable) = ScanDeclaredConverters();

        var violations = new List<string>(unreadable);

        foreach ((string file, string converter) in declared)
        {
            if (!DeclaredConverters.ContainsKey(converter))
            {
                violations.Add(
                    $"{file}: declares <{converter}>. A converter declared in a resource dictionary is "
                    + "allocated per inflation of that view, and every converter here except the stateful "
                    + "one has a shared singleton — bind it with {x:Static …Instance} instead.");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#1539). A converter declared in a resource dictionary is a second instance of an "
                + "object that already has one, and for a list-shaped view it is one per row. The shared "
                + "singleton on OneWayConverterBase<TSelf> exists to be used. "
                + string.Join("\n", violations));
    }

    [Test]
    public async Task The_Scan_Finds_The_App_Xaml_And_Its_Converter_Declarations()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The walk needs a repository root; without one this file guards nothing.");

        if (root is null)
        {
            return;
        }

        IReadOnlyList<string> files = SourceScan.EnumerateXamlFiles(AppProject);

        await Assert.That(files.Count).IsGreaterThan(50)
            .Because(
                $"The guarded shell holds well over 50 XAML files; found {files.Count}. A near-zero count "
                + "means the path is stale and the rule enforces nothing.");

        // The one declaration the rule ALLOWS has to be visible to the scan.
        // A rule that cannot see it cannot see a new one either, and the file
        // count above would not notice: the files are still there.
        (List<(string File, string Converter)> declared, _) = ScanDeclaredConverters();

        await Assert.That(declared.Count).IsEqualTo(DeclaredConverters.Count)
            .Because(
                "The app declares exactly one converter of its own — BoolToWidthConverter, whose widths the "
                + "XAML sets. A different count means a declaration appeared or disappeared, and both are "
                + "changes the rule has an opinion about.");

        await Assert.That(string.Join(",", declared.Select(d => d.Converter).OrderBy(n => n, StringComparer.Ordinal)))
            .IsEqualTo(string.Join(",", DeclaredConverters.Keys.OrderBy(n => n, StringComparer.Ordinal)))
            .Because("the only converter a view may declare is the stateful one, and it is declared once.");
    }

    /// <summary>
    ///     The converter instances the app's XAML declares in a resource
    ///     dictionary, and the files the scan could not read.
    /// </summary>
    private static (List<(string File, string Converter)> Declared, List<string> Unreadable) ScanDeclaredConverters()
    {
        var declared = new List<(string File, string Converter)>();
        var unreadable = new List<string>();

        foreach (string file in SourceScan.EnumerateXamlFiles(AppProject))
        {
            string? source = SourceScan.TryReadAllText(file);
            if (source is null)
            {
                unreadable.Add(
                    $"{SourceScan.Relative(file)}: could not be read. A file the scan cannot read is not a "
                    + "file without violations — skipping it would report green over an unpoliced perimeter.");
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(source);
            }
            catch (XmlException ex)
            {
                unreadable.Add($"{SourceScan.Relative(file)}: is not well-formed XML ({ex.Message}).");
                continue;
            }

            foreach (XElement resources in document.Descendants()
                         .Where(e => e.Name.LocalName.EndsWith("Resources", StringComparison.Ordinal)))
            {
                foreach (XElement converter in resources.Elements()
                             .Where(e => e.Name.LocalName.EndsWith("Converter", StringComparison.Ordinal)))
                {
                    declared.Add((SourceScan.Relative(file), converter.Name.LocalName));
                }
            }
        }

        return (declared, unreadable);
    }
}
