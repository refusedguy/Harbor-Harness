# Harbor sprint chain — next sprint dispatcher

Очередь спринтов в порядке выполнения.
Каждый спринт = зона работ + промпт для кило.

FORMAT:
sprint|NAME|MODEL|PROMPT_PATH

- все поля обязательные
- MODEL — полный ID с провайдером, без этого кило не стартует
- PROMPT_PATH — путь к файлу с промптом относительно репо

sprint|UI-FINAL|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-ui-final.md
sprint|UI-V2|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-ui-v2.md
sprint|Multi-Agent|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-multi-agent.md
sprint|Performance|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-performance.md
sprint|IDE Integration|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-ide-integration.md
sprint|Security & Sandboxing|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-security.md
sprint|Release Engineering|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-release-engineering.md
sprint|Testing Strategy|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-testing.md
