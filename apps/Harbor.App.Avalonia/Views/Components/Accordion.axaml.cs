using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Harbor.App.Avalonia.Views.Components;

/// <summary>
///     One section of an <see cref="Accordion" />: a stable id, a header and
///     the text shown while it is expanded. An optional <see cref="IsDisabled"/>
///     makes just that row inert.
/// </summary>
/// <param name="Id">Stable identity used in the expanded set.</param>
/// <param name="Header">Header text painted on the Expander.</param>
/// <param name="Content">Body text painted when the section is open.</param>
/// <param name="IsDisabled">Whether this row refuses toggles.</param>
public readonly record struct AccordionSection(
    string Id,
    string Header,
    string Content,
    bool IsDisabled = false);

/// <summary>
///     Reusable HDS accordion: a stack of <see cref="Expander"/> rows governed
///     by one expansion policy (K25).
/// </summary>
/// <remarks>
///     <para>
///         The control owns the expanded set and is the single source of truth.
///         Each row publishes its <see cref="Expander.IsExpanded"/> change; the
///         control runs it through <see cref="AccordionRules.NextExpanded"/>
///         and writes the resulting set back to every row under a re-entrancy
///         guard. That is what makes <see cref="AccordionMode.Single"/> close
///         the previously open row instead of leaving two rows expanded.
///     </para>
///     <para>
///         Rows are built in code rather than through an ItemsControl template
///         so the bordered/disabled classes can be applied to the live
///         expanders when those styled properties change. No reflection and no
///         dynamic code is involved, which keeps the control AOT-safe.
///     </para>
/// </remarks>
public partial class Accordion : UserControl
{
    /// <summary>The expansion policy. Defaults to <see cref="AccordionMode.Single"/>.</summary>
    public static readonly StyledProperty<AccordionMode> ModeProperty =
        AvaloniaProperty.Register<Accordion, AccordionMode>(
            nameof(Mode),
            defaultValue: AccordionMode.Single);

    /// <summary>The sections, in paint order.</summary>
    public static readonly StyledProperty<IReadOnlyList<AccordionSection>> SectionsProperty =
        AvaloniaProperty.Register<Accordion, IReadOnlyList<AccordionSection>>(
            nameof(Sections),
            defaultValue: Array.Empty<AccordionSection>());

    /// <summary>Whether rows draw the bordered variant.</summary>
    public static readonly StyledProperty<bool> IsBorderedProperty =
        AvaloniaProperty.Register<Accordion, bool>(nameof(IsBordered));

    /// <summary>Whether the whole accordion is disabled.</summary>
    public static readonly StyledProperty<bool> IsDisabledProperty =
        AvaloniaProperty.Register<Accordion, bool>(nameof(IsDisabled));

    private readonly List<Expander> _rows = [];
    private IReadOnlySet<string> _expanded = AccordionRules.Empty();
    private bool _applying;

    /// <summary>Construct the accordion.</summary>
    public Accordion()
    {
        InitializeComponent();
    }

    /// <summary>The expansion policy applied to every toggle.</summary>
    public AccordionMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>The sections, in paint order.</summary>
    public IReadOnlyList<AccordionSection> Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    /// <summary>Whether rows draw the bordered variant.</summary>
    public bool IsBordered
    {
        get => GetValue(IsBorderedProperty);
        set => SetValue(IsBorderedProperty, value);
    }

    /// <summary>Whether the whole accordion is disabled.</summary>
    public bool IsDisabled
    {
        get => GetValue(IsDisabledProperty);
        set => SetValue(IsDisabledProperty, value);
    }

    /// <summary>The ids currently expanded. A fresh snapshot of the control's own set.</summary>
    public IReadOnlySet<string> ExpandedIds => new HashSet<string>(_expanded, StringComparer.Ordinal);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SectionsProperty)
        {
            RebuildRows();
        }
        else if (change.Property == ModeProperty)
        {
            ApplyExpansion(AccordionRules.Normalize(_expanded, Mode));
        }
        else if (change.Property is IsBorderedProperty or IsDisabledProperty)
        {
            ApplyRowState();
        }
    }

    /// <summary>
    ///     Toggle a section programmatically with the same policy a click
    ///     uses. Exposed so a host (and a test) can drive the control without a
    ///     pointer device.
    /// </summary>
    public void Toggle(string id)
    {
        var section = FindSection(id);
        if (section is null) return;
        ApplyExpansion(AccordionRules.NextExpanded(
            _expanded, Mode, id, IsDisabled || section.Value.IsDisabled));
    }

    private void RebuildRows()
    {
        // A host may hand over a set restored under a different policy; make
        // it honour the current one before it drives any row.
        _expanded = AccordionRules.Normalize(_expanded, Mode);

        foreach (Expander row in _rows) row.PropertyChanged -= OnRowPropertyChanged;
        _rows.Clear();
        ItemsHost.Children.Clear();

        IReadOnlyList<AccordionSection> sections = Sections ?? Array.Empty<AccordionSection>();
        for (int i = 0; i < sections.Count; i++)
        {
            AccordionSection section = sections[i];
            var row = new Expander
            {
                Header = section.Header,
                Content = new TextBlock
                {
                    Text = section.Content,
                    TextWrapping = TextWrapping.Wrap,
                },
                IsExpanded = AccordionRules.IsExpanded(_expanded, section.Id),
            };
            row.Classes.Add("hds-accordion-item");
            row.PropertyChanged += OnRowPropertyChanged;
            _rows.Add(row);
            ItemsHost.Children.Add(row);
        }
        ApplyRowState();
    }

    private void OnRowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (_applying
            || change.Property != Expander.IsExpandedProperty
            || sender is not Expander row)
        {
            return;
        }

        int index = _rows.IndexOf(row);
        IReadOnlyList<AccordionSection> sections = Sections ?? Array.Empty<AccordionSection>();
        if (index < 0 || index >= sections.Count) return;

        AccordionSection section = sections[index];
        ApplyExpansion(AccordionRules.NextExpanded(
            _expanded, Mode, section.Id, IsDisabled || section.IsDisabled));
    }

    private void ApplyExpansion(IReadOnlySet<string> next)
    {
        _expanded = next;
        _applying = true;
        try
        {
            IReadOnlyList<AccordionSection> sections = Sections ?? Array.Empty<AccordionSection>();
            for (int i = 0; i < _rows.Count && i < sections.Count; i++)
                _rows[i].IsExpanded = AccordionRules.IsExpanded(next, sections[i].Id);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyRowState()
    {
        IReadOnlyList<AccordionSection> sections = Sections ?? Array.Empty<AccordionSection>();
        for (int i = 0; i < _rows.Count && i < sections.Count; i++)
        {
            bool disabled = IsDisabled || sections[i].IsDisabled;
            _rows[i].IsEnabled = !disabled;
            _rows[i].Classes.Set("hds-accordion-bordered", IsBordered);
            _rows[i].Classes.Set("hds-accordion-disabled", disabled);
        }
    }

    private AccordionSection? FindSection(string id)
    {
        IReadOnlyList<AccordionSection> sections = Sections ?? Array.Empty<AccordionSection>();
        for (int i = 0; i < sections.Count; i++)
        {
            if (string.Equals(sections[i].Id, id, StringComparison.Ordinal))
                return sections[i];
        }
        return null;
    }
}
