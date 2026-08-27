# Harbor sprint chain — next sprint dispatcher

Очередь спринтов в порядке выполнения.
Каждый спринт = папка в .kilo-docs/sprints/<name>/ с prompt.md + status.json.

FORMAT:
sprint|NAME|MODEL|PROMPT_PATH

- все поля обязательные
- MODEL — полный ID с провайдером, без этого кило не стартует
- PROMPT_PATH — путь к prompt.md относительно репо

sprint|UI-FINAL|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/ui-final/prompt.md
sprint|UI-V2|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/ui-v2/prompt.md
sprint|Multi-Agent|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/multi-agent/prompt.md
sprint|Performance|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/performance/prompt.md
sprint|IDE Integration|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/ide-integration/prompt.md
sprint|Security & Sandboxing|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/security/prompt.md
sprint|Release Engineering|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/release-engineering/prompt.md
sprint|Testing Strategy|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/testing-strategy/prompt.md
