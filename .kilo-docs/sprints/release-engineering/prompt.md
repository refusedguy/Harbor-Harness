Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Release Engineering.

## Context
Harbor использует kilo-chain для автономных спринтов (.kilo-docs/sprint-chain.md + scripts/sprint-chain.sh).
Очередь спринтов уже заполнена (6 спринтов без prompt.md), но диспатчер всё ещё требует ручных шагов:
checkout, apply prompt, run tests, commit.

Цель — полностью автоматизировать конвейер: от "спринт в очереди" до "CI зелёный, статус записан"
без человеческого участия. Дополнительно: zero-warning build gate, arch-test pre-commit hook,
automatic release notes.

## Tasks (выполняй по порядку)

1. **Полная автоматизация sprint-chain.sh.**
   Скрипт читает .kilo-docs/sprint-chain.md, находит HEAD очередного спринта, проверяет чистоту
   рабочей директории (git status --porcelain), создаёт feature branch, копирует prompt.md в sprint dir,
   запускает kilo dispatch через DISPATCH="$HOME/.hermes/skills/autonomous-ai-agents/kilo-dispatch/scripts/kilo-dispatch.sh",
   ждёт завершения, пишет status.json (pass/fail counts, commit SHA, model used, duration, log excerpt).
   Acceptance: sprint-chain.sh стартует с пустым аргументом и завершает весь цикл без ручных команд.

2. **Zero-warning arch-test gate в build.**
   Добавь BannedSymbols.txt analyzer + arch-test runner в Directory.Build.props так, чтобы
   dotnet build -c Release падал если: (a) есть banned symbol (GetResult, Result.Value на invalid input),
   (b) arch tests не проходят.
   Acceptance: любые из 46 architecture tests regress → build fails на CI, не дожидаясь PR review.

3. **Pre-commit hook (git alias `harbor-check`).**
   Реализуй hook, который запускает dotnet test tests/Harbor.Architecture.Tests/ -c Release --no-build
   + dotnet test tests/<Project> -c Release --no-build для всех changed projects
   (определяются по git diff --name-only). При любом fail — блокирует коммит.
   Acceptance: git commit после сломания arch теста — abort с понятным сообщением.

4. **Автоматические release notes.**
   По завершении спринта: extract sprint name + задачи из prompt.md, collect git log since last sprint tag,
   write в .kilo-docs/sprints/<name>/status.json + append section в CHANGELOG.md.
   Acceptance: каждый завершённый спринт имеет status.json с задачами, результатами, метриками,
   и CHANGELOG.md содержит запись.

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. sprint-chain.sh НИКОГДА не rm -rf и не трогает файлы вне sprint dir. Fail-safe: всё работает в изолированной feature branch, master остаётся untouched пока спринт не approved.
3. Arch-test gate — compile-time, не runtime. Анализаторы (AdditionalFiles), не shell scripts.
4. Pre-commit hook — fast (< 30s), иначе разработчик будет его отключать. Кэшируй build output.
5. После каждого коммита: dotnet build -c Release + dotnet test tests/Harbor.Architecture.Tests/ -c Release --no-build.

## Deliverables
- Fully automated sprint-chain.sh с status.json generation
- Zero-warning arch-test gate в Directory.Build.props (BannedSymbols + arch tests)
- Pre-commit hook `harbor-check` (fast, per-project test run)
- Automatic release notes pipeline (status.json + CHANGELOG)
- report.html в `.kilo-docs/sprints/release-engineering/report.html`
