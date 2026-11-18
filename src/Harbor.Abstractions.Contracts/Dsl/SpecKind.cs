namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Component kinds the markup grammar can name. Most of the set follows
///     the widgets in Harbor.Tui.CellForge/Generic/Widgets plus FilterList,
///     which lives in Harbor.Tui.CellForge.Engine/Widgets; the trailing
///     Row/Column/Overlay entries are layout containers, not widgets.
/// </summary>
/// <remarks>
///     The parser resolves markup names to these values once. Production
///     code generation and the dev interpreter both read the same enum, so
///     the grammar has one vocabulary. This assembly holds only the names;
///     no widget or renderer type is referenced.
/// </remarks>
public enum SpecKind
{
    /// <summary>Generic container.</summary>
    Panel,

    /// <summary>Static text line.</summary>
    Text,

    /// <summary>Filterable list (Harbor.Tui.CellForge.Engine FilterList).</summary>
    FilterList,

    /// <summary>Table with sortable columns (SortableTable).</summary>
    SortableTable,

    /// <summary>Hierarchical tree (GenericTreeView).</summary>
    Tree,

    /// <summary>Scrolling log pane (LogView).</summary>
    LogView,

    /// <summary>Single-line editor (TextInput).</summary>
    TextInput,

    /// <summary>Date selection control (DatePicker).</summary>
    DatePicker,

    /// <summary>Plain table (GenericTable).</summary>
    Table,

    /// <summary>Selectable item list (GenericSelectionList).</summary>
    SelectionList,

    /// <summary>Form text field (GenericTextField).</summary>
    TextField,

    /// <summary>Numeric editor (GenericNumberInput).</summary>
    NumberInput,

    /// <summary>Multi-line editor (MultilineTextInput).</summary>
    MultilineTextInput,

    /// <summary>Expandable sections (Accordion).</summary>
    Accordion,

    /// <summary>Action strip (ActionButton, Toolbar).</summary>
    ActionButton,

    /// <summary>Positioned menu (AnchoredMenu).</summary>
    AnchoredMenu,

    /// <summary>Positioned overlay (AnchoredOverlay).</summary>
    AnchoredOverlay,

    /// <summary>File or message attachment card (AttachmentCard).</summary>
    AttachmentCard,

    /// <summary>Short label (CompactLabel).</summary>
    CompactLabel,

    /// <summary>Copy-mode affordance (CopyMode).</summary>
    CopyMode,

    /// <summary>Document viewport (DocumentView).</summary>
    DocumentView,

    /// <summary>Drop-down button (DropdownButton).</summary>
    Dropdown,

    /// <summary>Focus tooltip (FocusTooltip).</summary>
    FocusTooltip,

    /// <summary>Input form (GenericForm).</summary>
    Form,

    /// <summary>Form layout scaffold (FormScaffold).</summary>
    FormScaffold,

    /// <summary>Live region (GenericLiveRegion).</summary>
    LiveRegion,

    /// <summary>Progress indicator (GenericProgress).</summary>
    Progress,

    /// <summary>Questionnaire field group (Questionnaire).</summary>
    Questionnaire,

    /// <summary>Rating control (Rating).</summary>
    Rating,

    /// <summary>Settings editor (SettingsPanel).</summary>
    SettingsPanel,

    /// <summary>Spinner (GenericProgress spinner).</summary>
    Spinner,

    /// <summary>Stepper (GenericStepper).</summary>
    Stepper,

    /// <summary>Toggle choice (GenericToggleSelect).</summary>
    ToggleSelect,

    /// <summary>Tool bar (Toolbar).</summary>
    Toolbar,

    /// <summary>Virtualized list body (VirtualListDelegate).</summary>
    VirtualList,

    /// <summary>Layout container that splits its children along the horizontal
    /// main axis (maps to FlexSplitNode with SplitDir.Horizontal).</summary>
    Row,

    /// <summary>Layout container that splits its children along the vertical
    /// main axis (maps to FlexSplitNode with SplitDir.Vertical).</summary>
    Column,

    /// <summary>Layout container that stacks every child over the full area
    /// (maps to ZStackNode).</summary>
    Overlay
}
