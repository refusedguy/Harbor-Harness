namespace Harbor.Core.Configuration;

/// <summary>
///     Default provider presets — no JSON authoring required for the user.
///     These are the "always available" defaults; user just picks one during onboarding.
/// </summary>
/// <remarks>
///     The presets list is the single source of truth for the onboarding wizard's
///     provider picker. New providers are added here in code (not via JSON) to keep
///     the onboarding UX stable and discoverable.
/// </remarks>
public static class ProviderPresets
{
    /// <summary>All builtin provider presets, ordered by onboarding recommendation.</summary>
    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("kilocode", "Kilo Code Gateway", "Multi-provider gateway with FREE models (tencent/hy3:free)", "tencent/hy3:free", true, "KILO_API_KEY", "Get a free key at https://kilo.ai"),
        new Preset("anthropic", "Anthropic (Claude)", "Direct Anthropic API — Claude Opus, Sonnet, Haiku", "claude-sonnet-4-20250514", true, "ANTHROPIC_API_KEY", "Get a key at https://console.anthropic.com"),
        new Preset("openai", "OpenAI (GPT)", "Direct OpenAI API — GPT-4o, o3, o4-mini", "gpt-4o", true, "OPENAI_API_KEY", "Get a key at https://platform.openai.com"),
        new Preset("openrouter", "OpenRouter", "Multi-provider router with 200+ models", "anthropic/claude-3.5-sonnet", true, "OPENROUTER_API_KEY", "Get a key at https://openrouter.ai"),
        new Preset("deepseek", "DeepSeek", "DeepSeek V3 and R1 (reasoning)", "deepseek-chat", true, "DEEPSEEK_API_KEY", "Get a key at https://platform.deepseek.com"),
        new Preset("groq", "Groq", "Ultra-fast LPU inference (Llama, Mixtral)", "llama-3.3-70b-versatile", true, "GROQ_API_KEY", "Get a key at https://console.groq.com"),
        new Preset("mistral", "Mistral AI", "Mistral Large, Codestral, Pixtral", "mistral-large-latest", true, "MISTRAL_API_KEY", "Get a key at https://console.mistral.ai"),
        new Preset("xai", "xAI (Grok)", "Grok models from xAI", "grok-2-latest", true, "XAI_API_KEY", "Get a key at https://x.ai"),
        new Preset("together", "Together AI", "Hosted open-source models", "meta-llama/Llama-3.3-70B-Instruct-Turbo", true, "TOGETHER_API_KEY", "Get a key at https://api.together.xyz"),
        new Preset("fireworks", "Fireworks AI", "Fast inference for open-source models", "accounts/fireworks/models/llama-v3p1-70b-instruct", true, "FIREWORKS_API_KEY", "Get a key at https://fireworks.ai"),
        new Preset("cerebras", "Cerebras", "Cerebras ultra-fast inference", "llama3.1-70b", true, "CEREBRAS_API_KEY", "Get a key at https://cloud.cerebras.ai"),
        new Preset("ollama", "Ollama (local)", "Local LLM inference — no API key needed", "llama3.2", false, null, "Install from https://ollama.ai and run `ollama serve`"),
        new Preset("vllm", "vLLM (local)", "Local vLLM server — no API key needed", "meta-llama/Llama-3.2-1B-Instruct", false, null, "Run `vllm serve <model>`")
    };

    /// <summary>Find a preset by id (case-insensitive). Returns <see langword="null" /> if not found.</summary>
    /// <param name="id">The provider id to look up.</param>
    /// <returns>The matching preset, or <see langword="null" />.</returns>
    public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Default presets that work without an API key (local providers).</summary>
    /// <returns>A list of presets with <see cref="Preset.RequiresApiKey" /> = <see langword="false" />.</returns>
    public static IReadOnlyList<Preset> GetNoAuth() => All.Where(p => !p.RequiresApiKey).ToList();

    /// <summary>A provider preset record.</summary>
    /// <param name="Id">Stable lowercase provider id.</param>
    /// <param name="DisplayName">Human-readable name shown in onboarding.</param>
    /// <param name="Description">One-line description shown when the user types <c>list</c> in onboarding.</param>
    /// <param name="DefaultModel">Default model id for this provider.</param>
    /// <param name="RequiresApiKey">Whether the provider needs an API key.</param>
    /// <param name="EnvVarName">Optional preset env var name (e.g. <c>KILO_API_KEY</c>).</param>
    /// <param name="SetupHint">Optional setup hint URL/message shown in onboarding.</param>
    public sealed record Preset(
        string Id,
        string DisplayName,
        string Description,
        string DefaultModel,
        bool RequiresApiKey,
        string? EnvVarName,
        string? SetupHint);
}
