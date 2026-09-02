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

sprint|renderer-unification|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/renderer-unification/prompt.md
sprint|multi-agent|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/multi-agent/prompt.md
sprint|performance|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/performance/prompt.md
sprint|ide-integration|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/ide-integration/prompt.md
sprint|security|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/security/prompt.md
sprint|release-engineering|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/release-engineering/prompt.md
sprint|testing-strategy|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/testing-strategy/prompt.md

sprint|renderer-moat|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/renderer-moat/prompt.md
sprint|osc-expansion|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/osc-expansion/prompt.md
sprint|design-system-product|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/design-system-product/prompt.md
sprint|codegen-boilerplate|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/codegen-boilerplate/prompt.md
sprint|mascot-brand|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/mascot-brand/prompt.md
sprint|demo-gif|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/demo-gif/prompt.md
sprint|ci-cd-maturity|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/ci-cd-maturity/prompt.md
sprint|avalonia|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/avalonia/prompt.md
sprint|ui-v2-hotfix|kilo/cohere/north-mini-code:free|.kilo-docs/sprints/ui-v2-hotfix/prompt.md
