// MessagePartAdapter.cs — the shared domain ↔ persist-form projection for
// assistant-message content parts (#1591).
//
// Linked source (no .csproj): compiled into Harbor.Storage.Jsonl and
// Harbor.Storage.Sqlite via <Compile Include> items — the same mechanism as
// the folder's other files. No ProjectReference between storage assemblies, so
// the Storage_ReferencesOnlyAbstractions architecture rule stays green.
//
// WHY THIS FILE EXISTS
//
// JsonlLineParser (span reader), JsonlMessageCodec.DeserializePart (element
// reader) and SqliteMappers.ContentPartJsonConverter (row converter) used to
// hold three hand-maintained copies of the same tag → type projection: which
// tags the persist form carries, which members each tag requires, which tag
// builds which ContentPart, and what a refusal must name. A new part kind
// needed synchronized edits in all three places with nothing noticing the
// edit that forgot one. This adapter is the one contract; the backends differ
// only in TRANSPORT:
//   - JsonlLineParser scans raw UTF-8 spans and classifies the tag itself;
//     the span matcher below keeps that path allocation-free on success.
//   - the other two readers scan a JsonElement through TryScanPartFields —
//     one shared scanner, with the row's legacy-PascalCase tolerance driven
//     by the legacyCasing flag rather than a second hand-maintained copy.
//   - every backend writes its own refusal WORDING — the SQLite converter says
//     "row", the JSONL paths say "session" and "payload"; the refusal STRUCTURE —
//     kind, tag, missing member — is produced here and only formatted there.
//
// ADDING A NEW ContentPart SUBTYPE (the checklist the three copies used to
// scatter) is now one shared table plus the two write shapes:
//   1. a tag constant below, arms in the span classifier and in the
//      ClassifyTag(string) path, and arms in TryBuildContentPart — the tag to
//      type factory plus the required-member census.
//   2. a write arm in the JSONL codec SerializePart DTO and in the SQLite
//      PartWriter converter — the transports' own shapes.
//   3. nothing else: tests/Harbor.Storage.Tests/StorageBackendParityTests
//      pins both backends to the same vocabulary, and a backend the table
//      missed goes red there instead of silently dropping a tagged part.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Shared;

/// <summary>
///     The one domain → persist-form contract for content parts: the tag
///     vocabulary, the per-tag required-member census and the tag → type
///     factory all three store readers previously carried separately (#1591).
/// </summary>
/// <remarks>
///     <para>
///         This is an Object Adapter over the persisted part object, not a
///         rewrite of either store's read path. The JSONL line parser keeps
///         its zero-allocation span scan (the allocation budget tests pin
///         exactly what this file must not cost); the SQLite row keeps its
///         AOT source-gen frame. What moved out is the part of each reader
///         that was the SAME code written three times.
///     </para>
///     <para>
///         <b>#550 semantics, kept verbatim:</b> a part this build cannot
///         rebuild is refused by name — there is no null to skip. An unknown
///         tag is version skew; a known tag missing a member is a corrupt
///         record. TryBuildContentPart never invents a part for either.
///     </para>
/// </remarks>
internal static class MessagePartAdapter
{
    // The tag vocabulary. Both backends' WRITE shapes take their discriminator
    // strings from these constants (SerializePart DTOs and the SQLite
    // PartWriter), so a rename cannot drift between the transports.
    internal const string TextTag = "text";
    internal const string ThinkingTag = "thinking";
    internal const string ToolCallTag = "tool_call";
    internal const string FileTag = "file";

    /// <summary>
    ///     The discrimination of one persisted part, by its tag. Unknown is
    ///     the honest value for a tag this build has no row for — the literal
    ///     stays readable through <see cref="PartScan.Tag" />, which the
    ///     transports materialize only on refusal paths.
    /// </summary>
    internal enum PartTagKind : byte
    {
        Unknown,
        Text,
        Thinking,
        ToolCall,
        File
    }

    /// <summary>Whether a <c>tool_call</c> part requires the <c>args</c> member.</summary>
    /// <remarks>
    ///     A real, documented difference between the transports, not a drift:
    ///     the JSONL line parser requires the member to be present, while the
    ///     element and row readers tolerate its absence (an undefined element
    ///     is a faithful reading of "this part carries no arguments", and
    ///     refusing it would truncate a row those readers had always loaded).
    ///     The <see cref="SizePolicy" /> below is the mirror image for the
    ///     <c>file</c> part's <c>SizeBytes</c>: the line parser reads an
    ///     absent member as zero, the element and row readers required it —
    ///     both before this file existed, both preserved here as an explicit
    ///     parameter rather than silently unified.
    /// </remarks>
    internal enum ArgsPolicy : byte
    {
        RequirePresent,
        TolerateMissing
    }

