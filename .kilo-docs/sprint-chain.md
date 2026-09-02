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

sprint|renderer-unification|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/renderer-unification/prompt.md
sprint|multi-agent|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/multi-agent/prompt.md
sprint|performance|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/performance/prompt.md
sprint|ide-integration|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/ide-integration/prompt.md
sprint|security|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/security/prompt.md
sprint|release-engineering|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/release-engineering/prompt.md
sprint|testing-strategy|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/testing-strategy/prompt.md

sprint|renderer-moat|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/renderer-moat/prompt.md
sprint|osc-expansion|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/osc-expansion/prompt.md
sprint|design-system-product|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/design-system-product/prompt.md
sprint|codegen-boilerplate|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/codegen-boilerplate/prompt.md
sprint|mascot-brand|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/mascot-brand/prompt.md
sprint|demo-gif|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/demo-gif/prompt.md
sprint|ci-cd-maturity|~deepseek/deepseek-v4-flash-latest|.kilo-docs/sprints/ci-cd-maturity/prompt.md
