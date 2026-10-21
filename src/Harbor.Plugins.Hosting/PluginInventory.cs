// PluginInventory.cs — a machine-readable snapshot of the LIVE plugin
// composition (issue #1722).
//
// WHAT THIS IS
// ------------
// Until now the answer to "why is my tool not there?" lived in the host log:
// one line for a plugin that loaded, one for a compilation that failed, one
// for a registration that was refused. Three questions, three log lines, and
// a reader who has to know which line means what.
//
// This file is the projection that answers them at once, in one document a
// program can read: which plugins the host loaded, what phase each one is in,
// which extension axes each one carries, which version it is, and what it
// actually registered in the host.
//
// IT IS A PROJECTION, NOT A SECOND LIFECYCLE
// ------------------------------------------
// The inventory has no state of its own. It reads the state the host already
// keeps — the loaded set, the pending set, and what the registrar's doors
// answered — and renders it. There is no second truth about a plugin's
// phase, and no way for the inventory to disagree with the host about it,
// because the host is what fills it in.
//
// THE DECLARED HALF IS READ, NEVER COPIED
// ----------------------------------------
// The axes and the per-route verdicts in the snapshot are read out of the two
// registries the product already declares them in:
//
//   * `PluginAxisRegistry` — which axes exist (marker/door pairs);
//   * `PluginRouteAxisRegistry` — what each route does with each axis, and
//     why (#1723).
//
// They are not written here. A copy in this file would be a third list of the
// axes, and the third list is the one that drifts.
//
// THE FORMAT IS ONE FORMAT WITH TWO PRODUCERS
// --------------------------------------------
// The JSON this file writes is the format the committed tree snapshot
// (`docs/PLUGIN_INVENTORY.json`) uses too, and the one the generator
// (`tools/gen-plugin-inventory.py`) writes. The runtime snapshot answers
// "what is loaded NOW"; the tree snapshot answers "what does the repository
// DECLARE". Same schema, two producers — so a consumer reads one shape.

using System.Text;
using System.Text.Json;
using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     Where a plugin is in the host's lifecycle, as the inventory sees it.
/// </summary>
public enum PluginInventoryPhase
{
    /// <summary>
    ///     The plugin is loaded and known to the host, but it has NOT been
    ///     activated: it is waiting for a declared dependency (#1733). It
    ///     contributes nothing yet.
    /// </summary>
    Pending,

    /// <summary>
    ///     The plugin is initialized and its contributions are registered in
    ///     the host.
    /// </summary>
    Active,

    /// <summary>
    ///     The host refused the plugin: it did not compile, it did not
    ///     instantiate, its manifest was unreadable, it asked for a newer
    ///     Harbor than the running one, or the host refused its registration.
    ///     The reason is in <see cref="PluginInventoryEntry.FailureReason" />.
    /// </summary>
    Failed,

    /// <summary>
    ///     The plugin WAS running and has been stopped (#1737): what it
    ///     registered was taken back and the plugin was shut down. Its
    ///     definition is kept, so the plugin can be run again.
    /// </summary>
    Stopped,
}

/// <summary>
///     What one door did with one contribution.
/// </summary>
public enum PluginInventoryOutcome
{
    /// <summary>The contribution was registered and is observable in the host.</summary>
    Registered,

    /// <summary>The contribution was refused, and the reason says why.</summary>
    Refused,
}

/// <summary>
///     One door, one contribution, one answer.
/// </summary>
/// <param name="Door">
///     The door's name, as declared on <c>IPluginLoadHost</c>.
/// </param>
/// <param name="Subject">
///     What was being registered — the tool name, the provider id, the agent
///     name.
/// </param>
/// <param name="Outcome">Whether the contribution landed or was refused.</param>
/// <param name="Reason">
///     Why it was refused, or <c>null</c> when it landed.
/// </param>
public sealed record PluginInventoryRegistration(
    string Door,
    string Subject,
    PluginInventoryOutcome Outcome,
    string? Reason);

/// <summary>
///     One plugin, as the host sees it right now.
/// </summary>
/// <param name="Name">
///     The plugin's stable id, or the empty string when the plugin never got
///     far enough to report one (a compilation failure, say).
/// </param>
/// <param name="Version">
///     The plugin's own version, or the empty string when it is unknown.
/// </param>
/// <param name="SourcePath">The source file the plugin came from.</param>
/// <param name="Phase">Where the plugin is in its lifecycle.</param>
/// <param name="Axes">
///     The extension axes the plugin carries — the markers it implements.
/// </param>
/// <param name="Dependencies">
///     The dependency names the plugin declared (<c>IPlugin.Dependencies</c>).
/// </param>
/// <param name="UnsatisfiedDependencies">
///     The declared dependencies the host cannot satisfy yet. Non-empty only
///     while the plugin is <see cref="PluginInventoryPhase.Pending" />.
/// </param>
/// <param name="Registrations">
///     What the plugin's registrations did in the host, one entry per door the
///     registrar called.
/// </param>
/// <param name="FailureReason">
///     Why the host refused the plugin, or <c>null</c> when it did not.
/// </param>
/// <param name="ManifestPresent">
///     Whether the plugin shipped a manifest next to its source (#1739).
/// </param>
public sealed record PluginInventoryEntry(
    string Name,
    string Version,
    string SourcePath,
    PluginInventoryPhase Phase,
    IReadOnlyList<string> Axes,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> UnsatisfiedDependencies,
    IReadOnlyList<PluginInventoryRegistration> Registrations,
    string? FailureReason,
    bool ManifestPresent);

