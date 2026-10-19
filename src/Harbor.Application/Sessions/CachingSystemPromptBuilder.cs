using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
///         <b>THE CONTRACT, stated once so it stops being a promise in a
///         comment: the key is a fingerprint of the RENDER, not of the run.</b>
///         Every <see cref="SystemPromptContext" /> member the inner builder
///         reads must be in the key. Nothing beyond that is required of it.
///     </para>
///     <para>
///         <b>The contract is asymmetric, and the asymmetry is load-bearing.</b>
///         UNDER-keying is a defect: a hit is served without calling the inner
///         builder, so a member the render reads and the key does not hold
///         means the model reads the previous turn's text, with no miss count
///         and no log line. OVER-keying is admissible: it costs one rebuild.
///         #792 was the first violation (<c>ToolDescriptor.PromptGuidelines</c>
///         rendered as <c>  - &lt;guideline&gt;</c>, never keyed) and it shipped
///         under a doc that already read as a coverage promise — which is why
///         the promise is now a computed inventory rather than a sentence:
///         <c>PromptCacheKeyCoverageRules</c> reads this file and
///         <c>SystemPromptBuilder</c> and fails when the two disagree.
///     </para>
///     <para>
///         <b>Why "render" and not "run" — the evidence, since this is the
///         question #815 opened over.</b> Four things in the code already decide
///         it, and none of them is a preference:
///         <list type="number">
///             <item>
///                 The one exclusion this class has ever documented is justified
///                 in terms of the renderer: permission-rule edits that keep the
///                 resolved tool set identical do not move the key,
///                 "acceptable, because the default builder renders tools, not
///                 rules". A fingerprint of the run would have no reason to
///                 consult the renderer.
///             </item>
///             <item>
///                 The key holds no <c>Permission</c>, <c>MaxSteps</c>,
///                 <c>IsSubAgent</c>, <c>Hidden</c> or
///                 <c>ToolTimeoutSeconds</c> — the fields that decide what a
///                 RUN is. (<c>Permission</c> reaches the key indirectly, as the
///                 resolved tool set.) A run fingerprint would have started
///                 there.
///             </item>
///             <item>
///                 The key holds <c>ToolDescriptor.Schema</c>, which the builder
///                 never reads. Over-keying is therefore already accepted here,
///                 on the same "one rebuild" grounds #792 argued for.
///             </item>
///             <item>
///                 "the key must be complete rather than merely good enough for
///                 one turn's inputs" measures completeness against the render,
///                 because the render is the thing a hit has to reproduce.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>The inventory, both directions.</b> Rendered and keyed: eight
///         context chains (<c>WorkingDirectory</c>, <c>Model.ProviderId</c>,
///         <c>Model.Id</c>, <c>Agent.SystemPromptAppend</c>, <c>Tools</c>,
///         <c>Skills</c>, <c>ContextFiles</c>, <c>McpInstructions</c>), plus the
///         tool, skill and file members reached through locals. Keyed and NOT
///         rendered: seven agent fields — <c>Name</c>, <c>DisplayName</c>,
///         <c>Description</c>, <c>Model</c>, <c>ProviderId</c>,
///         <c>Temperature</c>, <c>ReasoningEffort</c>. Seven, not eight: #815
///         reported "eight members the builder never reads" and then listed
///         seven — the eighth <c>AppendField</c> in that block is
///         <c>SystemPromptAppend</c>, which the builder does read. None of the
///         seven can serve a stale prompt; each is declared with its reason in
///         <c>PromptCacheKeyCoverageRules</c> so the surplus stays a maintained
///         inventory rather than an accident.
///     </para>
///     <para>
///         <b><c>Model.ProviderId</c> and <c>Agent.ProviderId</c> are the same
///         value and not the same source.</b> That is the whole of #815. The
///         environment section renders the MODEL's provider; this key used to
///         hold the AGENT's. They agree in-tree because the model is resolved
///         <i>through</i> the agent's provider —
///         <c>TurnRunner.ResolveModelAsync</c> parses <c>agent.ProviderId</c>,
///         fetches that provider's catalog and picks the entry whose id is
///         <c>agent.Model</c> — and every catalog stamps
///         <c>ModelInfo.ProviderId</c> with the fetching provider's own registry
///         id, at four independent sites: <c>ProviderConfig.ParseModel</c>
///         (<c>config.Id</c>), <c>OllamaLlmClient</c> (a literal), and the
///         hardcoded <c>AnthropicModels</c> / <c>OpenAIModels</c> catalogs.
///     </para>
///     <para>
///         <b>So it is latent, and it is a trap rather than a fixed bug.</b> No
///         stale hit is reachable today: within one process
///         <c>Model.ProviderId</c> is a function of <c>Agent.ProviderId</c>
///         alone, so two contexts with equal keys have equal rendered
///         providers. The coupling lives in four stamping sites, none of them the
///         agent's field, and nothing type-checks or tests it — a catalog that
///         stamped anything but its own registry id would collide silently, and
///         the key holding the agent's provider is what made the block read as
///         covered on sight (#792's lesson, in a second guise).
///     </para>
///     <para>
///         <b>What this contract does NOT cover — and what closes the gap:
///         ambient reads.</b> Everything above is a requirement on the KEY, and
///         it holds only if the render's inputs are context members. That is a
///         second requirement, on the RENDER, and it was in no file until #814.
///         <c>SystemPromptBuilder</c> had two ambient reads:
///         <c>GetOsShort()</c>, a process constant a process-long cache cannot
///         serve wrong, and <c>DateTimeOffset.UtcNow</c>, a clock. A clock is not
///         a context member, so no amount of key coverage reaches it — which is
///         why the two requirements are not rivals and neither subsumes the
///         other: covering the context is necessary and NOT sufficient on its
///         own, and a pure render is necessary and not sufficient on its own.
///         #841 took the second of the two options named here — drop the line,
///         rather than inject a <c>TimeProvider</c> and key the date — and
///         <c>PromptClockPurityRule</c> now states and enforces the render side:
///         an <see cref="ISystemPromptBuilder" /> implementation reads no clock,
///         ambient or injected, so the prompt is a function of its context. With
///         the inventory above, the key holds every input the render reads and
///         the render reads nothing else — which is what makes a hit correct
///         rather than merely likely to be. The asymmetry is untouched by this:
///         over-keying still costs one rebuild, and the seven surplus agent
///         fields stay declared rather than trimmed.
///     </para>
/// </remarks>
public sealed class CachingSystemPromptBuilder(ISystemPromptBuilder inner) : ISystemPromptBuilder
{
    private const char Separator = '\u001f';
    private static readonly byte[] SeparatorBytes = [(byte)Separator];

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
    ///     Derive a deterministic cache key from every context component:
    ///     SHA-256 streamed over the field spans. Every field's UTF-8 encoding
    ///     is appended to one <see cref="IncrementalHash" /> through a bounded
    ///     stack buffer, so the turn pays a single hash pass over the context
    ///     bytes and never materializes the joined prompt text or a whole-context
    ///     byte array (#1532). The separator byte after each field keeps the
    ///     boundary the old separator-joined dump supplied, so arg-boundary
    ///     ambiguity is unchanged.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two model fields sit next to each other because they are rendered as
    ///         one line: <c>- Model: &lt;provider&gt;/&lt;id&gt;</c>
    ///         (<c>SystemPromptBuilder</c>, the Environment section). #815 keyed the
    ///         agent's provider here and not the model's, and with the pair adjacent the
    ///         block still read as coverage. <c>PromptCacheKeyCoverageRules</c> compares
    ///         both chains against the renderer, so the next mismatch cannot land quietly.
    ///     </para>
    ///     <para>
    ///         The tool schemas used to ride a static <c>ConcurrentDictionary</c> that
    ///         cached raw schema text per <c>JsonDocument</c> REFERENCE. A fresh document
    ///         per tool resolution can never hit an equal-reference key, so that map
    ///         never once served a hit and grew one entry per distinct document instance
    ///         for the decorator's process-long lifetime. #1532 hashes the schema
    ///         directly per turn (see <see cref="AppendSchemaField" />) and the map is
    ///         gone; a value-keyed replacement is deliberately not reintroduced — the
    ///         only stable value a <c>JsonDocument</c> can carry is its own text, and
    ///         extracting that text is the per-turn cost the map existed to avoid. The
    ///         cache surface that remains is the one that earns its keep: the prompt
    ///         cache itself, keyed by this hash.
    ///     </para>
    ///     <para>
    ///         Keys from this builder do not byte-match keys produced before this
    ///         rewrite: schemas now hash as canonical JSON rather than raw text, and the
    ///         fields are chunk-encoded rather than joined. That is safe — the prompt
    ///         cache is in-memory for one decorator instance and there is no persisted
    ///         key — and it lands as one sole extra rebuild for whatever distinct
    ///         contexts run before the decorator's next disposal, not as a missed hit.
    ///     </para>
    /// </remarks>
    private static string ComputeKey(SystemPromptContext context)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var schemaBuffer = new ArrayBufferWriter<byte>(256);
        using var schemaWriter = new Utf8JsonWriter(schemaBuffer, new JsonWriterOptions { SkipValidation = true });

