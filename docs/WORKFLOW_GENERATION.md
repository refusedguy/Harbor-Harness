# Workflow-файлы: что остаётся в YAML и как это устроить после переезда на fallout

**Статус:** исследование. Ни одна строка в `.github/workflows/**` этим документом не меняется.
**Issue:** [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185).
**Владелец решения:** переезд сборщика с NUKE на [fallout](https://docs.fallout.build/) подтверждён; этот документ отвечает на вопрос «что останется в YAML и как это устроить», а не «писать ли генератор в NUKE».
**Все числа измерены на `dev` @ `a55ea18c`** (коммит указан везде, где важно). Числа, которые являются проекцией, помечены словом «проекция».
**Дополнение:** §8 разбирает вариант, которого в первом издании не было, — разбиение `ci.yml` на reusable workflow'и через `workflow_call`; §9 — независимая перепроверка на следующем дереве (`dev` @ `7d86fd0c`): три числа исправлены, одно не воспроизвелось, и добавлены две величины, которых в отчёте не было (минимальная YAML-обвязка и измеренный набор composite actions).

---

## 0. Ответ в одной строке

**Fallout умеет генерировать `.github/workflows/*.yml` — но его модель джоба не описывает наш CI: выражаются 4 джоба из 62, а пол нашей CI вырастает с 9 656 строк до 4 215, а не до 20 %.** Поэтому: **генерировать workflow-файлы не надо**; надо перенести в C# **списки**, а скелеты джобов оставить ручными. Отдельно: после переезда на fallout стоит завести **один** генерируемый workflow-файл того вида, который fallout моделирует хорошо (см. §7), и держать остальные 16 ручными — ровно так сделано в самом репозитории fallout. Четвёртый вариант — **разбить `ci.yml` на reusable workflow'и через `workflow_call`** — разобран в §8: он не выигрывает ничего (у нас нет второго вызывающего) и переименовывает все восемь обязательных проверок разом.

---

## 1. Что умеет fallout (факты, со ссылками)

### 1.1 Да, генерирует

Страница [docs.fallout.build/docs/cicd/github-actions](https://docs.fallout.build/docs/cicd/github-actions), раздел «Configuration Generation», цитата дословно:

> You can generate [workflow files](https://docs.github.com/en/actions/reference/workflow-syntax-for-github-actions) from your existing target definitions by adding the `GitHubActions` attribute. For instance, you can run the `Compile` target on every push with the latest Ubuntu image:

И ниже:

> Whenever you make changes to the attribute, you have to run the build at least once to regenerate the workflow file.

В исходниках это видно буквально — `src/Fallout.Common/CI/GitHubActions/GitHubActionsAttribute.cs`:

```csharp
public override AbsolutePath ConfigurationFile
    => Build.RootDirectory / ".github" / "workflows" / $"{name}.yml";
public override IEnumerable<AbsolutePath> GeneratedFiles => new[] { ConfigurationFile };
```

То есть workflow-файл — **артефакт сборки**, а не рукопись. Ровно та же модель, что уже работает у нас с `docs/SEAM_GRAPH.md` (генерируется гейтом `gen-seam-graph`, сверяется гейтом `gen-seam-graph --check`).

### 1.2 Это не новость fallout — генератор есть и в NUKE

В сборке `Nuke.Common` (9.0.4, лежит в локальном кэше NuGet) присутствует `Nuke.Common.CI.GitHubActions.GitHubActionsAttribute` и весь набор типов `GitHubActionsConfiguration` / `GitHubActionsJob` / `GitHubActionsStep` / `GitHubActionsDetailedTrigger` / `GitHubActionsWorkflowDispatchTrigger`. То есть «NUKE генерирует workflow» — факт, и он не изменится при переезде.

Что **добавляет** fallout (проверено по исходникам и по `strings` на сборке NUKE):

| возможность | NUKE 9.0.4 | fallout |
|---|---|---|
| `[GitHubActions]` → `.github/workflows/<name>.yml` | есть | есть |
| триггеры (push/PR/dispatch, ветки, пути, cron) | есть | есть |
| `import secrets`, `GITHUB_TOKEN` | есть | есть |
| загрузка артефактов (`.Produces` → `actions/upload-artifact`) | есть | есть |
| **пининг версий actions и SHA-пины** (`CheckoutAction`, `CacheAction`, `SetupDotNetAction`, `UploadArtifactAction`) | нет | есть |
| **точка вставки своих шагов** `IConfigureGitHubActions.ConfigureSteps(pipeline)` | нет | есть |
| **шаг с `if:` / `env:` / `with:` / `shell:` / `continue-on-error`** (`GitHubActionsCustomStep`) | нет | есть |
| типизированные входы `workflow_dispatch` (`GitHubActionsInputAttribute` с типом и описанием) | нет (есть только `OnWorkflowDispatchOptionalInputs`/`RequiredInputs` — списки строк) | есть |
| `runs-on` по меткам раннера (`RunsOnLabels`), `defaults.run.shell`, `env:` на уровне workflow | нет | есть |

**Граница §2 не вина форка.** Модель джоба унаследована от NUKE без изменений: `GitHubActionsJob` в `nuke-build/nuke` (`source/Nuke.Common/CI/GitHubActions/Configuration/GitHubActionsJob.cs`) и в fallout имеют один и тот же набор выводимых ключей, различается только `RunsOnLabels`. То есть ограничение «нет `needs`/`if`/`permissions` на уровне джоба» — свойство предка, и переезд его не снимает и не усугубляет.

Вывод для владельца: **переезд на fallout не приносит генератор — он приносит точку вставки своих шагов и пининг actions.** Именно вставка шагов делает гибрид из §7 возможным.

### 1.3 Как fallout сам это применяет

Репозиторий fallout — готовый образец нужного нам гибрида. Из `.agents/skills/editing-ci-workflows/SKILL.md` (цитата):

> **`build.yml` and `build-cross-platform.yml` are generated** from `build/Build.CI.GitHubActions.cs` — edit the attributes/constants there and regenerate (`./build.sh`), never hand-edit those two `.yml` files. `build-skip.yml`, `publish-packages-preview.yml`, and `publish-packages-release.yml` are hand-written; those you do edit directly.

То есть **2 из 5** workflow-файлов в самом fallout сгенерированы, **3 — ручные**. И там же — причина, по которой файл с секретом остался ручным (`build/Build.CI.GitHubActions.cs`):

> The release workflow is intentionally hand-written … The NUKE attribute generator would force the two to match.

---

## 2. Граница: что модель job'а fallout может и чего не может

Модель джоба задаётся `src/Fallout.Common/CI/GitHubActions/Configuration/GitHubActionsJob.cs`. Класс имеет ровно эти поля, и `Write()` выводит ровно эти ключи:

| поле | ключ в YAML |
|---|---|
| `Name` | `<job>:` и `name:` |
| `Image` / `RunsOnLabels` | `runs-on:` |
| `TimeoutMinutes` | `timeout-minutes:` |
| `ConcurrencyGroup`, `ConcurrencyCancelInProgress` | `concurrency:` |
| `EnvironmentName`, `EnvironmentUrl` | `environment:` |
| `Steps` | `steps:` |

**Не выводится ничего из этого:** `needs:`, `if:` (на уровне джоба), `permissions:` (на уровне джоба), `strategy:`/`matrix:`, `outputs:` (на уровне джоба), `env:` (на уровне джоба), `continue-on-error:`, `container:`, `services:`, `secrets:`.

Ещё три ограничения, важные для нас:

1. **Имя джоба выводится из образа.** `GitHubActionsAttribute.GetJobs`: `Name = image.GetValue().Replace(".", "_")`. Имя джоба нельзя задать произвольно — оно всегда `ubuntu-latest`, `macos-latest` и т. п. (`GitHubActionsImage` не содержит arm64-образа вовсе; наш пул `ubuntu-24.04-arm` задаётся только через `RunsOnLabels`, а он требует ровно одного образа и запрещает матрицу: `Assert.True(RunsOnLabels.Length == 0 || images.Length == 1, …)`).
2. **Матрицы нет.** Число джобов = число образов, переданных в атрибут. `strategy:`/`matrix:` в модели нет.
3. **Точка расширения — только шаги.** `IConfigureGitHubActions.ConfigureSteps(GitHubActionsStepPipeline pipeline)` даёт «вставить шаги в сгенерированный джоб» и ничего больше: добавить джоб, задать `needs:` или переименовать джоб через неё нельзя.

### 2.1 Что из этого использует наш CI (измерено, `dev` @ `a55ea18c`)

| ключ джоба | сколько наших джобов его используют | есть в модели fallout? |
|---|---:|---|
| `permissions:` | **52** | нет (только на уровне workflow) |
| `if:` | **20** | нет |
| `needs:` | **18** | нет |
| `outputs:` | **7** | нет |
| `strategy:` (матрица) | **5** | нет |
| `env:` | 5 | нет |

**Выражаются в модели fallout 4 джоба из 62:** `evals.yml:live`, `goldens.yml:regenerate`, `meas-jit-aot.yml:measure`, `meas-jit-aot.yml:measure-aot`.

### 2.2 Граница, которую нельзя перешагнуть: required checks

`.github/rulesets/dev.json` и `.github/rulesets/master.json` требуют восемь **имён джобов** (это `name:` под джобом, а не ключ джоба):

`gate`, `public-api freeze`, `checker self-test`, `internal links`, `markdown lint`, `normative citations`, `tree gates`, `test project coverage`.

Все восемь сегодня существуют (`ci.yml:gate`, `ci.yml:public-api`, `docs.yml:selftest`, `docs.yml:links`, `docs.yml:lint`, `docs.yml:cites`, `docs.yml:tree`, `docs.yml:testprojects`; всего различных отображаемых имён джобов — 29).

**Ни одно из этих имён не является именем образа**, а имя джоба в модели fallout выводится из образа и не задаётся. Значит генератор fallout не может породить имя `gate` — а ruleset без `gate` не пускает мерж. Это не «неудобство», это блокер для варианта «сгенерировать всё».

Отдельно, измеренный факт: **сегодня ничто не проверяет, что эти восемь имён на месте.** Греп по всему дереву: `.github/rulesets/**` не читает ни один гейт, ни один скрипт, ни один workflow. Имена джобов — единственное, что связывает ruleset с workflow-файлами, и это единственная связь без гейта.

---

## 3. Минимальная YAML-обвязка — числа, не утверждения

### 3.1 Что вообще есть в 9 656 строках

Измерено на `dev` @ `a55ea18c`, все 17 файлов (построчный разбор, комментарии отделены от YAML):

| слой | строк | доля |
|---|---:|---:|
| комментарии | 4 724 | 48,9 % |
| YAML (непустые, некомментарные строки) | 4 514 | 46,8 % |
| пустые строки | 417 | 4,3 % |
| **итого** | **9 655** | 100 % |

Разбивка YAML по назначению:

| слой | строк | доля |
|---|---:|---:|
| тела шагов (`run:`/`uses:` и всё, что под ними) | 3 328 | 34,5 % |
| из них шаги-«скелеты» (`actions/checkout`, `setup-dotnet`, `cache`, `upload/download-artifact`) | 1 171 | 12,1 % |
| шапки джобов (ключ джоба, `runs-on:`, `needs:`, `if:`, `permissions:`, `strategy:`, `steps:`) | 733 | 7,6 % |
| шапки workflow (триггеры, `env:`, `permissions:`, `concurrency:`) | 453 | 4,7 % |

**Почти половина файлов — комментарии.** Это не украшение: в workflow-файлах этого репозитория живёт обоснование решений («WHAT IS NOT IN THIS FILE, AND WHY», таблицы измерений, «the arm64 twin is a separate step, not a second `if:` inside the first»). Генератор обязан либо унести эту прозу в C# (строковыми литералами в C#, где она оторвётся от YAML, который она объясняет), либо выбросить её.

### 3.2 Пол при генерации «всё в C#»

Считаем честный минимум: Fallout выводит наш скелет сам, а тела шагов остаются в YAML как `GitHubActionsCustomStep` (с тем же текстом `run:`).

Скелет на один джоб (по снимку из репозитория fallout, `tests/Fallout.Common.Specs/CI/ConfigurationGenerationSpecs.Test_testName=simple-triggers_attribute=GitHubActionsAttribute.verified.txt`):

```
<job>:            1     <- ключ джоба
  name:           1
  runs-on:        1
  steps:          1
  - uses: actions/checkout@v7                  1
  - name: 'Cache: …' + uses/with/path/key      8
  - name: 'Setup: .NET SDK' + uses/with        5
  - name: 'Restore: dotnet tools' + run        2
  - name: 'Run: …' + run (+ env)               2
                                      итого   22
```

| сценарий | строк | доля от 9 656 |
|---|---:|---:|
| сегодня | 9 656 | 100 % |
| **пол: всё в C#, скелет выводит fallout** (шапки файлов 453 + 62 × 7 ключей джоба + тела шагов 3 328) | **4 215** | **43,7 %** |
| тот же пол, если каждый из 362 шагов получает обёртку custom-step (`name:` + `run:`) | 4 939 | 51,1 % |
| **из них: скелет, который fallout выводит сам** (1 171 action-шагов + 434 ключей джобов) | **1 605** | **16,6 %** |
| из них: тела шагов, которые остаются в YAML | 3 328 (+724) | 34,5 % (+7,5 %) |
| комментарии, которые надо куда-то деть | 4 724 | 48,9 % |

**Читать это надо так.** Число «−80 % строк» не подтверждается: **пол — 43,7 %** (51,1 %, если считать обёртки custom-step), а не 20 %. И «оставшиеся 43,7 %» — это не «YAML, который исчез», это YAML, который переехал в C#-литералы и вернулся в YAML шагом `run:`. Настоящий выигрыш генератора — **1 605 строк (16,6 %)**, и то только для джобов, которые модель вообще может выразить (см. §2.1: у неё таких **4 из 62**).

И последнее, что делает этот «пол» недостижимым: для 58 джобов из 62 модель не выражает ни `needs:`, ни `if:`, ни `permissions:`, ни `strategy:`, ни `outputs:`, ни имя `gate`. Для них файл **не может быть сгенерирован** вовсе — генератор либо потеряет эти ключи, либо их придётся дописывать в YAML поверх сгенерированного, и тогда файл перестаёт быть артефактом сборки.

### 3.3 А сколько строк реально меняется при добавлении гейта

Это и есть настоящая боль, и она измерена.

**Рост файлов** (`dev`, полная история):

| файл | первая измеренная точка | 2026-10-22 | рост | коммитов, его тронувших |
|---|---:|---:|---:|---:|
| `ci.yml` | 264 (2026-09-01) | 3 484 | **×13,2** | 110 |
| `docs.yml` | 110 (2026-09-29) | 1 733 | ×15,8 | 78 |
| всего по `.github/workflows/**` | | 9 656 | | **229 коммитов** |

> **Исправление (перепроверка, §9).** В первом издании здесь стояло «455 коммитов». Число не воспроизводится: 455 — это примерно удвоенное 229. Проверенные команды на `dev` @ `a55ea18c`: `git log --oneline a55ea18c -- .github/workflows/` — **229** различных коммитов; `-- .github/**` — 233; сумма пофайловых счётчиков без `--follow` — 300 (это коммит-файлы: один коммит, тронувший три файла, посчитан трижды); по всем веткам `--all` — 681. Ни одна из пяти не даёт 455. Число 229 — правильное: это и есть «сколько раз менялись workflow-файлы».

**Число мест, которые надо тронуть, чтобы добавить ОДИН гейт** — по коммиту `197241087` (гейт `check-workflows`), 11 файлов:

| файл | что |
|---|---|
| `tools/gates/check-workflows/{main,flow,selftest}.go` | сам гейт |
| `tools/gates/main.go` | регистрация в реестре (по алфавиту) |
| `.github/workflows/docs.yml` | +39 строк: джоб гейта **и** шаг self-test в джобе `selftest` |
| `docs/CI_GATES.md` | строка таблицы гейтов (в ней же — колонки «Workflow», «Jobs», «Lane») |
| `tools/README.md` | строка таблицы |
| `tools/bin/harbor-gates-linux-x64`, `tools/bin/harbor-gates-linux-arm64`, `tools/bin/SHA256SUMS` | пересобранный бинарь |
| `changelog/fragments/<PR>.md` | фрагмент |

Коммит, добавивший сразу 15 гейтов (`e7479b9a5`), тронул **6 workflow-файлов**: `ci.yml`, `docs.yml`, `meas-analyzer-cost.yml`, `meas-build-vcpu.yml`, `nightly.yml`, `test-os.yml`.

**Точки вызова гейтов в workflow-файлах:** 31 различный гейт, **68 мест вызова**. Реально повторяющихся: `measure-build-cpu` — 8 (в 3 файлах), `changelog-assemble` — 4, затем шесть гейтов по 3.

> **Исправление (перепроверка, §9).** В первом издании здесь стояло «74 места вызова», и `all` — 5. Пересчёт построчно, **без строк-комментариев**, на том же дереве `a55ea18c` даёт **68** реальных мест вызова в 31 гейте: ещё 6 совпадений приходятся на прозу в комментариях. `all` вызывается **2** раза — один с `--self-test`, другой без; три остальных упоминания — комментарии (`docs.yml` объясняет, что список гейтов дерева берётся из бинаря). Ссылки на строки ниже даны по текущему дереву, а не по `a55ea18c`: `all --self-test` — `.github/workflows/docs.yml:659`, `all` — `.github/workflows/docs.yml:705`. Остальные числа §3 при перепроверке воспроизвелись.

---

## 4. Три варианта

Третий вариант из задания владельца — **разбить `ci.yml` на несколько workflow'ев через `workflow_call`** — разобран отдельно в §8: у него другая природа (это не генерация, а переиспользование), и другая граница (правило required checks).

| | **(а) Генерация в fallout** | **(б) Свой генератор в сборщике** | **(в) Без генератора: списки + composite actions** |
|---|---|---|---|
| Что делает | `[GitHubActions]` пишет `.github/workflows/<name>.yml` | свой код в `build/_build/` (после переезда — в `build/`) пишет YAML | один источник данных + генератор **списков**; скелеты джобов ручные |
| Покрытие наших 62 джобов | **4 из 62** (нет `needs`/`if`/`permissions`/`strategy`/`outputs`/`env` на уровне джоба; имя джоба выводится из образа, а required checks — нет) | сколько напишешь — столько и покроет, но это **ещё одна** реализация YAML-эмиттера | покрывает ровно то, что является **списком**: 245 строк уже покрыты (`tools/ci-project-lists.json`), потенциал — ещё несколько сотен |
| Выигрыш в строках | **−5 441 строк (56,3 %) в гипотетическом полном**, из которых **1 605 строк (16,6 %)** — реальный выигрыш генератора, а остальное переезжает в C#-литералы | тот же по величине, но без ограничений модели — и ценой второго YAML-эмиттера | **−0…−300 строк**, но −11 файлов мест правки на гейт |
| Выигрыш в местах правки | почти ноль: `needs:`/`if:`/`permissions:` всё равно придётся писать руками, и в YAML, а не в C# | ноль, если генерировать только списки | **−1 файл** из 11 при добавлении гейта, если вынести реестр «гейт → workflow → джоб → флаги» в источник данных |
| Риск | ошибка в генераторе убивает полосу целиком; имя джоба не может стать `gate` | тот же, плюс свой YAML-эмиттер без snapshot-тестов | низкий: гейт сверки ловит расхождение; сломанный YAML ломает один джоб, а не полосу |
| Читаемость результата | плохая: 4 724 строки объяснений уезжают в C#-литералы | та же проблема | **хорошая**: YAML остаётся YAML, комментарии остаются на месте |
| Прецедент в репозитории | нет (у fallout — да, 2 из 5 файлов) | нет | **есть и работает**: `tools/ci-project-lists.json` + `tools/ci-shard-slices.py` (`--write` / гейт / `--self-test`) |
| Совместимость с планом переезда | требует переезда; порядок: **сначала переезд, потом генерация** | требует переезда | не требует; работает на NUKE и на fallout одинаково |

### 4.1 Что уже сделано и работает (вариант (в) в действии)

`tools/ci-project-lists.json` — единый источник списков CI-проектов; `tools/ci-shard-slices.py` переписывает из него четыре производных места и падает, когда они разошлись. Измерено, сколько строк он занимает:

| производное место | строк |
|---|---:|
| `.github/workflows/ci.yml` — матрица `test` (списки шардов) | 38 |
| `.github/workflows/ci.yml` — срезы загрузки `solution-binaries-*` | 175 |
| `.github/workflows/ci.yml` — список `coverage` | 1 |
| `.github/workflows/nightly.yml` — список `coverage` | 20 |
| `.github/workflows/test-os.yml` — список OS и примечания | 12 |
| **всего генерируется** | **245** |

Механика — **точечная подстановка текста**, а не сборка YAML с нуля: `job_range()` находит тело джоба по отступу, `apply_edits()` заменяет диапазон строк (`tools/ci-shard-slices.py:286`, `:580`). Гейт при этом честно говорит, если не нашёл место, которое должен был проверить, вместо того чтобы «проверить ничто».

Это ровно тот образец, который стоит расширять: **генерировать списки, а не скелеты.**

---

## 5. Риски и меры

| # | риск | как он выглядит | мера | статус сегодня |
|---|---|---|---|---|
| R1 | **Сломанный YAML убивает полосу молча** | GitHub создаёт прогон с **0 джобов** и закрывает его как `failure` с текстом «This run likely failed because of a workflow file issue»; полоса не красная — её **нет** | гейт `check-workflows` (`tools/gates/check-workflows/`) ловит дубль ключа в маппинге, дубль под кавычками и не-list строку в списке; живёт в `docs.yml`, а **не** в `ci.yml` — гейт, живущий в сломанном файле, умирает вместе с ним | **есть**, покрывает три формы; истории: прогоны 39627731779 и 39627783779 (0 джобов) после `e7479b9a` |
| R2 | **Сгенерированный файл разошёлся с тем, что в git** | правило «генерируй и коммити» без сверки: CI проверяет сам себя и всегда зелёный | гейт-сверка `--check` рядом с `--write`, как у `gen-seam-graph` (`tools/gates/gen-seam-graph/run.go:192`, `checkOutput`) | **есть образец**, нет применения к workflow |
| R3 | **Имена джобов поехали → ruleset перестал пускать** | required check `gate` исчез → PR ждёт вечно, и никто не видит почему | гейт, читающий `.github/rulesets/*.json` и сверяющий каждый `context` с набором `name:` джобов во всех workflow-файлах | **ГЕЙТА НЕТ** — `.github/rulesets/**` не читает никто |
| R4 | **Генерация требует dotnet → «дерево здорово» нельзя проверить до сборки** | проверка порядка «дерево → сборка» не может опираться на сборщик | проверка должна быть **скомпилированным гейтом**, читающим закоммиченный YAML (как `check-workflows`), а не целью сборки | **есть** образец: `check-workflows` — Go, без dotnet |
| R5 | **Ошибка в генераторе останавливает всю полосу, а ошибка в ручном файле — один джоб** | при R1 генератор ломает файл целиком; руками — максимум один джоб | **не генерировать скелеты джобов**; ограничить генерацию списками, у которых есть независимая сверка | рекомендация §6 |
| R6 | **Триггеры, `permissions`, `secrets` и требуемые проверки — вне досягаемости C#** | `on:` — это контракт с GitHub; ruleset — контракт с настройкой репозитория | оставить в YAML; `permissions` — на уровне workflow (там модель fallout есть), per-job — в YAML | измерено: 52 джоба с per-job `permissions` |
| R7 | **Проза уезжает в C#** | 4 724 строки объяснений становятся строковыми литералами; читатель workflow-файла не видит ничего | не переносить; генератор не трогает комментарии (текстовая подстановка, как у `ci-shard-slices.py`, это умеет) | см. §3.1 |
| R8 | **Два нестабильных слоя сразу** (миграция сборщика + генерация) | переезд fallout меняет `build/`, генерация меняет `.github/workflows/**`; обе правки в одном PR — неразделимая диагностика | **порядок**: сначала переезд и его зелёный прогон, потом генерация отдельным PR | рекомендация §6 |
| R9 | **Разбиение на reusable workflow'и переименовывает все восемь required checks разом** | джоб внутри `workflow_call` сообщает свою проверку под именем `<вызывающий> / <вызываемый>`, а сам reusable workflow выбрать обязательной проверкой в ruleset нельзя | не делить `ci.yml`; если понадобится — делить **списки** (источник данных) и **шаги** (composite actions), а не файлы | измерено в §8.3; гейта, сверяющего правило с именами джобов, нет (см. R3 и issue #2191) |

---

## 6. Рекомендация

**Генерировать workflow-файлы не надо. Генерировать списки — надо, и это уже делается.**

Обоснование по фактам, а не по вкусу:

1. **Модель не подходит.** 4 джоба из 62 выражаются. `needs:` (18), `if:` (20), per-job `permissions:` (52), `outputs:` (7), `strategy:` (5), job-`env:` (5) в модели нет. А имя джоба выводится из образа — required check `gate` невозможно получить в принципе.
2. **Пол — 43,7 %, а не 20 %.** Тела шагов (3 328 строки) никуда не деваются, они переезжают в C#-литералы и возвращаются в YAML. Настоящий выигрыш генератора — **1 605 строк (16,6 %)**, и модель fallout даже её не может выразить для 58 из 62 джобов.
3. **Половина файлов — комментарии** (4 724 из 9 656). Генератор либо уносит их в C# (плохо), либо выбрасывает (хуже).
4. **Риск несимметричен.** Ошибка в генераторе убивает полосу (R1: 0 джобов, «failure», который никто не видит); ошибка в ручном файле стоит одного джоба. Правило репозитория уже требует, чтобы гейт жил **в другой полосе**, чем тот, который polices («a gate that lives inside the file it polices disappears together with it» — `tools/gates/check-workflows/main.go`).
5. **Прецедент, который работает, — это генерация списков**, и он уже в дереве: `tools/ci-project-lists.json` + `ci-shard-slices.py` (245 строк, 3 файла, гейт + `--write` + `--self-test`).

### Что делать

| шаг | что | когда |
|---|---|---|
| 1 | **Переезд на fallout** — отдельным PR, с зелёным прогоном. Генератор приезжает вместе с ним как возможность, а не как обязательство | первым |
| 2 | **Гейт на required checks** (R3): скомпилированный Go-гейт, читающий `.github/rulesets/*.json` и сверяющий каждый `context` с `name:` джобов во всех workflow-файлах. Дешёвый, ловит R3 до того, как он станет причиной | вместе с переездом или сразу после — не зависит от переезда |
| 3 | **Расширить источник данных**: перенести в `tools/ci-project-lists.json` (или в новый `tools/ci-gate-lists.json`) реестр «гейт → workflow → джоб → флаги», чтобы добавление гейта не требовало правки `docs.yml` вручную | после переезда |
| 4 | **Один генерируемый workflow-файл** того вида, который fallout моделирует хорошо (см. §7) | после переезда, отдельным PR |
| 5 | **Остальные 16 файлов остаются ручными** — как в самом fallout | навсегда, пока не появится измеримая причина |

---

## 7. Пилот (если решим что-то генерировать)

**Что именно вынести первым.** Один файл, одна роль, форма под модель fallout.

`ci.yml` сейчас — 12 джобов, 3 484 строки, граф `needs`, per-job `permissions`, матрица шардов, required check `gate`. Это **не** подходит.

Подходит вот что: **две самые простые «списочные» полосы**, у которых нет графа джобов, нет матриц и нет per-job `permissions`:

| кандидат | строк | джобов | почему подходит |
|---|---:|---:|---|
| `evals.yml` | 93 | 1 | один джоб `live`, ни `needs`, ни `permissions`, ни `strategy` — **выражается в модели целиком** |
| `meas-jit-aot.yml` | 91 | 2 | оба джоба (`measure`, `measure-aot`) выражаются в модели целиком |

Суммарно **184 строки, 3 джоба** — это 1,9 % дерева workflow-файлов. Если пилот испортит, он испортит 1,9 % и не уронит полосу `ci`.

**Что должно получиться.** Один C#-файл (после переезда — в `build/`) с:

```csharp
[GitHubActions(
    "evals",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.WorkflowDispatch },
    OnPullRequestBranches = …,
    InvokedTargets = new[] { nameof(IEvals.Evals) })]
```

**Как выглядит проверка «сгенерированное совпадает с закоммиченным».** Три слоя, все из уже существующих образцов:

1. **Гейт-сверка (слой 1).** Скомпилированный Go-гейт `tools/gates/check-workflows-generated/` (по образцу `check-workflows`, без dotnet, в `docs.yml`): читает закоммиченный `.github/workflows/evals.yml`, **перегенерирует** его тем же кодом и сравнивает байты. Расхождение → красный, с именем файла и номером строки. Флаг `--self-test` доказывает, что гейт умеет краснеть: подсаженная правка в сгенерированный файл должна его разбудить.
2. **Проверка, что генератор не может выдать нечитаемый YAML (слой 2).** Тот же `check-workflows` (существующий) уже ловит дубль ключа. Он ничего не знает о происхождении файла и должен продолжать ловить всё, что сгенерировано: **сгенерированный файл проходит через тот же гейт, что и ручной**.
3. **Проверка, что required checks на месте (слой 3).** Гейт из шага 2 §6: восемь `context` из `.github/rulesets/*.json` существуют как `name:` джобов. Сгенерированный файл не может тихо переименовать джоб.

**Что НЕ делаем в пилоте:**

- не генерируем `ci.yml` (граф `needs` + матрица + required check `gate` — модель не выражает, а `gate` невозможно получить);
- не генерируем `docs.yml` (21 джоб, у **всех 21** per-job `permissions`);
- не генерируем шаги с телом `run:` — то есть **не переносим логику в C#**; генератор выдаёт каркас, а тела шагов остаются в YAML как `GitHubActionsCustomStep`;
- не генерируем файлы, которые читают секреты из окружения (`release.yml`).

### 7.1 Сгенерированный файл НЕ будет побайтово равен ручному — и это надо сказать заранее

Скелет, который выдаёт fallout, отличается от нашего в четырёх местах. Это не дефект пилота, это его предмет: генерация меняет поведение, и ревьюер должен видеть это в diff.

| что | наш файл сейчас | сгенерированный | последствие |
|---|---|---|---|
| **как выбирается SDK** | `dotnet-version: "10.0.x"` в **16** из 17 `setup-dotnet` и `dotnet-version: "10.0.402"` в 1 (`nightly.yml`) | `global-json-file: global.json` — то есть версия из `global.json` (`10.0.402`, `rollForward: latestPatch`) | правило выбора SDK меняется в 16 местах: «плавающий 10.0.x» → «пин из `global.json`». Для arm64 это даже важно: комментарий в `ci.yml` прямо говорит, что образ arm64 несёт 10.0.401, а `global.json` требует 10.0.402, и `latestPatch` 401 не принимает |
| **кэш** | `setup-dotnet` с `cache: true` и `cache-dependency-path: Directory.Packages.props`; свой `actions/cache` — это `cache/restore` + `cache/save` **для выходов сборки** (`obj/` + `bin/`) в `ci.yml` | отдельный шаг `actions/cache` на `~/.nuget/packages` + `.fallout/temp` | появится **второй** NuGet-кэш поверх встроенного в `setup-dotnet`. Ключи и содержимое у них разные — это надо либо принять, либо отключить кэш в атрибуте |
| **шаг запуска** | джоб вызывает гейт или `dotnet run --project tests/…` | шаг `dotnet fallout <targets>` **всегда присутствует** и **не удаляется**: `GitHubActionsStepPosition` даёт четыре точки вставки (`PostCheckout`, `PreRun`, `PostRun`, `JobEnd`) и ни одной точки удаления | каждый сгенерированный джоб будет звать сборщик. Для гейтовой полосы это лишний вызов: придётся либо мириться, либо вручную дописывать шаг после `PostRun` |
| **лишний шаг** | `dotnet tool restore` в наших workflow нет ни разу | есть в скелете | на раннере выполнится восстановление dotnet-инструментов (у нас в `.config/dotnet-tools.json` — `husky`), которого сегодня там нет |

Вывод из таблицы: **генерация — это не «то же самое, но в C#».** Это другое поведение, записанное в другом месте. Поэтому пилот обязан включать ревью diff'а сгенерированного файла, а не только проверку «совпало с закоммиченным».

**Критерий приёмки пилота** (измеримый): после переезда сборщика на fallout
1. `./build.sh` (или `dotnet fallout`) в пилоте переписывает **ровно два** файла (`evals.yml`, `meas-jit-aot.yml`) и **ни один** другой;
2. побайтовое сравнение «перегенерированное == закоммиченное» зелёное;
3. гейт `check-workflows` на перегенерированных файлах зелёный;
4. набор требуемых проверок не изменился (8 из 8 на месте);
5. **diff, который ревьюер видит, разобран по таблице §7.1** — каждое из четырёх отличий либо принято, либо отменено (например, кэш выключен в атрибуте);
6. прогон полосы `ci` на PR с этим изменением зелёный, а прогон `docs` — тоже (гейты живут в разных полосах, R1).

**Что пилот НЕ доказывает** (честно): он не доказывает, что модель fallout подходит для основной полосы. Она не подходит — это измерено в §2.1. Пилот доказывает только одно: **механизм «сгенерированный артефакт + гейт-сверка» переживает переезд и не ломает полосу**. Объём (2 файла из 17) выбран ровно так, чтобы цена ошибки была 1,9 % дерева.

---

## 8. Вариант (г): разбить `ci.yml` на несколько workflow'ев через `workflow_call`

Третий вариант из задания владельца в первом издании отчёта не разобран: §4 сравнивает (а) генерацию в fallout, (б) свой генератор в сборщике и (в) «списки + composite actions». Разбирать его отдельно нужно потому, что у него две границы, которых нет ни у одного из трёх: **ограничения GitHub** и **правило required checks**.

### 8.1 Что GitHub разрешает (дословно)

> You can connect a maximum of ten levels of workflows - that is, the top-level caller workflow and up to nine levels of reusable workflows. ([Reuse workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows))

> You can call a maximum of 50 unique reusable workflows from a single workflow file. This limit includes any trees of nested reusable workflows that may be called starting from your top-level caller workflow file. ([Reusing workflow configurations](https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations))

> Any environment variables set in an `env` context defined at the workflow level in the caller workflow are not propagated to the called workflow.

> Similarly, environment variables set in the `env` context, defined in the called workflow, are not accessible in the `env` context of the caller workflow. Instead, you must use outputs of the reusable workflow.

> Secrets are only passed to directly called workflow, so in the workflow chain A > B > C, workflow C will only receive secrets from A if they have been passed from A to B, and then from B to C.

> `GITHUB_TOKEN` permissions can only be the same or more restrictive in nested workflows.

> Reusable workflows are called directly within a job, and not from within a job step. You cannot, therefore, use `GITHUB_ENV` to pass values to job steps in the caller workflow.

> When you call a reusable workflow, you can only use the following keywords in the job containing the call: `name`, `uses`, `with`, `secrets`, `strategy`, `needs`, `if`, `concurrency`, `permissions`, `cache-mode`.

### 8.2 Что через границу переносится, а что нет (измерено на нашем корпусе)

| что | наш корпус | переносится? |
|---|---|---|
| `steps:`, `runs-on:`, `timeout-minutes:`, `concurrency:`, `environment:` | 62 джоба | да — это тело вызываемого workflow |
| `needs:` между джобами | 18 джобов | на стороне вызывающего: у джоба-вызова `needs:` разрешён, граф воспроизводится; но джобы **внутри** вызываемого не могут зависеть от джобов вызывающего |
| per-job `permissions:` | 52 джоба, 10 различных наборов, 8 просят WRITE | только вниз: `permissions:` у джоба-вызова разрешён, и вызываемый наследует его. `ci.yml::build` просит `actions: write`, `gates-binary.yml::rebuild-and-commit` и `release.yml::github-release` — `contents: write` → вызывающий обязан выдать объединение |
| `env:` на уровне workflow | 36 блоков `env:` по корпусу | **нет**: env вызывающего в вызываемый не попадает, и наоборот |
| `env:` на уровне джоба | 5 джобов | остаётся внутри вызываемого — не помешает |
| `GITHUB_ENV` | — | **нет**: записи вызывающего не видны шагам вызываемого |
| `secrets:` | 2 ссылки на `KILO_API_KEY` во всём корпусе | только непосредственно вызываемому; по цепочке — если переданы дальше |
| артефакты | 34 имени, 5 шагов загрузки | **да**: артефакты общие для прогона, цепочка `build` → `test`/`pack`/`perf-gate` пережила бы разбиение |
| `outputs:` | 7 джобов | наружу — через `on.workflow_call.outputs`, внутрь — через `inputs`; `ci.yml::changes` отдаёт 5 выходов, `test-os.yml::changes` — один |
| `strategy.matrix` | 5 джобов | на стороне вызывающего: `strategy:` у джоба-вызова разрешён |
| `continue-on-error`, `container:`, `services:` | 0 джобов | не мешает |

Вывод по таблице: **структура переносится, контекст — нет.** Всё, что определяет *когда* и *с какими правами* работает джоб, либо остаётся у вызывающего (и тогда джоб-вызов становится посредником), либо теряется.

### 8.3 Блокер: восемь required checks

Восемь имён, которые требуют ruleset'ы, — это `name:` под джобом, и все они живут в двух файлах:

| required check | где живёт |
|---|---|
| `gate` | `.github/workflows/ci.yml:3459` |
| `public-api freeze` | `.github/workflows/ci.yml:459` |
| `checker self-test` | `.github/workflows/docs.yml:332` |
| `internal links` | `.github/workflows/docs.yml:732` |
| `markdown lint` | `.github/workflows/docs.yml:826` |
| `normative citations` | `.github/workflows/docs.yml:763` |
| `tree gates` | `.github/workflows/docs.yml:691` |
| `test project coverage` | `.github/workflows/docs.yml:1277` |

Измерено на корпусе: **62 джоба, у 30 есть явный `name:`, различных отображаемых имён — 29**; у 32 джобов `name:` нет, и имя их проверки равно ключу джоба.

Два свойства GitHub, которые здесь важны:

1. **Джоб внутри reusable workflow сообщает свою проверку под именем `<имя джоба-вызывающего> / <имя джоба-внутри>`.** Форма названа в обсуждениях сообщества GitHub: `CI / Commit Validator` (обсуждение #46752), `aio (CentOS OVS) / All in one` (#72708); вложенные вызовы дают `CI / CI / Commit Validator`.
2. **Reusable workflow нельзя выбрать обязательной проверкой в ruleset** — только вызывающий. Ответ в обсуждении #170628: «a reusable workflow (`on: workflow_call`) can't be selected directly as a required status check in a repository ruleset. It doesn't run on its own… so it doesn't produce an independent required check entry».

Следовательно: **разбиение `ci.yml` переименовывает все восемь required checks одновременно** — из `gate` в `gate / gate` или в `<вызывающий workflow> / gate`, — и требует правки `.github/rulesets/dev.json` **и** `.github/rulesets/master.json` в том же PR, что меняет CI, в окне, где правило ждёт имён, которых больше нет. Постановка задачи говорит, что переименование одного джоба уже стоило заблокированного PR.

### 8.4 Где бы `workflow_call` всё-таки окупился

Механизм существует ради **переиспользования**: reusable workflow выигрывает тогда, когда его зовут больше одного раза. Измерено: **17 workflow-файлов, 0 триггеров `workflow_call`, каждый файл вызывается один раз.** Второго вызывающего нет — значит, нет и дублирования, которое механизм устраняет.

Исключение одно, и репозиторий уже записал решение по нему в текст. Джоб `changes` файла `test-os.yml` — намеренная копия джоба `changes` файла `ci.yml`:

> The reason it is replicated rather than shared is that a workflow cannot consume another workflow's job outputs; the only ways to share would be a reusable workflow (`workflow_call`) or a duplicated filter, and the duplicated filter is the smaller change.

(`.github/workflows/test-os.yml:98`)

Измерено, что именно дублируется:

| | `ci.yml::changes` | `test-os.yml::changes` |
|---|---|---|
| строк джоба | 318 (`.github/workflows/ci.yml:141`) | 64 (`.github/workflows/test-os.yml:110`) |
| фильтров | 5 (`dotnet`, `packages`, `ui`, `deps`, `aot`) | 1 (`dotnet`) |
| записей в фильтре | 115 | 16 |
| выходов джоба | 5 | 1 |
| `permissions:` | `contents: read`, `actions: read` | `contents: read` |

**Пересечение измерено: все 16 записей списка `test-os.yml` есть в списке `dotnet` файла `ci.yml` — ноль записей, уникальных для `test-os.yml`.** Список `test-os.yml` — строгое подмножество списка `ci.yml`, и риск разъезда, который комментарий называет, односторонний: `test-os.yml` не сможет получить путь, которого нет в `ci.yml`, а `ci.yml` — сможет.

Это единственное место, где `workflow_call` окупился бы: один reusable workflow с одним выходом на фильтр, вызванный дважды. Но:

- **16 строк списка дешевле закрыть не новым workflow'ем, а тем, что уже есть в дереве:** либо composite action, либо одним файлом данных. Оба приёма уже работают (`.github/actions/queue-wait`, `tools/ci-project-lists.json` + `ci-shard-slices.py`), и оба не трогают имена джобов;
- reusable workflow переименовал бы и сам джоб `changes` — в проверку с именем `<вызывающий> / changes`; `changes` не входит в восемь обязательных, но это проверка, которую ждут PR'ы;
- набор выходов у reusable workflow объявляется один раз, а нужны разные (5 и 1) — лишние выходы вызывающий получит и проигнорирует.

### 8.5 Вердикт по варианту (г)

| | |
|---|---|
| что выигрывает | **ничего**: дублирования, которое устраняет reusable workflow, в корпусе нет — каждый файл вызывается один раз |
| что стоит | переименование всех восьми required checks разом + правка обоих ruleset'ов + окно, в котором правило ждёт несуществующих имён |
| что можно вынести | джобы без `needs:` и без `matrix:` — 7 файлов, 905 строк, 9 % корпуса; но это ровно те файлы, которые почти не меняются (1-14 коммитов каждый) |
| что вынести нельзя | `ci.yml` целиком: граф `needs` (18 джобов), per-job `permissions` (52), матрица (5), выходы (7), обязательная проверка `gate` |
| что делать вместо | делить **списки** (источник данных) и **шаги** (composite actions). Оба приёма в дереве, оба не трогают имена джобов, оба совместимы и с NUKE, и с fallout |

Отдельно, как вывод из 8.4: **дублирование, которое `workflow_call` устранил бы (16 записей фильтра), дешевле устраняется источником данных.** Механизм reusable workflow'ев — это про совместное использование между двумя вызывающими; у нас оно есть ровно в одном месте и стоит 16 строк.

---

## 9. Перепроверка: независимое измерение на следующем дереве

Отчёт перепроверен независимо, на дереве `dev` @ `7d86fd0c` (на два коммита новее `a55ea18c`, на котором сняты числа §3). Числа §3 воспроизвелись, кроме трёх; одно число не воспроизвелось вовсе.

| утверждение отчёта | что даёт независимое измерение | дерево |
|---|---|---|
| 17 файлов, 9 656 строк, 62 джобы | те же | `a55ea18c` |
| комментарии 4 724 (48,9 %) | те же | `a55ea18c` |
| per-job `permissions:` — 52, `if:` — 20, `needs:` — 18, `outputs:` — 7, `strategy:` — 5, `env:` — 5 | те же; плюс 10 различных наборов `permissions` и 8 джобов, просящих WRITE | `a55ea18c` |
| выражаются в модели fallout **4 джоба из 62** | те же четыре: `evals.yml::live`, `goldens.yml::regenerate`, `meas-jit-aot.yml::measure`, `meas-jit-aot.yml::measure-aot` | `a55ea18c` |
| 29 различных отображаемых имён джобов | те же 29; 30 джобов с явным `name:`, 32 без | `a55ea18c` |
| **31 гейт, 74 места вызова** | **68 реальных мест вызова** в 31 гейте; шесть остальных — проза в комментариях. `all` — **2** вызова, не 5 | `a55ea18c` |
| **455 коммитов**, тронувших `.github/workflows/**` | **229** различных коммитов; 233 по `.github/**`; 300 — сумма пофайловых счётчиков (коммит-файлы); 681 по всем веткам. 455 не воспроизводится | `a55ea18c` |

На следующем дереве корпус — 9 708 строк, 62 джобы, 364 шага; `ci.yml` — 3 520 строк.

### 9.1 Чего в отчёте не было: минимальная YAML-обвязка

§3.2 отвечает на вопрос «что останется, если всё уедет в C#». Вопрос из задания владельца другой: **что останется в YAML при любом варианте**. Ответ измерен, построчно:

| слой | строк | как считано |
|---|---:|---|
| блоки `on:` (триггеры) | 461 (282 кода, 179 комментариев) | построчно по каждому файлу |
| шапки файлов (`name:`, workflow-`permissions:`, `concurrency:`, `jobs:`) | ~102 | 17 файлов × 6 строк |
| шапки джобов (ключ, `name:`, `runs-on:`, `needs:`, `if:`, `permissions:`, `timeout-minutes:`, `steps:`) | ~500 | 62 джоба × 8 строк |
| по одной строке на вызов шага | 364 | 364 шага |
| **итого обвязки** | **≈ 1 425** | **15 % корпуса** |

Тела шагов (7 620 строк, 78 % корпуса) и комментарии (4 756 строк, 49 %) уходят из YAML в composite actions — или остаются в нём. Число «1 425» — это то, что **не может** уйти никуда: триггеры, имена джобов, права, граф и вызовы шагов. Это и есть ответ на вопрос «что останется в YAML».

### 9.2 Чего в отчёте не было: измеренный набор composite actions

Три формы, которые повторяются, измерены по корпусу:

| composite action | мест вызова | строк, которые уходят | строк на месте вызова |
|---|---:|---:|---:|
| `setup-dotnet` (checkout + SDK + кэш) | 25 джобов в 15 файлах | 517 | 25 × 6 |
| `upload-artifact` | 34 шага | 856 | 34 × 5 |
| `run-gate` (скомпилированный гейт с меткой) | 72 шага | 1 237 | 72 × 4 |

Форма `setup-dotnet` — одна из двух (с кэшем и без), `checkout` — одна из семи (`fetch-depth`, `ref`, `submodules`), `upload-artifact` — одна из одной (те же четыре ключа `with:`, разные значения). Уже существующий образец — `.github/actions/queue-wait`: 14 мест вызова в двух файлах, 217 строк на само действие.

**Что в composite action не переносится:** три пары близнецов x64/arm64 в джобе `test` файла `ci.yml` (`.github/workflows/ci.yml:2711` — `.github/workflows/ci.yml:2770`, 60 строк на 3 логических шага). Близнецы отличаются бинарём, который зовут (`harbor-gates-linux-x64` против `harbor-gates-linux-arm64`), и параметром одного действия это не выражается.

---

## 10. Что осталось неизмеренным

| вопрос | почему не измерено | как измерить |
|---|---|---|
| Сколько строк даст переезд NUKE → fallout в самом `build/_build/` | локальный `dotnet` запрещён (OOM); правка сборщика — работа другого агента (ADR + план переезда) | прогон CI на PR переезда |
| Реальная скорость генератора fallout и размер его вывода | нельзя запустить без dotnet | один прогон `dotnet fallout` в CI |
| Влияет ли `ConfigureSteps` на читаемость результата | требует рабочего примера сгенерированного файла | сгенерировать `evals.yml` и прочитать |
| Сколько времени стоит гейт-сверка (перегенерация + сравнение) | гейта ещё нет | замер в CI после первого прогона |
| Что будет с `tools/ci-shard-slices.py` после переезда | это Python, а новая политика — Go-гейты; решение принимает владелец | миграция гейта на Go (отдельная задача) |
| Полная стоимость «переписать 17 файлов в C#» | не считалась: оценка выше — «43,7 % строк», а не «сколько дней» | оценка по числу шагов (362) и тел шагов (3 328 строки) |
| Поведение `actions/cache` в сгенерированном файле vs ручном | не измерено: в наших файлах кэш настроен вручную (2 места `actions/cache`), а генератор выдаёт свой | сравнить два файла после пилота |
| Точное имя проверки джоба **внутри** reusable workflow | в этом репозитории нет ни одного reusable workflow, поэтому имя не наблюдалось здесь; форма `<вызывающий> / <вызываемый>` взята из обсуждений сообщества GitHub, а не из собственных прогонов | один экспериментальный reusable workflow на ветке и чтение `check-runs` через API |
| Сколько стоит **реальный** выигрыш composite actions | §9.2 считает строки, которые уходят из файлов, но не учитывает, что у каждого действия появляются свои входы и своя документация | написать одно действие и пересчитать |
| Пересечение фильтров `changes` после правок | измерено на `a55ea18c`: список `test-os.yml` — строгое подмножество списка `ci.yml`; на следующем дереве это надо перепроверить | повторить построчный пересчёт (§8.4) |

---

## 11. Связанное

- **Issue [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185)** — задача, в которой зафиксированы числа этого отчёта и рекомендация.
- **Issue [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185)** — задача, в которой зафиксированы числа этого отчёта и рекомендация.
- **Issue [#2191](https://github.com/refusedguy/Harbor-Harness/issues/2191)** — имена обязательных проверок в ruleset никто не проверяет; это отдельная от этого отчёта работа, и R3/R9 (§5, §8.3) указывают в неё.
- `tools/ci-project-lists.json` — единственный источник списков CI-проектов; гейт `tools/ci-shard-slices.py` держит четыре производных места в согласии с ним.
- `tools/gates/check-workflows/` — гейт, который читает workflow-файлы и ловит то, что GitHub прочитать не сможет; живёт в `docs.yml`, а не в `ci.yml`, — и это не выбор вкуса.
- `tools/gates/gen-seam-graph/` — образец «сгенерированный артефакт + гейт-сверка»: `--write` / `--check` / `--self-test`.
- `.github/workflows/gates-binary.yml` — образец «CI сам коммитит сгенерированный артефакт»: джоб `rebuild-and-commit` пересобирает бинарь и коммитит его, а `verify-committed` на PR-path сверяет байты.
- `.github/rulesets/dev.json`, `.github/rulesets/master.json` — восемь требуемых проверок; сегодня их имена никем не проверяются.
- `docs/CI_GATES.md` — таблица гейтов; её колонки «Workflow» и «Jobs» — это карта «гейт → workflow → джоб», которую стоит перенести в источник данных.