/// <summary>
///     The live plugin composition, as one document.
/// </summary>
/// <remarks>
///     <para>
///         The snapshot carries three things: the axes the product declares,
///         the per-route verdicts for those axes, and the plugins the host
///         has in front of it. The first two are read from the registries;
///         the third is filled in by the host as the load proceeds.
///     </para>
/// </remarks>
/// <param name="Route">
///     The route this composition is on — the id the route registry declares —
///     or <c>null</c> when the host was not told which route it is.
/// </param>
/// <param name="Entries">The plugins, in the order the host met them.</param>
public sealed record PluginInventory(string? Route, IReadOnlyList<PluginInventoryEntry> Entries)
{
    /// <summary>
    ///     The version of the snapshot format. A consumer that does not know
    ///     this version reads a document it cannot trust.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     The file the composition is published to, inside the plugin root.
    /// </summary>
    /// <remarks>
    ///     The publisher is the out-of-process host; the reader is the CLI's
    ///     <c>harbor plugin inventory</c>. The name lives here so the two ends
    ///     cannot drift apart: a reader that guessed the name would read
    ///     nothing and say "no plugins".
    /// </remarks>
    public const string FileName = "inventory.json";

    /// <summary>
    ///     The snapshot as a JSON document.
    /// </summary>
    /// <returns>The JSON text, with a trailing newline.</returns>
    /// <remarks>
    ///     The document is written with <see cref="Utf8JsonWriter" /> rather than
    ///     through a serializer, so it costs no reflection and works under
    ///     NativeAOT (#626). The order of the members is fixed, so two
    ///     snapshots of the same composition are byte-identical and a diff
    ///     between them says what changed.
    /// </remarks>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    ///     Write the snapshot into an existing JSON writer.
    /// </summary>
    /// <param name="writer">The writer to write into.</param>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);

        if (Route is null)
        {
            writer.WriteNull("route");
        }
        else
        {
            writer.WriteString("route", Route);
        }

        writer.WriteStartArray("axes");
        foreach (PluginAxis axis in PluginAxisRegistry.All)
        {
            writer.WriteStartObject();
            writer.WriteString("marker", axis.Marker);
            writer.WriteString("door", axis.Door);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("routes");
        foreach (PluginRouteAxis row in PluginRouteAxisRegistry.All)
        {
            writer.WriteStartObject();
            writer.WriteString("route", row.Route);
            writer.WriteString("door", row.Door);
            writer.WriteString("marker", PluginRouteAxisRegistry.MarkerOf(row.Door));
            writer.WriteString("carriage", row.Carriage.ToString());
            writer.WriteString("reason", row.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("plugins");
        foreach (PluginInventoryEntry entry in Entries)
        {
            WriteEntry(writer, entry);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Write one plugin's entry.
    /// </summary>
    /// <param name="writer">The writer to write into.</param>
    /// <param name="entry">The plugin to write.</param>
    private static void WriteEntry(Utf8JsonWriter writer, PluginInventoryEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("name", entry.Name);
        writer.WriteString("version", entry.Version);
        writer.WriteString("source", entry.SourcePath);
        writer.WriteString("phase", entry.Phase.ToString().ToLowerInvariant());
        WriteStringArray(writer, "axes", entry.Axes);
        WriteStringArray(writer, "dependencies", entry.Dependencies);
        WriteStringArray(writer, "unsatisfiedDependencies", entry.UnsatisfiedDependencies);
        writer.WriteBoolean("manifest", entry.ManifestPresent);

        writer.WriteStartArray("registrations");
        foreach (PluginInventoryRegistration registration in entry.Registrations)
        {
            writer.WriteStartObject();
            writer.WriteString("door", registration.Door);
            writer.WriteString("subject", registration.Subject);
            writer.WriteString(
                "outcome",
                registration.Outcome == PluginInventoryOutcome.Registered ? "registered" : "refused");
            if (registration.Reason is null)
            {
                writer.WriteNull("reason");
            }
            else
            {
                writer.WriteString("reason", registration.Reason);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (entry.FailureReason is null)
        {
            writer.WriteNull("failureReason");
        }
        else
        {
            writer.WriteString("failureReason", entry.FailureReason);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    ///     Write a JSON array of strings.
    /// </summary>
    /// <param name="writer">The writer to write into.</param>
    /// <param name="name">The property name.</param>
    /// <param name="values">The values.</param>
    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
