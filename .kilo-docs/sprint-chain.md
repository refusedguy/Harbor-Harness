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

sprint|renderer-unification|kilo-auto/free|.kilo-docs/sprints/renderer-unification/prompt.md
sprint|multi-agent|kilo-auto/free|.kilo-docs/sprints/multi-agent/prompt.md
sprint|performance|kilo-auto/free|.kilo-docs/sprints/performance/prompt.md
sprint|ide-integration|kilo-auto/free|.kilo-docs/sprints/ide-integration/prompt.md
sprint|security|kilo-auto/free|.kilo-docs/sprints/security/prompt.md
sprint|release-engineering|kilo-auto/free|.kilo-docs/sprints/release-engineering/prompt.md
sprint|testing-strategy|kilo-auto/free|.kilo-docs/sprints/testing-strategy/prompt.md

sprint|renderer-moat|kilo-auto/free|.kilo-docs/sprints/renderer-moat/prompt.md
sprint|osc-expansion|kilo-auto/free|.kilo-docs/sprints/osc-expansion/prompt.md
sprint|design-system-product|kilo-auto/free|.kilo-docs/sprints/design-system-product/prompt.md
sprint|mascot-brand|kilo-auto/free|.kilo-docs/sprints/mascot-brand/prompt.md
sprint|demo-gif|kilo-auto/free|.kilo-docs/sprints/demo-gif/prompt.md
sprint|ci-cd-maturity|kilo-auto/free|.kilo-docs/sprints/ci-cd-maturity/prompt.md