    /// <summary>Whether a <c>file</c> part requires the <c>sizeBytes</c> member.</summary>
    internal enum SizePolicy : byte
    {
        RequirePresent,
        TolerateMissing
    }

    /// <summary>Which backend asked for the refusal text — the wording carrier.</summary>
    internal enum PartTransport : byte
    {
        JsonlLine,
        JsonlElement,
        SqliteRow
    }

    /// <summary>Why a part object was refused, structured for the backend formatting.</summary>
    internal enum PartRefusalKind : byte
    {
        NoDiscriminator,
        UnknownTag,
        MissingMember
    }

    /// <summary>A structured refusal: the kind, the offending tag and the missing member.</summary>
    /// <remarks>
    ///     <see cref="Tag" /> is the literal that came off the wire and is
    ///     always present for the element/row transports; the line parser
    ///     materializes it only for unknown tags (success paths stay
    ///     allocation-free), so <see cref="FormatRefusal" /> falls back to the
    ///     canonical constants via <see cref="TagKind" /> when it is null.
    /// </remarks>
    internal readonly record struct PartRefusal(
        PartRefusalKind Kind,
        PartTagKind TagKind,
        string? Tag,
        string? Member);

    /// <summary>
    ///     The scanned members of one persisted part, transport-independent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Tag" /> carries the raw discriminator string for the
    ///         refusal texts; the built part is driven by the separately
    ///         passed <see cref="PartTagKind" />, so the span transport never
    ///         stringifies a tag it already classified and recognized.
    ///     </para>
    ///     <para>
    ///         <see cref="HasSizeBytes" /> distinguishes an absent file member
    ///         from an explicit zero: <c>FilePart.SizeBytes</c> of 0 is a
    ///         legitimate value and must not read as corruption.
    ///     </para>
    /// </remarks>
    internal readonly record struct PartScan(
        string? Tag,
        string? Text,
        string? PartId,
        string? ToolName,
        JsonElement Args,
        bool HasArgs,
        string? Path,
        string? MimeType,
        long SizeBytes,
        bool HasSizeBytes);

    /// <summary>Classifies a UTF-8 tag literal (the line parser's zero-alloc path).</summary>
    internal static PartTagKind ClassifyTag(ReadOnlySpan<byte> tag) => tag switch
    {
        var t when t.SequenceEqual("text"u8) => PartTagKind.Text,
        var t when t.SequenceEqual("thinking"u8) => PartTagKind.Thinking,
        var t when t.SequenceEqual("tool_call"u8) => PartTagKind.ToolCall,
        var t when t.SequenceEqual("file"u8) => PartTagKind.File,
        _ => PartTagKind.Unknown
    };

    /// <summary>Classifies a tag string already materialized by the element/row transports.</summary>
    internal static PartTagKind ClassifyTag(string? tag) => tag switch
    {
        TextTag => PartTagKind.Text,
        ThinkingTag => PartTagKind.Thinking,
        ToolCallTag => PartTagKind.ToolCall,
        FileTag => PartTagKind.File,
        _ => PartTagKind.Unknown
    };

    /// <summary>The canonical tag constants, by kind — used by refusal formatting.</summary>
    internal static string TagText(PartTagKind kind) => kind switch
    {
        PartTagKind.Text => TextTag,
        PartTagKind.Thinking => ThinkingTag,
        PartTagKind.ToolCall => ToolCallTag,
        PartTagKind.File => FileTag,
        _ => "<no 'type' discriminator>"
    };

    /// <summary>
    ///     Scans one persisted part object into <see cref="PartScan" />.
    ///     Shared scanner for the element reader (JSONL codec) and the row
    ///     reader (SQLite converter); <paramref name="legacyCasing" /> is the
    ///     row's pre-camelCase tolerance, serialized into a flag instead of a
    ///     second hand-maintained scanner.
    /// </summary>
    /// <returns>
    ///     <see langword="false" /> when the object carries no usable 'type'
    ///     discriminator — the caller formats the
    ///     <see cref="PartRefusalKind.NoDiscriminator" /> refusal. Malformed
    ///     member kinds keep the transports' existing behaviour: the whole
    ///     scan runs under the caller's Result.Try rail, so a throw there is
    ///     the same failure it has always been.
    /// </returns>
    internal static bool TryScanPartFields(JsonElement element, bool legacyCasing, out PartScan scan)
    {
        // The discriminator read mirrors each transport's previous exact shape:
        // the row takes whatever 'type' or 'Type' holds ( GetString may throw
        // for a non-string member — the caller's rail turns that into the row
        // failure it has always produced), the element reader requires a
        // string and treats anything else as an absent discriminator.
        string? tag = legacyCasing
            ? element.TryGetProperty("type", out var tp) ? tp.GetString()
                : element.TryGetProperty("Type", out var tp2) ? tp2.GetString()
                : null
            : element.TryGetProperty("type", out var tp3) && tp3.ValueKind == JsonValueKind.String
                ? tp3.GetString()
                : null;

        if (tag is null)
        {
            scan = default;
            return false;
        }

        scan = new PartScan(
            Tag: tag,
            Text: StringOrNull(element, "text", legacyCasing),
            PartId: StringOrNull(element, "id", legacyCasing),
            ToolName: StringOrNull(element, "toolName", legacyCasing),
            Args: ArgsElement(element, legacyCasing, out bool hasArgs),
            HasArgs: hasArgs,
            Path: StringOrNull(element, "path", legacyCasing),
            MimeType: StringOrNull(element, "mimeType", legacyCasing),
            SizeBytes: Int64OrNull(element, "sizeBytes", legacyCasing, out bool hasSize),
            HasSizeBytes: hasSize);

        return true;
    }

