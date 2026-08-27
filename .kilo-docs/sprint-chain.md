# Harbor sprint chain — next sprint dispatcher

Очередь спринтов в порядке выполнения.
Каждый спринт = зона работ + промпт для кило.

FORMAT:
sprint|NAME|MODEL|PROMPT_PATH

- все поля обязательные
- MODEL — полный ID с провайдером, без этого кило не стартует
- PROMPT_PATH — путь к файлу с промптом относительно репо

sprint|UI-FINAL|openrouter/z-ai/glm-5.3-flash|.kilo-docs/agent-prompts/kilo-prompt-ui-final.md
