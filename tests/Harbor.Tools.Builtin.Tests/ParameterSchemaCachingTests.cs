using System.Runtime.CompilerServices;
using System.Text.Json;
using Harbor.Abstractions.Tools;
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
///         and the schema is the one that was approved — which
///         <see cref="ParameterSchemaPinTests" /> checks against the pin.
///     </para>
///     <para>
///         The tool set is <see cref="ToolSchemaFixtures.ToolFactories" />: every
///         tool that owns a schema. A tool added without a factory there loses
///         its coverage silently, so the factories and the pin are checked
///         against each other.
///     </para>
/// </remarks>
public class ParameterSchemaCachingTests
{
    [Test]
    public async Task ParameterSchema_IsTheSameDocumentForEveryInstanceOfATool()
    {
        var reParsed = new List<string>();

        foreach ((string key, Func<ITool> factory) in ToolSchemaFixtures.ToolFactories)
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
        foreach (var entry in ToolSchemaFixtures.ToolFactories)
        {
            documents.Add(entry.Factory().ParameterSchema);
            documents.Add(entry.Factory().ParameterSchema);
        }

        await Assert.That(documents.Count).IsEqualTo(ToolSchemaFixtures.ToolFactories.Length * 2);
        await Assert.That(CountDistinctByReference(documents))
            .IsEqualTo(ToolSchemaFixtures.ToolFactories.Length)
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

    /// <summary>
    ///     Capture the schema of a fresh instance of every tool and drop the
    ///     instance itself. Kept out of the caller so the tools are not kept
    ///     alive by a stack slot of the test method.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string Key, WeakReference Reference)> CaptureSchemaReferences()
    {
        var captured = new List<(string Key, WeakReference Reference)>();
        foreach ((string key, Func<ITool> factory) in ToolSchemaFixtures.ToolFactories)
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
}
