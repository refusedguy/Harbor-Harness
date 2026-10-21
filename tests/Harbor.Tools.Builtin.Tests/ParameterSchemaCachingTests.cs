using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Agents;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1574 — every builtin tool used to parse its parameter schema inside the
///     constructor, so the very same JSON block was parsed again for every tool
///     instance. The schema is a constant, so it now lives in a process-wide
///     static and the property hands out that one document.
/// </summary>
/// <remarks>
///     <para>
///         These tests pin the three properties that make the difference
///         observable: the same tool hands out the same
///         <see cref="JsonDocument" /> to every instance, the number of live
///         schema documents equals the number of tools (no per-instance copies),
///         and the schema content is unchanged — it still round-trips to the
///         shape pinned in <see cref="ParameterSchemaGolden" />.
///     </para>
///     <para>
///         The tool set below is the full set of tools that own a schema. A tool
///         added without a factory here loses its coverage silently, so the
///         factories and the pinned schemas are checked against each other.
///     </para>
/// </remarks>
public class ParameterSchemaCachingTests
{
    /// <summary>
    ///     A notes root that is never written to — the schema test only reads
    ///     the schema, it never executes the tool.
    /// </summary>
    private const string NotebookRoot = "harbor-schema-test-notes";

    /// <summary>
    ///     Every builtin tool that owns a parameter schema, keyed by the tool
    ///     name. <c>mcp_adapter</c> stands in for <see cref="McpToolAdapter" />,
    ///     whose name is derived from the server and the tool it wraps and is
    ///     therefore not stable.
    /// </summary>
    private static readonly (string Key, Func<ITool> Factory)[] ToolFactories =
    [
        ("bash", () => new BashTool(new SystemProcessProxy(), NullLogger<BashTool>.Instance)),
        ("edit", () => new EditTool(NullLogger<EditTool>.Instance)),
        ("glob", () => new GlobTool(NullLogger<GlobTool>.Instance)),
        ("grep", () => new GrepTool(NullLogger<GrepTool>.Instance)),
        ("ls", () => new LsTool(NullLogger<LsTool>.Instance)),
        ("lsp", () => new LspTool(NullLogger<LspTool>.Instance)),
        ("mcp", () => new McpToolTool(NullLogger<McpToolTool>.Instance)),
        ("mcp_adapter", () => new McpToolAdapter(ScriptedMcpRegistry.Succeed("{}"), "server", "tool")),
        ("mcp_prompt", () => new McpPromptTool(NullLogger<McpPromptTool>.Instance)),
        ("notebook", () => new NotebookTool(NullLogger<NotebookTool>.Instance, NotebookRoot)),
        ("patch", () => new PatchTool(NullLogger<PatchTool>.Instance)),
        ("plugin_manager", () => new PluginManagerTool(new FakePluginInventory(), NullLogger<PluginManagerTool>.Instance)),
        ("read", () => new ReadTool(NullLogger<ReadTool>.Instance)),
        ("read_mcp_resource", () => new McpResourceTool(NullLogger<McpResourceTool>.Instance)),
        ("ripgrep", () => new RipGrepTool(NullLogger<RipGrepTool>.Instance)),
        ("session_read", () => new SessionReadTool(new DeferredSessionStore(), NullLogger<SessionReadTool>.Instance)),
        ("session_steer", () => new SessionSteerTool(new DeferredSessionStore(), NullLogger<SessionSteerTool>.Instance)),
        ("skill", () => new SkillTool(NullLogger<SkillTool>.Instance)),
        ("task", () => new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance)),
        ("tree", () => new TreeTool(NullLogger<TreeTool>.Instance)),
        ("webfetch", () => new WebFetchTool(NullLogger<WebFetchTool>.Instance)),
        ("write", () => new WriteTool(NullLogger<WriteTool>.Instance)),
    ];

    [Test]
    public async Task ParameterSchema_IsTheSameDocumentForEveryInstanceOfATool()
    {
        var reParsed = new List<string>();

        foreach ((string key, Func<ITool> factory) in ToolFactories)
        {
            ITool first = factory();
            ITool second = factory();

            // Repeated reads of one instance must not re-parse either.
            JsonDocument firstRead = first.ParameterSchema;
            JsonDocument secondRead = first.ParameterSchema;
            if (!ReferenceEquals(firstRead, secondRead))
                reParsed.Add($"{key} (repeated read)");
            if (!ReferenceEquals(firstRead, second.ParameterSchema))
                reParsed.Add($"{key} (second instance)");
        }

        await Assert.That(reParsed).IsEmpty()
            .Because("a parameter schema is a constant and must be parsed once per process");
    }

    [Test]
    public async Task LiveSchemaDocuments_CountEqualsTheToolCount()
    {
        // Two instances per tool: a schema owned by the instance would produce
        // two documents per tool, a cached one produces exactly one.
        var documents = new List<JsonDocument>();
        foreach (var entry in ToolFactories)
        {
            documents.Add(entry.Factory().ParameterSchema);
            documents.Add(entry.Factory().ParameterSchema);
        }

        await Assert.That(documents.Count).IsEqualTo(ToolFactories.Length * 2);
        await Assert.That(CountDistinctByReference(documents)).IsEqualTo(ToolFactories.Length)
            .Because("one live schema document per tool, shared by every instance of it");
    }

    [Test]
    public async Task SchemaDocuments_SurviveTheDeathOfTheToolThatProducedThem()
    {
        List<(string Key, WeakReference Reference)> captured = CaptureSchemaReferences();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var collected = new List<string>();
        foreach ((string key, WeakReference reference) in captured)
        {
            if (!reference.IsAlive)
                collected.Add(key);
        }

        await Assert.That(collected).IsEmpty()
            .Because("a static schema document is rooted by its type; only a per-instance one dies with the tool");
    }

    [Test]
    public async Task EveryBuiltinTool_HasAPinnedSchema()
    {
        string[] tools = ToolFactories.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] pinned = ParameterSchemaGolden.Schemas.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        await Assert.That(string.Join(", ", pinned)).IsEqualTo(string.Join(", ", tools))
            .Because("a tool without a pinned schema would change shape unnoticed");
    }

    [Test]
    public async Task EveryToolSchema_KeepsItsPinnedShape()
    {
        foreach ((string key, Func<ITool> factory) in ToolFactories)
        {
            ITool tool = factory();
            JsonDocument schema = tool.ParameterSchema;

            await Assert.That(ParameterSchemaGolden.Schemas.ContainsKey(key)).IsTrue()
                .Because($"tool '{key}' has no pinned schema");

            string pinned = ParameterSchemaGolden.Schemas[key];
            using JsonDocument golden = JsonDocument.Parse(pinned);

            await Assert.That(Normalize(schema.RootElement)).IsEqualTo(Normalize(golden.RootElement))
                .Because($"tool '{key}' changed its parameter schema");

            // The schema must survive a parse round-trip with the same shape.
            using JsonDocument roundTripped = JsonDocument.Parse(schema.RootElement.GetRawText());
            await Assert.That(Normalize(roundTripped.RootElement)).IsEqualTo(Normalize(schema.RootElement))
                .Because($"tool '{key}' schema does not round-trip");
        }
    }

    /// <summary>
    ///     Capture the schema of a fresh instance of every tool and drop the
    ///     instance itself. Kept out of the caller so the tools are not kept
    ///     alive by a stack slot of the test method.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string Key, WeakReference Reference)> CaptureSchemaReferences()
    {
        var captured = new List<(string Key, WeakReference Reference)>();
        foreach ((string key, Func<ITool> factory) in ToolFactories)
        {
            ITool tool = factory();
            captured.Add((key, new WeakReference(tool.ParameterSchema)));
        }

        return captured;
    }

    private static int CountDistinctByReference(List<JsonDocument> documents)
    {
        var distinct = new List<JsonDocument>();
        foreach (JsonDocument document in documents)
        {
            bool known = false;
            foreach (JsonDocument candidate in distinct)
            {
                if (ReferenceEquals(candidate, document))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
                distinct.Add(document);
        }

        return distinct.Count;
    }

    /// <summary>
    ///     Re-serialize a schema element so two documents can be compared
    ///     without caring about the whitespace of the literal they came from.
    /// </summary>
    private static string Normalize(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