        AppendField(hash, context.Agent.Name.Value);
        AppendField(hash, context.Agent.DisplayName);
        AppendField(hash, context.Agent.Description);
        AppendField(hash, context.Agent.Model);
        // Over-keyed, and declared as such in PromptCacheKeyCoverageRules: the
        // agent's provider is never rendered. It stays because dropping it is not
        // what fixes #815 — adding the model's is — and because it is the cheapest
        // per-agent cache separation available.
        AppendField(hash, context.Agent.ProviderId);
        AppendField(hash, context.Agent.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(hash, context.Agent.ReasoningEffort?.ToString());
        AppendField(hash, context.Agent.SystemPromptAppend);
        AppendField(hash, context.Model.ProviderId);
        AppendField(hash, context.Model.Id);

        var tools = context.Tools;
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            AppendField(hash, t.Name.Value);
            AppendField(hash, t.Description);
            AppendSchemaField(hash, t.Schema, schemaWriter, schemaBuffer);
            AppendField(hash, t.PromptSnippet);
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
                AppendField(hash, guidelines[g]);
            }
        }

        var files = context.ContextFiles;
        for (int i = 0; i < files.Count; i++)
        {
            AppendField(hash, files[i].Path);
            AppendField(hash, files[i].Content);
        }

        var skills = context.Skills;
        for (int i = 0; i < skills.Count; i++)
        {
            AppendField(hash, skills[i].Name);
            AppendField(hash, skills[i].Description);
            AppendField(hash, skills[i].FilePath);
        }

        AppendField(hash, context.McpInstructions);
        AppendField(hash, context.WorkingDirectory);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    ///     Append one field plus a separator byte; null fields collapse to a bare
    ///     separator. The field's UTF-8 encoding reaches the hash through bounded
    ///     chunks — a context file can be arbitrarily large, and the turn must not
    ///     hold its entire encoding in memory just to hash it (#1532).
    /// </summary>
    private static void AppendField(IncrementalHash hash, string? field)
    {
        AppendChars(hash, field);
        hash.AppendData(SeparatorBytes);
    }

    /// <summary>
    ///     Encode <paramref name="chars" /> into the hash in bounded chunks: never
    ///     more than <see cref="MaxChunkBytes" /> encoded bytes exist at a time,
    ///     whatever the field size. Chunk boundaries avoid splitting surrogate
    ///     pairs, so a pair cuts with the chunk it belongs to; every split is
    ///     deterministic from the field's own length, so the same field hashes to
    ///     the same bytes on every turn.
    /// </summary>
    /// <remarks>
    ///     <see cref="Encoding.UTF8" /> emits at most three UTF-8 bytes for one
    ///     <c>char</c> (rounded up), which pins <see cref="MaxChunkBytes" /> to
    ///     <c>GetMaxByteCount(MaxChunkChars)</c>; the span target of
    ///     <c>Encoding.GetBytes</c> throws on a too-small buffer, hence the bound.
    /// </remarks>
    private static void AppendChars(IncrementalHash hash, ReadOnlySpan<char> chars)
    {
        // Hoisted out of the loop on purpose: inside the loop body it would
        // re-stackalloc on every iteration (see EscapeCodeStrategy for the same
        // discipline in this codebase).
        Span<byte> scratch = stackalloc byte[MaxChunkBytes];
        while (chars.Length > 0)
        {
            int take = Math.Min(chars.Length, MaxChunkChars);
            if (take < chars.Length && take > 1 && char.IsHighSurrogate(chars[take - 1]))
            {
                take--; // keep the pair with the chunk it belongs to
            }

            int written = Encoding.UTF8.GetBytes(chars[..take], scratch);
            hash.AppendData(scratch[..written]);
            chars = chars[take..];
        }
    }

    /// <summary>
    ///     Hash one tool schema in place: <see cref="JsonElement.WriteTo" /> streams
    ///     canonical UTF-8 JSON into a reused <see cref="ArrayBufferWriter" />, so a
    ///     turn builds no per-schema raw-text string and keeps no per-schema cache
    ///     entry (#1532).
    /// </summary>
    /// <remarks>
    ///     Canonical JSON is a deliberate rep of the raw text the schema dump used to
    ///     read: two documents print equal bytes when their value trees are equal,
    ///     regardless of the whitespace their literal text carried — a stronger
    ///     notion of "same schema" for a key than raw text had.
    /// </remarks>
    private static void AppendSchemaField(
        IncrementalHash hash,
        JsonDocument schema,
        Utf8JsonWriter writer,
        ArrayBufferWriter<byte> buffer)
    {
        buffer.Clear();
        writer.Reset(buffer);
        schema.RootElement.WriteTo(writer);
        writer.Flush();
        hash.AppendData(buffer.WrittenSpan);
        hash.AppendData(SeparatorBytes);
    }

    /// <summary>
    ///     Bounded-encoding knobs for <see cref="AppendField" />:
    ///     <see cref="MaxChunkChars" /> source characters encode under
    ///     <see cref="MaxChunkBytes" /> target bytes, per the
    ///     <c>Encoding.UTF8.GetMaxByteCount</c> ceiling (<c>(chars + 1) × 3</c>).
    /// </summary>
    private const int MaxChunkChars = 80;
    private const int MaxChunkBytes = (MaxChunkChars + 1) * 3;
}
