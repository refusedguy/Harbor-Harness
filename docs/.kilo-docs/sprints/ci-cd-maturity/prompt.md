# CI/CD Maturity — довести GitHub Actions до production качества

## Цель
Сделать CI быстрым, надежным и информативным: сборка, тесты, coverage, perf gate, артефакты, релизы.

## Текущее состояние
- Есть 1 workflow: `.github/workflows/renderer-perf-gate.yml`
- Он падает на GitHub Actions: `Zero tests ran`, exit code 5
- Нет стандартного build/test workflow для всего решения
- Нет matrix builds, artifact caching, test sharding, publish steps

## Задачи
1. **Исправить perf-gate workflow** — разобраться почему TUnit тесты не запускаются на GitHub Actions. Возможные причины: `[NotInParallel]`, фильтры, необходимость `--filter`. Acceptance: workflow green на push в dev.
2. **Добавить стандартный CI workflow** — build + test всего решения на ubuntu-latest, с NuGet кэшем, артефактами сборки, summary.
3. **Matrix builds** — добавить windows-latest и macOS-latest для критических тестов (TUI, IPC).
4. **Test sharding** — разделить тесты на группы для параллельного запуска, сократить время CI.
5. **Artifact publishing** — выкладывать self-contained publish Harbor.App.Cli как артефакт workflow.
6. **Release workflow** — автоматический GitHub Release при теге v* с changelog, binaries для win-x64/linux-x64/linux-arm64.
7. **Benchmark CI job** — запускать `tests/Harbor.Benchmarks` на каждом PR с публикацией результатов.
8. **Dependabot** — включить авто-обновление NuGet пакетов.
9. **CodeQL / security** — добавить CodeQL анализ и secret scanning.

## Hard Rules
1. Один коммит = 1-2 файла. Не собирай гигантский дифф.
2. Все workflows должны работать на `ubuntu-latest` без self-hosted runners.
3. Используй `actions/cache@v4` для NuGet packages.
4. Не ломай существующий `renderer-perf-gate` workflow — только исправь.
5. После каждого коммита: локальный `bash -n` для YAML + `gh workflow run` для проверки.

## Deliverables
- `renderer-perf-gate.yml` работает на GitHub Actions
- Новый `ci.yml` workflow для всего решения
- `release.yml` workflow для автоматических релизов
- `benchmark.yml` workflow для бенчмарков
- `dependabot.yml` для NuGet
- HTML-отчет в `.kilo-docs/sprints/ci-cd-maturity/report.html`
