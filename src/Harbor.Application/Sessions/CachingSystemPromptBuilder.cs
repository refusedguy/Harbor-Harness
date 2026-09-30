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
    /// <remarks>
    ///     The two model fields sit next to each other because they are rendered as
    ///     one line: <c>- Model: &lt;provider&gt;/&lt;id&gt;</c>
    ///     (<c>SystemPromptBuilder</c>, the Environment section). #815 keyed the
    ///     agent's provider here and not the model's, and with the pair adjacent the
    ///     block still read as coverage. <c>PromptCacheKeyCoverageRules</c> compares
    ///     both chains against the renderer, so the next mismatch cannot land quietly.
    /// </remarks>
    private static string ComputeKey(SystemPromptContext context)
    {
        var sb = new StringBuilder(512);
        AppendField(sb, context.Agent.Name.Value);
        AppendField(sb, context.Agent.DisplayName);
        AppendField(sb, context.Agent.Description);
        AppendField(sb, context.Agent.Model);
        // Over-keyed, and declared as such in PromptCacheKeyCoverageRules: the
        // agent's provider is never rendered. It stays because dropping it is not
        // what fixes #815 — adding the model's is — and because it is the cheapest
        // per-agent cache separation available.
        AppendField(sb, context.Agent.ProviderId);
        AppendField(sb, context.Agent.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(sb, context.Agent.ReasoningEffort?.ToString());
        AppendField(sb, context.Agent.SystemPromptAppend);
        AppendField(sb, context.Model.ProviderId);
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
