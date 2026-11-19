// SettingsViewAccordionTests.cs — #3929: the settings screen's section rows.
//
// SettingsView no longer lays out ten StackPanels by hand. It describes the
// sections in SettingsSections, filters that list by the search text and
// hands the result to the HDS Accordion. These assertions drive the inflated
// view on a headless UI thread and read the rows back, so they pin the row
// count, the expansion state and the search filtering the old markup gave us.

using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Tests.Support;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Components;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

[NotInParallel("avalonia-headless")]
public class SettingsViewAccordionTests
{
    [Test]
    public async Task SettingsView_Accordion_RowsExpansionAndSearchMatchTheSectionData()
    {
        var actual = await AvaloniaSession.RunAsync(() =>
        {
            var vm = new SearchOnlyVm();
            var view = new SettingsView { DataContext = vm };
            Accordion accordion = view.GetLogicalDescendants().OfType<Accordion>().Single();

            string[] headers = Rows(accordion).Select(r => r.Header?.ToString() ?? string.Empty).ToArray();
            bool[] expanded = Rows(accordion).Select(r => r.IsExpanded).ToArray();
            string[] names = Rows(accordion)
                .Select(r => AutomationProperties.GetName(r) ?? string.Empty).ToArray();

            vm.SearchQuery = "theme";
            string[] themeHeaders = Rows(accordion).Select(r => r.Header?.ToString() ?? string.Empty).ToArray();
            bool[] themeExpanded = Rows(accordion).Select(r => r.IsExpanded).ToArray();
            bool themeInputPresent = view.GetLogicalDescendants().OfType<ComboBox>()
                .Any(c => AutomationProperties.GetName(c) == "Theme");

            vm.SearchQuery = "no-such-setting-xyz";
            int noneCount = Rows(accordion).Length;
            bool scrollVisible = view.GetLogicalDescendants().OfType<ScrollViewer>().First().IsVisible;
            bool emptyVisible = view.GetLogicalDescendants().OfType<StackPanel>()
                .First(p => AutomationProperties.GetName(p) == "Settings empty state").IsVisible;
            bool noResults = !vm.HasSearchResults;

            vm.SearchQuery = string.Empty;
            string[] restored = Rows(accordion).Select(r => r.Header?.ToString() ?? string.Empty).ToArray();

            return (headers, expanded, names, themeHeaders, themeExpanded, themeInputPresent,
                noneCount, scrollVisible, emptyVisible, noResults, restored);
        });

        string[] expectedHeaders = SettingsSections.All.Select(s => s.Header).ToArray();
        await Assert.That(actual.headers).IsEquivalentTo(expectedHeaders);
        await Assert.That(actual.expanded.All(e => e)).IsTrue()
            .Because("every section was visible before the conversion, so every row opens by default.");
        await Assert.That(actual.names).IsEquivalentTo(expectedHeaders);
        await Assert.That(actual.themeHeaders).IsEquivalentTo(new[] { "Theme" });
        await Assert.That(actual.themeExpanded).IsEquivalentTo(new[] { true });
        await Assert.That(actual.themeInputPresent).IsTrue()
            .Because("the search must filter the section list, not remove the editable controls.");
        await Assert.That(actual.noneCount).IsEqualTo(0);
        await Assert.That(actual.scrollVisible).IsFalse();
        await Assert.That(actual.emptyVisible).IsTrue();
        await Assert.That(actual.noResults).IsTrue();
        await Assert.That(actual.restored).IsEquivalentTo(expectedHeaders);
    }

    [Test]
    public async Task SettingsView_TogglingARow_OnlyClosesThatRow()
    {
        var actual = await AvaloniaSession.RunAsync(() =>
        {
            var view = new SettingsView { DataContext = new SearchOnlyVm() };
            Accordion accordion = view.GetLogicalDescendants().OfType<Accordion>().Single();

            accordion.Toggle("theme");

            return (accordion.ExpandedIds.Contains("theme"),
                accordion.ExpandedIds.Contains("default-provider"),
                Rows(accordion).First(r => AutomationProperties.GetName(r) == "Theme").IsExpanded);
        });

        await Assert.That(actual.Item1).IsFalse();
        await Assert.That(actual.Item2).IsTrue();
        await Assert.That(actual.Item3).IsFalse();
    }

    private static Expander[] Rows(Accordion accordion)
    {
        Expander[] rows = accordion.GetLogicalDescendants().OfType<Expander>()
            .Where(e => e.Classes.Contains("hds-accordion-item")).ToArray();
        return rows.Length > 0
            ? rows
            : accordion.GetVisualDescendants().OfType<Expander>()
                .Where(e => e.Classes.Contains("hds-accordion-item")).ToArray();
    }

    /// <summary>
    ///     Just the search surface the view reads. The remaining bindings in
    ///     SettingsView.axaml resolve against a missing property harmlessly —
    ///     this test owns the accordion behaviour, not the config VM.
    /// </summary>
    private sealed class SearchOnlyVm : INotifyPropertyChanged, ISettingsSearchContext
    {
        private string _searchQuery = string.Empty;

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (string.Equals(_searchQuery, value, StringComparison.Ordinal)) return;
                _searchQuery = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchQuery)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSearchResults)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFiltering)));
            }
        }

        public bool HasSearchResults => SettingsSearch.HasMatches(SearchQuery);

        public bool IsFiltering => !string.IsNullOrWhiteSpace(SearchQuery);

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
