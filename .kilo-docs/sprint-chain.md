# Harbor sprint chain — next sprint dispatcher

Очередь спринтов в порядке выполнения.
Каждый спринт = папка в .kilo-docs/sprints/<name>/ с prompt.md + status.json.

FORMAT:
sprint|NAME|MODEL|PROMPT_PATH

- все поля обязательные
- MODEL — полный ID с провайдером, без этого кило не стартует
- PROMPT_PATH — путь к prompt.md относительно репо
- NAME — без пробелов (lowercase-hyphen): из NAME строится путь .kilo-docs/sprints/<name>/status.json,
  иначе диспетчер не находит status.json и не может пропустить выполненный спринт (re-dispatch loop)

sprint|renderer-unification|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/renderer-unification/prompt.md
sprint|multi-agent|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/multi-agent/prompt.md
sprint|performance|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/performance/prompt.md
sprint|ide-integration|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/ide-integration/prompt.md
sprint|security|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/security/prompt.md
sprint|release-engineering|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/release-engineering/prompt.md
sprint|testing-strategy|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/testing-strategy/prompt.md

sprint|renderer-moat|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/renderer-moat/prompt.md
sprint|osc-expansion|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/osc-expansion/prompt.md
sprint|design-system-product|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/design-system-product/prompt.md
sprint|codegen-boilerplate|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/codegen-boilerplate/prompt.md
sprint|mascot-brand|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/mascot-brand/prompt.md
sprint|demo-gif|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/demo-gif/prompt.md
sprint|ci-cd-maturity|openrouter/z-ai/glm-5.3-flash|.kilo-docs/sprints/ci-cd-maturity/prompt.md