    /// <summary>
    ///     Builds a <see cref="ContentPart" /> from the scanned members. The
    ///     tag → type factory plus the required-member census that all three
    ///     store readers carried separately (#1591); every refusal it produces
    ///     is structured, and the caller names it through
    ///     <see cref="FormatRefusal" /> in the transport's own wording.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #550. This is a tag → type factory, not a walk over the part
    ///         union, so a reader cannot answer with a part it cannot rebuild:
    ///         an unknown tag is version skew (the session was written by a
    ///         newer Harbor and the record is refused rather than read with
    ///         the part missing), and a known tag missing a member is a corrupt
    ///         record (the line claims a part it does not contain). Both
    ///         refusals name the part; there is no union case that stands for
    ///         "a part I could not read", and inventing one would be the same
    ///         loss with a nicer shape.
    ///     </para>
    ///     <para>
    ///         <b>Spelling custody:</b> the missing-member census runs in
    ///         declaration order (id → toolName → args, path → mimeType →
    ///         sizeBytes), which is the order the previous per-member probes
    ///         threw in — the refusals this returns name the FIRST missing
    ///         member, exactly where the copies named theirs.
    ///     </para>
    /// </remarks>
    internal static bool TryBuildContentPart(
        in PartScan scan,
        PartTagKind kind,
        ArgsPolicy argsPolicy,
        SizePolicy sizePolicy,
        [NotNullWhen(true)] out ContentPart? part,
        out PartRefusal refusal)
    {
        // The success arms mirror the previous three factories verbatim: same
        // member gates, same tolerance (the args and size policies are the
        // knobs that carry each transport's documented fidelity).
        part = kind switch
        {
            PartTagKind.Text when scan.Text is not null => new TextPart(scan.Text),
            PartTagKind.Thinking when scan.Text is not null => new ThinkingPart(scan.Text),
            PartTagKind.ToolCall when scan.PartId is not null && scan.ToolName is not null
                && (!ArgsRequired(argsPolicy) || scan.HasArgs)
                => new ToolCallPart(scan.PartId, scan.ToolName, scan.Args),
            PartTagKind.File when scan.Path is not null && scan.MimeType is not null
                && (!SizeRequired(sizePolicy) || scan.HasSizeBytes)
                => new FilePart(scan.Path, scan.MimeType, scan.SizeBytes),
            _ => null,
        };

        if (part is not null)
        {
            refusal = default;
            return true;
        }

        refusal = kind switch
        {
            PartTagKind.Text => Missing(scan, kind, scan.Text is null ? "text" : null),
            PartTagKind.Thinking => Missing(scan, kind, scan.Text is null ? "text" : null),
            PartTagKind.ToolCall => Missing(
                scan,
                kind,
                scan.PartId is null ? "id"
                    : scan.ToolName is null ? "toolName"
                    : ArgsRequired(argsPolicy) && !scan.HasArgs ? "args"
                    : null),
            PartTagKind.File => Missing(
                scan,
                kind,
                scan.Path is null ? "path"
                    : scan.MimeType is null ? "mimeType"
                    : SizeRequired(sizePolicy) && !scan.HasSizeBytes ? "sizeBytes"
                    : null),
            _ => new PartRefusal(PartRefusalKind.UnknownTag, kind, scan.Tag, null),
        };

        return false;
    }

    /// <summary>Missing-member refusal, or the unknown-tag refusal when no hole was named.</summary>
    private static PartRefusal Missing(in PartScan scan, PartTagKind kind, string? member) =>
        member is null
            ? new PartRefusal(PartRefusalKind.UnknownTag, kind, scan.Tag, null)
            : new PartRefusal(PartRefusalKind.MissingMember, kind, scan.Tag, member);

    /// <summary>Whether the args policy pins the member to present.</summary>
    private static bool ArgsRequired(ArgsPolicy policy) => policy == ArgsPolicy.RequirePresent;

