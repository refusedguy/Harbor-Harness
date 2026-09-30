using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Harbor.Abstractions.Sessions;
namespace Harbor.Application.Sessions;
/// <summary>
///     Memoizing decorator over <see cref="ISystemPromptBuilder" /> (Ф6/A2).
///     The loop rebuilds the prompt context every turn even though the inputs
///     (agent definition, model, resolved tools, context files, skills, MCP
///     instructions, working directory) rarely change within a run — and the
///     builder itself is a ~180-line template assembly. This decorator hashes
///     ALL context components into a key and serves repeat contexts from a
///     <see cref="ConcurrentDictionary{TKey,TValue}" /> without touching the
///     inner builder.
/// </summary>
/// <remarks>
///     <para>
///         Thread-safe by contract (<c>ISystemPromptBuilder</c> implementers
///         must be): the dictionary is concurrent and the key derivation is
///         pure. Nothing evicts — no TTL, no size cap, no clear — so the cache
///         holds one entry per distinct context for as long as the decorator
///         is reachable.
///     </para>
///     <para>
///         <b>The lifetime is the process, not the run.</b> The decorator is
///         built in <c>AgentLoop</c>'s constructor, <c>IAgentLoop</c> is a DI
///         singleton, and the CLI builds its host once around the whole REPL —
///         so every turn of every run in a session shares this one dictionary.
///         An earlier version of this comment said the loop "creates one
///         decorator per instance, so entries die with the loop", and read
///         from the decorator's construction site alone it is true; the
///         registration makes it false. Entries outlive the run that filled
///         them, which is why the key must be complete rather than merely good
///         enough for one turn's inputs.
///     </para>
///     <para>
///         The key covers every <see cref="SystemPromptContext" /> component
///         the default builder renders. Permission-rule edits that keep the
///         resolved tool set identical do not change the key — acceptable,
///         because the default builder renders tools, not rules. Two inputs
///         still reach the prompt without reaching the key, and are recorded
///         rather than fixed here: #815 (<c>ModelInfo.ProviderId</c>, where the
///         key holds the agent's provider instead of the model's) and #814
///         (the wall clock, which is not a context component at all). Both are
///         latent in-tree. The list is here so the coverage claim reads as the
///         partial claim it is.
///     </para>
/// </remarks>
public sealed class CachingSystemPromptBuilder(ISystemPromptBuilder inner) : ISystemPromptBuilder
{
    private const char Separator = '\u001f';

    private static readonly ConcurrentDictionary<System.Text.Json.JsonDocument, string> _schemaTextCache = new();
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>Number of prompts served from cache (diagnostics/tests).</summary>
    public int CacheHits => Volatile.Read(ref _hits);

    /// <summary>Number of prompts built through the inner builder.</summary>
    public int Misses => Volatile.Read(ref _misses);

    private int _hits;
    private int _misses;

    /// <inheritdoc />
    public async Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
    {
        string key = ComputeKey(context);
        if (_cache.TryGetValue(key, out string? cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);
        string built = await inner.BuildAsync(context, ct).ConfigureAwait(false);
        _cache[key] = built;
        return built;
    }

    /// <summary>
    ///     Derive a deterministic cache key from every context component.
    ///     SHA-256 over a separator-joined field dump — collision-safe for
    ///     realistic inputs and allocation-light enough for a per-turn call.
    /// </summary>
    private static string ComputeKey(SystemPromptContext context)
    {
        var sb = new StringBuilder(512);
        AppendField(sb, context.Agent.Name.Value);
        AppendField(sb, context.Agent.DisplayName);
        AppendField(sb, context.Agent.Description);
        AppendField(sb, context.Agent.Model);
        AppendField(sb, context.Agent.ProviderId);
        AppendField(sb, context.Agent.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(sb, context.Agent.ReasoningEffort?.ToString());
        AppendField(sb, context.Agent.SystemPromptAppend);
        AppendField(sb, context.Model.Id);

        var tools = context.Tools;
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            AppendField(sb, t.Name.Value);
            AppendField(sb, t.Description);
            string raw = _schemaTextCache.GetOrAdd(t.Schema, static d => d.RootElement.GetRawText());
            AppendField(sb, raw);
            AppendField(sb, t.PromptSnippet);
            // #792: the builder emits up to MaxGuidelinesPerTool of these as
            // `  - <guideline>` lines, which makes them prompt content exactly
            // as the three fields above are. Keyed raw — every guideline, not
            // the capped subset that happens to render — because this decorator
            // wraps an unknown ISystemPromptBuilder: over-keying costs one
            // rebuild, under-keying serves the previous turn's text. One field
            // per guideline, so the count and the order ride along too.
            var guidelines = t.PromptGuidelines;
            for (int g = 0; g < guidelines.Count; g++)
            {
                AppendField(sb, guidelines[g]);
            }
        }

        var files = context.ContextFiles;
        for (int i = 0; i < files.Count; i++)
        {
            AppendField(sb, files[i].Path);
            AppendField(sb, files[i].Content);
        }

        var skills = context.Skills;
        for (int i = 0; i < skills.Count; i++)
        {
            AppendField(sb, skills[i].Name);
            AppendField(sb, skills[i].Description);
            AppendField(sb, skills[i].FilePath);
        }

        AppendField(sb, context.McpInstructions);
        AppendField(sb, context.WorkingDirectory);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>Append one field plus separator; null fields collapse to a bare separator.</summary>
    private static void AppendField(StringBuilder sb, string? field)
    {
        sb.Append(field);
        sb.Append(Separator);
    }
}
