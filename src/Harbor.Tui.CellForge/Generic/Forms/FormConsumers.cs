using System.Collections.Generic;
using System.Text;

namespace Harbor.Tui.CellForge.Generic.Forms;

/// <summary>
///     Reference consumer in the preferences area. It assembles its rows from
///     the shared primitives and keeps no scenario-specific validation code.
/// </summary>
/// <remarks>
///     The default descriptors show a typical preferences shape: a required
///     label and a bounded value. A host may pass its own descriptors instead.
/// </remarks>
public sealed class OptionsFormHost
{
    /// <summary>Creates a host over the default descriptor set.</summary>
    public OptionsFormHost()
        : this(Default())
    {
    }

    /// <summary>Creates a host over supplied descriptors.</summary>
    /// <param name="descriptors">Rows in paint order.</param>
    public OptionsFormHost(IEnumerable<FieldDescriptor> descriptors)
    {
        Form = new DeclarativeForm(descriptors);
    }

    /// <summary>The form built from the shared primitives.</summary>
    public DeclarativeForm Form { get; }

    /// <summary>True when the declared rules accept the current values.</summary>
    /// <returns>Validation result.</returns>
    public bool TryApply() => Form.IsValid();

    /// <summary>Current values for the host to store.</summary>
    /// <returns>Value snapshot keyed by field id.</returns>
    public IReadOnlyDictionary<string, string> Snapshot() => Form.Values;

    private static IReadOnlyList<FieldDescriptor> Default() => new[]
    {
        new FieldDescriptor(
            "display-name",
            "Display name",
            validators: new FieldValidator[]
            {
                FieldValidators.Required("A name is needed."),
                FieldValidators.MinLength(2),
            }),
        new FieldDescriptor(
            "depth",
            "Depth",
            maxLength: 3,
            validators: new FieldValidator[] { FieldValidators.MaxLength(3) }),
    };
}

/// <summary>
///     Reference consumer in the lookup area. It builds a different row shape
///     from the very same primitives as <see cref="OptionsFormHost"/>.
/// </summary>
/// <remarks>
///     The default descriptors show a typical lookup shape: a patterned term
///     and a second row checked against the first by the cross-field rule.
/// </remarks>
public sealed class QueryFormHost
{
    /// <summary>Creates a host over the default descriptor set.</summary>
    public QueryFormHost()
        : this(Default())
    {
    }

    /// <summary>Creates a host over supplied descriptors.</summary>
    /// <param name="descriptors">Rows in paint order.</param>
    public QueryFormHost(IEnumerable<FieldDescriptor> descriptors)
    {
        Form = new DeclarativeForm(descriptors);
    }

    /// <summary>The form built from the shared primitives.</summary>
    public DeclarativeForm Form { get; }

    /// <summary>True when the declared rules accept the current values.</summary>
    /// <returns>Validation result.</returns>
    public bool TryRun() => Form.IsValid();

    /// <summary>Current values for the host to dispatch.</summary>
    /// <returns>Value snapshot keyed by field id.</returns>
    public IReadOnlyDictionary<string, string> Snapshot() => Form.Values;

    private static IReadOnlyList<FieldDescriptor> Default() => new[]
    {
        new FieldDescriptor(
            "term",
            "Term",
            placeholder: "letters and digits",
            validators: new FieldValidator[]
            {
                FieldValidators.Required("A term is needed."),
                FieldValidators.Matches("^[a-z0-9]+$"),
            }),
        new FieldDescriptor(
            "repeat",
            "Repeat",
            kind: FieldKind.Masked,
            mask: new Rune('*'),
            maxLength: 24,
            validators: new FieldValidator[] { FieldValidators.MatchesField("term") }),
    };
}