    /// <summary>Whether the size policy pins the member to present.</summary>
    private static bool SizeRequired(SizePolicy policy) => policy == SizePolicy.RequirePresent;

    /// <summary>
    ///     Formats a refusal into the transport's own wording. The texts are
    ///     the ones the previous copies produced, byte for byte: shared
    ///     structure, per-transport naming ("row" for SQLite, "session" for
    ///     the line parser, "payload" for the element codec).
    /// </summary>
    internal static string FormatRefusal(PartRefusal refusal, PartTransport transport)
    {
        switch (refusal.Kind)
        {
            case PartRefusalKind.NoDiscriminator:
                return NoDiscriminatorText(transport);
            case PartRefusalKind.UnknownTag:
                return UnknownTagText(refusal.Tag ?? TagText(refusal.TagKind), transport);
            case PartRefusalKind.MissingMember:
                return transport switch
                {
                    // The line parser's wording lists the tag's whole member set
                    // (its previous single clause per kind), not the one hole.
                    PartTransport.JsonlLine => MissingMemberText(
                        TagText(refusal.TagKind),
                        refusal.TagKind switch
                        {
                            PartTagKind.Text => "text",
                            PartTagKind.Thinking => "text",
                            PartTagKind.ToolCall => "'id', 'toolName' or 'args'",
                            _ => "'path', 'mimeType' or 'sizeBytes'",
                        },
                        period: false),
                    _ => MissingMemberText(refusal.Tag ?? TagText(refusal.TagKind), refusal.Member ?? "<member>", period: true),
                };
            default:
                return $@"content part refusal '{refusal.Kind}' has no text for {transport}.";
        }
    }

    private static string NoDiscriminatorText(PartTransport transport) => transport switch
    {
        // The line parser folded the absence into its unknown-tag clause with
        // a placeholder tag; the element and row readers had a dedicated one.
        PartTransport.JsonlLine => UnknownTagText("<no 'type' discriminator>", transport),
        _ => @"content part has no 'type' discriminator, so it cannot be rebuilt.",
    };

    private static string UnknownTagText(string tag, PartTransport transport) => transport switch
    {
        PartTransport.JsonlLine => $@"content part type '{tag}' is not known to this build — this session "
            + "was written by a newer Harbor, and the record is refused rather than read with the part missing",
        PartTransport.JsonlElement => $@"content part type '{tag}' is not known to this build — this payload "
            + "was written by a newer Harbor, and the message is refused rather than read with the part missing",
        _ => $@"content part type '{tag}' is not known to this build; the row was refused rather than read "
            + "with the part missing.",
    };

    private static string MissingMemberText(string tag, string member, bool period) =>
        $@"content part of type '{tag}' is missing '{member}'{(period ? "." : string.Empty)}";

    // ── member probes shared by the element and row scans ──────────────────

    /// <summary>
    ///     A string member, or null. A member that sits there without being a
    ///     string reads as absent (that is what the previous per-member probes
    ///     reported: "missing 'camel'"), the legacy spelling is only consulted
    ///     when the current one is absent, and a present-but-null string is
    ///     null, which the census refuses.
    /// </summary>
    private static string? StringOrNull(JsonElement element, string camel, bool legacyCasing)
    {
        if (element.TryGetProperty(camel, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (!legacyCasing)
            return null;

        string legacy = char.ToUpperInvariant(camel[0]) + camel[1..];
        return element.TryGetProperty(legacy, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.String
            ? legacyValue.GetString()
            : null;
    }

    /// <summary>
    ///     The <c>args</c> member: present under either spelling for the row,
    ///     current spelling only for the element reader. Absent is a
    ///     legitimate shape the census grades by the args policy — the probe
    ///     itself never refuses.
    /// </summary>
    private static JsonElement ArgsElement(JsonElement element, bool legacyCasing, out bool hasArgs)
    {
        if (element.TryGetProperty("args", out var a))
        {
            hasArgs = true;
            return a.Clone();
        }

        if (legacyCasing && element.TryGetProperty("Args", out var a2))
        {
            hasArgs = true;
            return a2.Clone();
        }

        hasArgs = false;
        return default;
    }

    /// <summary>A number member, with presence distinguished from zero.</summary>
    private static long Int64OrNull(JsonElement element, string camel, bool legacyCasing, out bool present)
    {
        if (element.TryGetProperty(camel, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            present = true;
            return value.GetInt64();
        }

        if (!legacyCasing)
        {
            present = false;
            return 0;
        }

        string legacy = char.ToUpperInvariant(camel[0]) + camel[1..];
        if (element.TryGetProperty(legacy, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.Number)
        {
            present = true;
            return legacyValue.GetInt64();
        }

        present = false;
        return 0;
    }
}
