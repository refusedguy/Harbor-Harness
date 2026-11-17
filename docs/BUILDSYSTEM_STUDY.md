# Сборщик: NUKE → Fallout. План переезда

> **Статус (2026-10-22):** план, а не отчёт о рассмотрении. Владелец решил
> 2026-10-22, что оркестратор сборки переезжает с NUKE на
> [Fallout](https://github.com/Fallout-build/Fallout). Этот документ — обоснование
> (факты) и пошаговый план переезда с проверяемым результатом на каждом шаге.
>
> Все числа сняты 2026-10-22 по дереву `dev` = `82c7a1d0` и по GitHub API в тот же
> день. Число, которое не измерено, помечено как проекция.

## Что уже было в репозитории

Проверено перед тем, как писать план: **решения о смене сборщика в репозитории
нет**.

| Где искали | Что нашли |
|---|---|
| `docs/adr/` (23 файла, ADR-001…ADR-022) | ADR о сборщике нет. Ближайшее — ADR-019 (рендерер SharpConsoleUI уходит из продукта), где NUKE упоминается только как контекст. |
| `docs/ROADMAP.md` | Единственное упоминание NUKE — строка 144, про флаг `HarborWithPlugins` по умолчанию. Про судьбу сборщика — ничего. |
| `docs/DEVELOPMENT.md`, `CLAUDE.md`, `AGENTS.md` | Сборщик не описан: в `AGENTS.md` и `CLAUDE.md` слов «NUKE»/«build/_build» нет вовсе. `docs/DEVELOPMENT.md` описывает тестовые команды, но не оркестратор. |
| `git log --all --grep=nuke` | История из 20+ коммитов — все про поведение NUKE-таргета (ключи MSBuild, кэш, анализаторы), ни один — про замену сборщика. |
| `docs/BUILD.md`, `docs/AOT_PUBLISH.md`, `docs/CENTRAL_PACKAGE_MANAGEMENT.md`, `docs/RELEASE_ENGINEERING_ANALYSIS.md`, `docs/BENCHMARKS.md`, `docs/REFERENCE-TRIMMER.md`, `docs/ALTERNATIVE_UIS.md` | 8 документов описывают сборщик как данность. Их надо править. |

Вывод: спорных нет, противоречий нет, отчёт ни с чем не спорит.

---

## 1. Факты по NUKE: проект заброшен автором

### 1.1 Что показывает GitHub API (2026-10-22)

| Показатель | Значение | Источник |
|---|---|---|
| `archived` | **`false`** | `GET /repos/nuke-build/nuke` |
| последний коммит | **2025-12-02T10:17:32+01:00**, Matthias Koch | `git log` в полном клоне |
| последний релиз | **10.1.0**, 2025-12-02T09:58:36Z | `GET /releases` |
| коммитов за 12 мес (2025-10-22…2026-10-22) | **20** (18 Matthias Koch, 2 Marko Lahma), все до 2025-12-03 | `git log --since` |
| коммитов за 6 мес (2026-04-22…2026-10-22) | **0** | `git log --since` |
| открытых issue | **65** | `GET /search/issues` |
| открытых PR | **55** | `GET /search/issues` |
| всего issue (открытых + закрытых) | 931 | `GET /search/issues` |
| звёзд / форков | 3 748 / 457 | `GET /repos` |
| лицензия | MIT | `LICENSE.md` |
| сайт `nuke.build` | **не открывается** | issue [#1601](https://github.com/nuke-build/nuke/issues/1601) (2026-07-24), webfetch — transport error |

Итог по срокам: последний коммит NUKE — **2025-12-02**, то есть **324 дня** до
2026-10-22. Шестимесячного окна активности нет вообще.

### 1.2 Мейнтейнер не отвечает — это видно по датам, а не по впечатлению

Проверено: ни на одном открытом issue после 2025-12-02 нет комментария от
`matkoch` (Matthias Koch — автор NUKE). Комментарии есть, но все от сообщества:

| issue | создан | последний комментарий | кто |
|---|---|---|---|
| [#1601](https://github.com/nuke-build/nuke/issues/1601) сайт nuke.build недоступен | 2026-07-24 | 2026-07-24 | `ITaluone`, `ymalich-np4` (оба отвечают: «Come and see: https://docs.fallout.build») |
| [#1591](https://github.com/nuke-build/nuke/issues/1591) уязвимые транзитивные зависимости: CVE-2026-33116 (S.S.C.Xml) и Scriban 6.2.1 (1 critical, 5 high) | 2026-04-16 | **комментариев нет вообще** | — |
| [#1586](https://github.com/nuke-build/nuke/issues/1586) поддержка формата `.slnx` | 2026-01-09 | 2026-07-27 | `IT-VBFK` отвечает: «https://github.com/Fallout-build/Fallout» |
| [#1599](https://github.com/nuke-build/nuke/issues/1599) шифрование небезопасно | 2026-05-27 | **нет** | — |
| [#1585](https://github.com/nuke-build/nuke/issues/1585) build.ps1 path failure | 2026-01-06 | 2026-09-11 | `ITaluone` |

Это не «медленно отвечает». Это — ни одного ответа мейнтейнера за **324 дня**, при
том что открытых issue 65 и открытых PR 55.

### 1.3 Что из этого следует для нас

Наш билд сидит на `Nuke.Common 10.1.0` (пин в `build/_build.csproj`). Пока NUKE
не обновлялся, мы держали транзитивные уязвимости закрыты **своими** пинами в
`build/_build.csproj`: `System.Security.Cryptography.Xml 10.0.13` (11 advisory-диапазонов
закрыто), `Microsoft.Build* 18.0.2`, `NuGet.Packaging 6.12.5`. Комментарий в
`build/_build.csproj` называет причину прямо: пины нужны, потому что NUKE-версия
заморожена. Это состояние — «обслуживаем чужой долг вручную», и оно тем дороже, чем
дольше стоит.

### 1.4 Точная формулировка факта

Формулировка владельца — «NUKE 100 % просто забит автором в пучины» — **подтверждается
по датам и цифрам** с одной оговоркой: **флаг `archived` на GitHub не выставлен**
(`archived: false`). Проект не помечен как архивный, но фактически не обслуживается:
0 коммитов за 6 месяцев, 324 дня без единого коммита, ни одного ответа мейнтейнера,
сайт проекта не работает, отчёт о CVE с 1 critical без ответа четыре месяца.

Дальше в этом документе и в
[ADR-023](adr/ADR-023-build-orchestrator-moves-to-fallout.md) факт записан именно
так: «де-факто заброшен автором», со ссылкой на источники, а не со ссылкой на слова
владельца. Слова владельца идут отдельной строкой как **основание решения**.

---

## 2. Факты по Fallout

### 2.1 Что это

| Показатель | Значение | Источник |
|---|---|---|
| репозиторий | `github.com/Fallout-build/Fallout` | — |
| `fork` на GitHub | **`false`** — это «жёсткий форк» с сохранённой историей, а не fork-запись GitHub | `GET /repos/Fallout-build/Fallout` |
| точка форка | **NUKE `develop` = `27e8077b8ab01222c16c3610a899c25d1bce910c`, 2025-12-02** — это последний коммит NUKE и релиз 10.1.0 | `git merge-base --is-ancestor 27e8077b8 HEAD` в клоне Fallout → **ancestor: YES** |
| лицензия | **MIT** — `LICENSE`: «Copyright 2026 Maintainers of Fallout / Based on NUKE, originally created by Matthias Koch and contributors. Copyright 2017-2025 Maintainers of NUKE» | `LICENSE` в репозитории + `<license type="expression">MIT</license>` в `.nuspec` |
| `archived` | `false` | `GET /repos` |
| создан | 2026-05-18 | `GET /repos` |
| последний пуш | **2026-10-22T04:54:39Z** (сегодня) | `GET /repos` |
| своих коммитов сверх точки форка | **459** | `git rev-list --count 27e8077b8..HEAD` |
| авторов этих коммитов | **17** | `git log --format=%an` |
| коммитов за 30 дней | **21** | `git log --since=2026-09-22` |
| коммитов за 90 дней | **146** | `git log --since=2026-07-24` |
| звёзд / форков | 173 / 22 | `GET /repos` |
| открытых issue+PR | 214 | `GET /repos` |
| последний релиз на GitHub | `v10.5.0-rc.2`, 2026-10-21 | `GET /releases` |
| последняя версия на nuget.org | `11.0.18` | `api.nuget.org/v3-flatcontainer` |
| скачиваний `Fallout.Common` | **103 378** | `azuresearch-usnc.nuget.org` |
| скачиваний `Nuke.Common` | **39 894 159** | там же |

Топ-контрибьюторы Fallout (сверх точки форка): Chrison Simtian 285, Dennis Doomen 59,
Lukas Gasselsberger 32+9, Anže Videnič 26, ITaluone 11, Marko Lahma 10, Yousif Salah 8,
Alexander Nachtmann 6.

Разрыв по скачиваниям — **386×** (103 378 против 39 894 159). Это честная цифра
зрелости: форк молодой и им почти никто не пользуется. NUKE набирал 39.9 млн
скачиваний за годы; Fallout — 103 тысячи за пять месяцев. Ниже в риск-реестре это
учтено отдельной строкой.

### 2.2 Совместимость с нашим случаем — по фактам, не по предположению

| Требование | Статус | Чем подтверждено |
|---|---|---|
| **.NET 10** | **подтверждено** | `Fallout.Common 11.0.18` содержит только `lib/net10.0/`; то же у `10.4.0`. `Fallout.ProjectModel 11.0.18` — `lib/net10.0` + `net8.0` + `net9.0`. |
| **`.slnx`** | **подтверждено** | В дереве Fallout: `src/Persistence/Fallout.Persistence.Solution/Serializer/Xml/SlnxSerializerSettings.cs`, `Slnx.xsd`, `XmlDecorators/SlnxFile.cs`. В собранном пакете `Fallout.Persistence.Solution` (и 10.4.0, и 11.0.18) строки `SlnxFile`, `SlnxSerializerSettings`, `SlnXmlSerializer` и описание «Internal parser for .sln/.slnx solution files used by Fallout.Solution. Inlined from Microsoft.VisualStudio.SolutionPersistence (MIT)». Сам Fallout собирается на `fallout.slnx`. |
| **NativeAOT** | **подтверждено косвенно, прямого теста нет** | `Fallout.Common 11.0.18` содержит `SelfContained`, `PublishSingleFile`, `PublishTrimmed`, но **не** содержит `PublishAot` (0 вхождений в DLL). Наш код ставит `PublishAot` **как MSBuild-свойство** (`PublishVariantBuilder.ApplyVariant` → `SetProperty("PublishAot", "true")`), а не типизированным сеттером, — то есть поведение идентично и не зависит от поверхности API. Но «fallout публикует AOT» отдельным тестом **не подтверждено**: это надо доказать шагом 0. |
| **упаковка под разные RID** | **подтверждено** | `DotNetPublishSettings` в `Fallout.Common` имеет `Runtime`, `SelfContained`, `Output`, `Properties` — те же, что нужны нашим 6 вариантам публикации. |
| **API-совместимость DSL** | **высокая** | Миграционный гайд: «The 1:1 namespace prefix swap is the only structural change. Type names (other than `NukeBuild` / `INukeBuild`) keep their identifiers — `[Parameter]`, `[Solution]`, `[GitHubActions]`, `Solution`, `GitRepository`, etc. all stay the same.» |

### 2.3 Документированный путь миграции — есть

1. **`fallout-migrate`**, глобальный dotnet-инструмент (`Fallout.Migrate`, на
   nuget.org, последняя версия 11.0.18). Переписывает `PackageReference`,
   `using`-директивы, MSBuild-свойства, bootstrap-скрипты, env-переменные,
   переименовывает `.nuke/` → `.fallout/`. Идемпотентен, есть `--dry-run`.
2. **`Fallout.Migrate.Analyzers`** — Roslyn-анализатор, каждое упоминание `Nuke.*`
   даёт предупреждение `FALLOUT004` с кодфиксом.
3. **Переходный shim** — пакеты `Nuke.*` на GitHub Packages, переэкспортируют
   легаси-типы как подклассы канонических `Fallout.*`. Оговорка из гайда: «The shim
   covers most public types but skips a few (sealed-class wrappers and static helper
   classes like `DotNetTasks` are not bridged yet — tracked in #69)». **Для нас это
   важно: `DotNetTasks` не переброшен**, а мы зовём `DotNetTasks.DotNetBuild`,
   `DotNetTasks.DotNetPublish`, `DotNetTasks.DotNetPack`. Значит shim-путь нам не
   подходит, нужен `fallout-migrate`.
4. **`fallout :cake-convert`** — конвертер из Cake (для сравнения, не для нас).

Ограничения shim, цитатой: *«The shim is a transition aid, not a permanent home. It's
removed in 12.0. Plan to migrate before then.»*

### 2.4 Два решения о версии, которые надо принять в шаге 1

| Вопрос | Факт | Последствие для нас |
|---|---|---|
| **10.x или 11.x** | `10.4.0` — последняя **листинговая** версия на nuget.org (`azuresearch` отдаёт `latest=10.4.0`, в плоском контейнере при этом лежит `11.0.18`). `11.x` по [ADR-0002](https://github.com/Fallout-build/Fallout/blob/main/docs/adr/0002-v11-off-nuget-by-default.md) публикуется на nuget.org **только по явному флагу**; v11 — нестабильная поверхность. | Брать **10.4.0**: она листинговая, `dotnet add package` её найдёт, `Fallout.Solution 10.4.0` уже тянет `Fallout.Persistence.Solution` с `.slnx`-читалкой. |
| **пространство имён** | В `10.4.0` — `Fallout.Common.ProjectModel`; в `11.0.18` — переименовано в `Fallout.Solutions` (в DLL 11.0.18 строки `Fallout.Common.ProjectModel` уже нет). | Наш `using Nuke.Common.ProjectModel;` → `using Fallout.Common.ProjectModel;` на 10.x. **Рекомендация: 10.4.0.** |

### 2.5 Одна конкретная поломка, которую мы уже нашли

**Наш пин `NuGet.Packaging 6.12.5` станет downgrade-ошибкой.** `Fallout.Tooling
11.0.18` для `net10.0` требует `NuGet.Packaging 6.14.3`. Прямой `PackageReference`
6.12.5 рядом с транзитивным требованием 6.14.3 — это **NU1605 «Detected package
downgrade»**, то есть ошибка restore, а не предупреждение. Пин надо поднять до
6.14.3 (или убрать и позволить транзитивному).

Второй пин: `System.Security.Cryptography.Xml 10.0.13` — **оставить**. Fallout
11.0.18 приносит 10.0.6 для `net10.0` (10.4.0 — 10.0.10), и наш комментарий в
`build/_build.csproj` прямо говорит, что 10.0.13 — минимальная версия вне 11
advisory-диапазонов. Наш пин выше транзитивного, downgrade-ошибки не будет, но и
защита сохранится.

Третий: `Microsoft.Build* 18.0.2` — совпадает с тем, что требует Fallout для
`net10.0`. Менять не нужно.

---

## 3. План переезда

Правило, из которого выведены все шаги: **первый шаг миграции не должен ломать
полосу CI**. Поэтому пилот идёт **параллельно** старому пути, а переключение —
отдельным шагом с явной откаткой.

### Шаг 0. Пилот: один таргет на fallout, тот же артефакт

**Что делаем.** Заводим **второй** build-проект рядом с NUKE-овым:
`build/_build.fallout/` (или `build/_build2/`), тот же `Build.cs`, но на API
fallout. Ничего в `build/_build/` не трогаем.

**Какой таргет.** Выбор обоснован измерением:

| Кандидат | Почему нет |
|---|---|
| `Compile` | Артефакт — 114 проектов, сравнение по хешам непрактично; время прогона длинное; цена ошибки максимальная. |
| `Test` | Нет детерминированного артефакта: результат зависит от порядка шардов и окружения. |
| `Publish` / `PublishAot` | Долго (AOT-сборка), артефакт зависит от RID и среды. |
| **`Pack`** | **Да.** Даёт ровно один сравнимый артефакт: `.nupkg`. Побайтовое сравнение возможно (nupkg — zip, но состав и CRC фиксированы). |

**Выбран `Pack`**, потому что:
- наш `PackTarget` уже умеет dry-run и печатает полный argv (`DotNetArgv.RenderPublish`
  и его аналог) — argv сравнивается как строка, это дешёвый и точный признак;
- артефакт — один `.nupkg` с фиксированным именем; сравниваем список файлов внутри,
  их CRC и SHA-256 пакета;
- таргет не тянет RID, AOT и окружение: чистый `dotnet pack` + `dotnet nuget push`
  в dry-run;
- наш CI уже качает пакеты в джобе `pack` (`.github/workflows/ci.yml`, джоб `pack`,
  строки 1818+), то есть артефакт уже проходит через полосу — пилот можно
  положить в тот же джоб.

**Чем доказываем.** Пять проверок, все обязательны:

1. **argv совпадает.** `pack --dry-run --format json` на NUKE и на fallout даёт
   **одинаковую** строку argv (после перевода `Nuke.` → `Fallout.` в именах пакетов
   — сам argv не меняется).
2. **exit code совпадает.** 0 в обоих.
3. **состав пакета совпадает.** Список файлов внутри `.nupkg` — то же множество, те
   же CRC.
4. **SHA-256 `.nupkg` совпадает** — либо расходится только полем, которое NuGet
   пишет временем (тогда сравниваем состав + CRC, и это записывается в отчёт).
5. **`list --format json` совпадает.** Каталог таргетов (`TargetCatalog`) на обеих
   сборках даёт одинаковый список — это уже делает наш `TargetCatalog.Verify`,
   который падает на расхождении.

**Что может сломаться.**
- `fallout-migrate` не перепишет наш код полностью: у нас 32 файла, а не шаблон
  NUKE. Особенно `build/_build/Meta/DotNetArgv.cs` (66 строк) — он рендерит argv
  **вручную**, и это наш собственный код, а не API NUKE.
- NuGet-версия пакета может отличаться в транзитивных зависимостях → другой `.nuspec`
  внутри пакета → состав разойдётся. Это ожидаемо и не является регрессом; критерий
  приёмки — «тот же состав **исходников**», а не побайтовое равенство всего пакета.
- Наш `PackTarget` печатает и пушит в GitHub Release — пилот обязан быть в dry-run.

**Как откатить.** Пилот — это **добавленный** проект. Откат = удалить
`build/_build.fallout/` и один workflow-файл. NUKE-путь не тронут, полоса CI не
знает о пилоте, если пилот не включён в джоб `pack` — а он включается **новым
шагом**, а не заменой.

**Критерий приёмки.** Один PR, который добавляет (а) второй build-проект,
(б) workflow-шаг в джобе `pack`, который печатает оба argv и оба состава пакета,
(в) текстовое резюме сравнения. Полоса CI зелёная, NUKE-путь не изменился ни на
одной строке.

### Шаг 1. Весь `build/_build/` переводится на API fallout

**Что делаем.** Копия с пилота становится основной; NUKE-овый `build/_build/`
остаётся на месте до шага 4.

**Изменения по файлам.** Полный инвентарь `build/_build/` — **32 файла `.cs`, 3 718
строк** плюс `build/_build.csproj` и `build/Directory.Build.props`. Что меняется в
каждом:

| Файл | Строк | Что меняется |
|---|---:|---|
| `build/_build/Build.cs` | 382 | `using Nuke.Common;` → `using Fallout.Common;`; `NukeBuild` → `FalloutBuild`; `using Nuke.Common.ProjectModel;` → `using Fallout.Common.ProjectModel;`; `[Solution("Harbor.slnx")] private readonly Solution Solution;` — **тип и имя поля те же**, меняется только пространство имён. 15 таргетов остаются 15. |
| `build/_build/Components/ArchiveBuilder.cs` | 102 | `using Nuke.Common.IO;` → `using Fallout.Common.IO;`. `AbsolutePath` — тот же тип, другое пространство. |
| `build/_build/Components/ArtifactPathResolver.cs` | 104 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Components/CliBuildConfigurator.cs` | 86 | **не меняется** — своего кода NUKE не касается. |
| `build/_build/Components/GitHubReleaseUploader.cs` | 140 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. |
| `build/_build/Components/PublishVariantBuilder.cs` | 133 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. Типы `DotNetPublishSettings`/`DotNetTasks` — те же имена. **API-сеттеры совпадают**: `SetProject`, `EnableNoRestore`, `EnableNoBuild`, `DisableNoBuild`, `SetConfiguration`, `SetOutput`, `SetProperty`, `SetRuntime`, `SetSelfContained`. |
| `build/_build/Configuration/ArchiveFormat.cs` | 8 | не меняется |
| `build/_build/Configuration/BuildConfiguration.cs` | 7 | не меняется |
| `build/_build/Configuration/BuildSettings.cs` | 10 | не меняется |
| `build/_build/Configuration/FeatureFlags.cs` | 22 | не меняется |
| `build/_build/Configuration/PublishVariant.cs` | 11 | не меняется |
| `build/_build/Configuration/RendererSelection.cs` | 32 | не меняется |
| `build/_build/Extensions/DotNetPublishExtensions.cs` | 33 | `using Nuke.Common.Tools.DotNet;` → `Fallout.Common.Tools.DotNet;`. Расширения на `DotNetPublishSettings` переносятся как есть. |
| `build/_build/Extensions/FileSystemTasksExtensions.cs` | 59 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. |
| `build/_build/GlobalUsings.cs` | 3 | **не меняется** — в файле только `global using Harbor.Build.Configuration;`, NUKE он не касается |
| `build/_build/Meta/BuildOutput.cs` | 314 | не меняется (свой код) |
| `build/_build/Meta/DoctorChecks.cs` | 481 | не меняется (свой код) |
| `build/_build/Meta/DotNetArgv.cs` | 66 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. **Рендер argv — наш код**, API не трогаем. |
| `build/_build/Meta/ImpactMapper.cs` | 441 | не меняется (свой код) |
| `build/_build/Meta/ImpactMapperTests.cs` | 142 | не меняется (свой код) |
| `build/_build/Meta/TargetCatalog.cs` | 425 | `using Nuke.Common;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. **Важно**: каталог отражает таргеты NUKE-класса — при переносе список должен совпасть 1:1, иначе `TargetCatalog.Verify` (его проверяет шаг `list` в CI) покраснеет. |
| `build/_build/Targets/AllTargets.cs` | 60 | `using Nuke.Common;` → `Fallout.Common;`. |
| `build/_build/Targets/ArchitectureTestTarget.cs` | 32 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/ArchiveTarget.cs` | 48 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/CleanTarget.cs` | 52 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/CompileTarget.cs` | 112 | `using Nuke.Common.ProjectModel;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. **Ключевое:** таргет передаёт настоящие switch'и MSBuild (`-maxcpucount:{ProcessorCount}`, `/nodeReuse:false`) — issue #2111. Эти switch'и передаются как аргументы процесса, то есть API-независимы и переносятся как есть. |
| `build/_build/Targets/IpcPublishTarget.cs` | 91 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/PackTarget.cs` | 79 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/PublishTarget.cs` | 51 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/ReleaseTarget.cs` | 76 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/RestoreTarget.cs` | 28 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/TestTarget.cs` | 88 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build.csproj` | 105 | `Nuke.Common 10.1.0` → `Fallout.Common 10.4.0`; `<NukeTelemetryVersion>` убрать (у fallout телеметрии нет, ADR-0010); **пин `NuGet.Packaging` 6.12.5 → 6.14.3** (иначе NU1605); пины `Microsoft.Build* 18.0.2` и `System.Security.Cryptography.Xml 10.0.13` **оставить**; комментарии переписать под новую реальность. |
| `build/Directory.Build.props` | 55 | `<NukeTelemetryVersion>1</NukeTelemetryVersion>` убрать; комментарии «NUKE-specific overrides» → «Fallout-specific overrides». Остальные свойства (`TreatWarningsAsErrors=false`, CPM off, анализаторы off) — те же. |
| `build/build.sh` | 78 | bootstrap: `dotnet nuke` → `dotnet fallout`, `.nuke/temp` → `.fallout/temp`, `NUKE_TELEMETRY_OPTOUT` убрать. **Либо** оставить наш «толстый» bootstrap (он сам собирает `_build.csproj` и зовёт `dotnet exec _build.dll`) — тогда меняется только имя пакетов, а не скрипт. |
| `build/build.ps1` | 118 | то же для Windows. |
| `Directory.Packages.props` | 615 | комментарий в строках 584-601 «NUKE build system … NO Nuke.Common ENTRY HERE» — переписать на fallout. **Пакетного пина не добавлять**: CPM для `build/` выключен. |
| `Directory.Build.props` (корень) | 268 | комментарии в строках 208, 218, 228, 256 упоминают «NUKE-driven release builds» и «NUKE spelling» — переписать. Свойства не меняются. |
| `.nuke/` → `.fallout/` | 2 файла | `build.schema.json` (6 776 байт) и `parameters.json` (39 байт) — переименовать каталог, содержимое сохраняется. |

**Итого по шагу 1:** 32 файла `.cs` (из них **12 вообще не меняются** — своего кода
NUKE не касается, правятся только `using`-ы в **20**), 2 файла props, 2 bootstrap-скрипта,
1 `.csproj`, 1 каталог `.nuke/`.

**Чем доказываем.** Шаг 0 повторяется на каждом из 15 таргетов: argv совпадает,
exit code совпадает, `list --format json` совпадает 1:1. Плюс — сборка всех 15
таргетов на fallout проходит без NUKE.

**Что может сломаться.**
- `DotNetTasks` **не переброшен** в shim (issue #69) — значит любой `using Nuke.*`
  должен быть переписан, shim-путь не работает. Это уже учтено: план идёт через
  `fallout-migrate`, а не через shim.
- Транзитивные зависимости fallout'а другие (`Microsoft.Extensions.DependencyModel 10.0.0`,
  `Octokit 14.0.0`, `Azure.Identity 1.17.1`, `YamlDotNet 16.3.0` — в дереве NUKE
  их нет) → больше пакетов в restore, больше времени.
- Наш `Build.cs` использует `new Target Help` (`private new Target Help`) — «новый»
  ключевое слово означает скрытие члена базового класса. Если базовый класс
  переименует/перестроит `Help`, компиляция может сломаться. **Проверяется компиляцией,
  это и есть приёмка шага.**
- `RootDirectory`, `ArtifactsDirectory`, `Solution` — свойства базового класса
  `NukeBuild`/`FalloutBuild`. Имена те же по гайду, но это проверяется компиляцией.

**Как откатить.** Шаг 1 — добавление, а не замена: NUKE-овый `build/_build/` цел.
Откат = удалить новый каталог. Ни один workflow-файл на этом шаге не меняется.

### Шаг 2. Точки вызова в CI и bootstrap-скрипты

**Что делаем.** Переключаем CI на новую точку входа.

**Все места в `.github/workflows/` (полный список, снят grep'ом):**

| Файл | Строка | Что там | Что меняется |
|---|---:|---|---|
| `.github/workflows/ci.yml` | **1354** | `bash build/build.sh Compile --configuration Release` (джоб `build`, шаг «Build solution via NUKE (Release, zero warnings, docs, deterministic)») | `bash build/build.sh Compile --configuration Release` — **строка не меняется**, если bootstrap-скрипт сохраняет имя `build.sh`. Меняется то, что внутри скрипта. Если скрипт переименуется — строка меняется. |
| `.github/workflows/ci.yml` | **1562** | `bash build/build.sh list --format json > /dev/null` (джоб `build`, шаг «Validate installer scripts + build catalog») | то же |
| `.github/workflows/ci.yml` | 252, 956, 1485 | комментарии («via ./build.sh», «NUKE build-code edits validate here too», «NUKE target catalog») | переписать комментарии |
| `build/build.sh` | весь | bootstrap | `dotnet nuke` → `dotnet fallout` либо оставить свой запуск `_build.dll` |
| `build/build.ps1` | весь | bootstrap | то же |

**Всего: 2 исполняемые точки вызова + 3 комментария + 2 bootstrap-скрипта.**
Ни один другой workflow `build.sh` не зовёт — проверено grep'ом по всем 17 файлам.
Отдельные workflow-файлы (`release.yml`, `nightly.yml`, `demo.yml`, `test-os.yml`,
`benchmark.yml`, `docs.yml`, `gates-binary.yml`, `goldens.yml`, `evals.yml`,
`beta.yml`, `codeql.yml`, `contrib-dryrun.yml`, `docs-site.yml`,
`meas-analyzer-cost.yml`, `meas-build-vcpu.yml`, `meas-jit-aot.yml`) собирают
напрямую через `dotnet build`/`dotnet test` и сборщика не трогают — **их миграция
не нужна и в объём не входит**.

**Чем доказываем.** Джоб `build` в CI зелёный; шаг на строке 1562 (`list`) печатает
тот же каталог; шаг на строке 1354 даёт тот же артефакт `bin/Release/net10.0/`.

**Что может сломаться.** Bootstrap-скрипт — единственное место, где «NUKE» живёт
как имя процесса. Если наш «толстый» bootstrap останется (а он у нас толстый: сам
собирает `_build.csproj` и зовёт `dotnet exec _build.dll`), то **правок в CI не
нужно вовсе** — достаточно поменять пакеты в `_build.csproj`. Это самый дешёвый
вариант и он предпочтителен.

**Как откатой.** Переключение — это изменение **одной** строки в `build.sh`
(или ноль строк в CI). Откат = вернуть строку.

### Шаг 3. Документация и правила

**Что делаем.** Правим 8 документов + комментарии.

| Файл | Что правим |
|---|---|
| `docs/BUILD.md` | 8 упоминаний сборщика: «NUKE» → «Fallout», пути вызова, описание таргетов. |
| `docs/AOT_PUBLISH.md` | 3 упоминания: «NUKE `PublishAot` target», «only a NUKE build». |
| `docs/CENTRAL_PACKAGE_MANAGEMENT.md` | 19 упоминаний: §4.4 «The one project outside CPM» — весь раздел про `Nuke.Common 10.1.0` переписывается под `Fallout.Common 10.4.0` и новые пины. |
| `docs/RELEASE_ENGINEERING_ANALYSIS.md` | 9 упоминаний: «Harbor uses NUKE», «NUKE target», примеры workflow. |
| `docs/BENCHMARKS.md` | 12 упоминаний (в основном — что замеры делались на NUKE-сборке). |
| `docs/REFERENCE-TRIMMER.md` | 2 упоминания. |
| `docs/ALTERNATIVE_UIS.md` | 1 упоминание. |
| `docs/ROADMAP.md` | 1 упоминание (строка 144). |
| `AGENTS.md`, `CLAUDE.md`, `docs/DEVELOPMENT.md`, `docs/ARCHITECTURE.md` | **NUKE не упоминается ни в одном из них** (проверено grep'ом) — править нечего. Но `AGENTS.md` §«Build & test commands» описывает `dotnet build`/`dotnet run --project tests/...`, то есть не оркестратор. **Вывод: эти четыре файла не трогаем.** |
| `Directory.Build.props` (стр. 208, 218, 228, 256) | комментарии «NUKE-driven release builds», «NUKE spelling», «the NUKE tool». |
| `Directory.Packages.props` (стр. 584-601) | блок комментариев про отсутствие пина NUKE. |
| `build/Directory.Build.props` | «NUKE-specific overrides», `<NukeTelemetryVersion>`. |
| `build/_build.csproj` | длинный комментарий про то, «где решается версия NUKE» — переписать. |

**Итого: 8 документов + 3 файла конфигурации.** Строк — порядка 60-80 (это оценка
по объёму правки, не измерение).

**Чем доказываем.** `./tools/check-md-links.py` и `./tools/md-lint.py` — оба в
зеленом; `check-md-links` ловит битые ссылки на переименованные файлы.

**Что может сломаться.** `docs/CENTRAL_PACKAGE_MANAGEMENT.md` — документ, который
**обязан** совпадать с `Directory.Packages.props` и `build/_build.csproj`. Если
комментарии в трёх местах разъедутся, это расхождение надо закрыть в том же PR.
Есть готовый инструмент для проверки: `tools/check-abstractions-contract.py`
(уже проверяет пакеты `src/`), но для `build/` аналога нет — это в объём шага 5 не
входит, фиксируем как известный долг.

**Как откатить.** Правки документации обратимы независимо от кода.

### Шаг 4. Удаление старого

**Что делаем.** Удаляем NUKE-путь:
- каталог `build/_build/` (32 файла `.cs`, 3 718 строк);
- `build/_build.csproj` → переименовывается/заменяется на fallout-версию;
- каталог `.nuke/` (уже переименован в `.fallout/` на шаге 1);
- `Nuke.Common` из `build/_build.csproj` (на шаге 1);
- `NUKE_TELEMETRY_OPTOUT` из `build/build.sh` и `build/build.ps1`;
- `<NukeTelemetryVersion>` из `build/_build.csproj` и `build/Directory.Build.props`.

**Проверка «ни одного упоминания NUKE не осталось»** — это работа гейта шага 5.

**Чем доказываем.** Полоса CI зелёная; `grep -rn "Nuke" build/ .github/ Directory.*.props`
пусто.

**Что может сломаться.** Если что-то в CI ещё зовёт `build/_build.csproj` напрямую —
grep покажет. Проверено: `build/_build.csproj` упоминается в CI только в комментарии
(строка 1485), не в команде.

**Как откатой.** Откат шага 4 = возврат шага 1-3 (всё это в git). **Это единственный
необратимый на вид шаг, поэтому он идёт последним и только после того, как шаги 0-3
прожили полосу.**

### Шаг 5. Гейт, который не даёт вернуться к NUKE

**Что делаем.** Новый гейт в дереве гейтов: `tools/gates/check-no-nuke/`
(`Run(args []string) int`), регистрация в `tools/gates/main.go` **по алфавиту**,
сборка `bash tools/build-gates.sh`, оба файла `tools/bin/*` в коммите.

**Что проверяет (два независимых правила):**

1. **В дереве нет NuGet-ссылок на NUKE.** Ищет `PackageReference` c `Nuke.*` и
   `using Nuke.*` в `build/`, `src/`, `apps/`, `tests/`, `tools/`, `samples/`.
   Краснеет на любом вхождении.
2. **Workflow-файлы зовут новую точку входа.** Ищет `dotnet nuke`,
   `NUKE_TELEMETRY_OPTOUT`, `.nuke/temp` в `.github/workflows/**` и в
   `build/build.sh`, `build/build.ps1`. Краснеет на любом вхождении.

**Почему именно так.** Это ровно тот приём, который в этом репозитории уже применён
к `Microsoft.NET.Test.Sdk`: `tools/check-no-test-sdk.py` не даёт вернуться к VSTest.
Схема та же: правило зашито в инструмент, а не в память ревьюера.

**Чем доказываем.** Гейт краснеет на дереве до миграции (невакуумность) и зеленеет
после. Обе проверки должны иметь позитивный контроль — как того требует подход
[./ARCH_RULES.md](./ARCH_RULES.md) для архитектурных гвардов (у каждого правила
объявлен положительный контроль и нижняя граница обнаружения).

**Что может сломаться.** Ложное срабатывание: строка «NUKE» в историческом документе
(`docs/.kilo-docs/`, `docs/audit-archive/`) не должна считаться. Гейт должен
сканировать только живые пути и иметь список исключений, записанный в гейт, а не
в конфиг.

**Как откатой.** Гейт — аддитивный; красный гейт можно временно сузить списком
исключений, но не выключить.

### Схема зависимостей

```
Шаг 0 (пилот Pack)      — ни от чего не зависит; ничего не ломает
   │
Шаг 1 (весь build/_build) — блокируется #0
   │
Шаг 2 (CI + bootstrap)    — блокируется #1
   │
Шаг 3 (документация)      — независим от #1; можно параллельно
   │
Шаг 4 (удаление NUKE)     — блокируется #1, #2, #3
   │
Шаг 5 (гейт)              — блокируется #4 (иначе гейт красный с первого дня)
```

---

## 4. Смета по нашему коду, в числах

### 4.1 Объём кода

| Что | Число | Как получено |
|---|---:|---|
| файлов `.cs` в `build/_build/` | **32** | `find build/_build -name '*.cs'` |
| строк в них | **3 718** | `cat \| grep -c ''` |
| из них меняются (правка `using`-ов) | **20** | `grep -rl Nuke build/_build/` |
| из них не меняются | **12** | `find build/_build -name '*.cs'` минус те, что содержат `Nuke` |
| таргетов | **15** | `grep -cE 'private (new )?Target [A-Za-z]+' build/_build/Build.cs`; столько же записей в `TargetCatalog` |
| из них таргетов сборки | **11** | `Clean`, `Restore`, `Compile`, `Test`, `ArchitectureTests`, `Publish`, `PublishArchive`, `Release`, `Pack`, `PublishIpcServer`, `PublishIpcClient` |
| из них мета-команд | **4** | `Help` (скрывает член базового класса — `private new Target Help`), `List`, `Doctor`, `What` |
| файлов конфигурации сборщика | **5** | `_build.csproj`, `build/Directory.Build.props`, `build/build.sh`, `build/build.ps1`, `.nuke/` |
| строк в `_build.csproj` | 105 | `wc -l` |
| distinct NuKE-идентификаторов, которые мы используем | **7** | `grep -rho "Nuke\.[A-Za-z.]*"` |
| уникальных `using Nuke.*` | **4** | `Nuke.Common`, `Nuke.Common.IO`, `Nuke.Common.ProjectModel`, `Nuke.Common.Tools.DotNet` (33 строки `using` всего) |
| пакетных пинов, которые надо пересмотреть | **4** | `NuGet.Packaging` (поднять), `Microsoft.Build*` (оставить), `System.Security.Cryptography.Xml` (оставить), `Nuke.Common` (заменить) |

### 4.2 Места в CI

| Что | Число |
|---|---:|
| workflow-файлов всего | **17** |
| суммарно строк в них | **9 691** |
| из них **исполняемых** вызовов `build/build.sh` | **2** (оба в `ci.yml`: строки 1354 и 1562) |
| комментариев, упоминающих сборщик | 3 (в `ci.yml`) |
| джобов всего (блоков под `jobs:`) | **63** |
| джобов в `ci.yml` / в `docs.yml` | **13** / **21** |
| `runs-on:` | **63** (по одному на джоб — сходится) |
| `needs:` | **19** (в 9 файлах) |
| `permissions:` на уровне workflow / джоба | 8 / **53** |
| `if:` на уровне джобы / шага | **21** / 25 |
| `strategy:` / `matrix:` | **5** / 5 |
| `outputs:` на уровне джобы / шага | **7** / 7 |
| `env:` на уровне workflow / джобы / шага | 8 / **5** / 22 |
| ссылок на `secrets.` | 2 |
| ссылок на `upload-artifact` | 36 |
| шагов | **364** (302 с `- name:` + 62 с `- uses:`) |
| строк в `run:`-блоках (непустых, без комментариев) | **1 700** (17,5 % от 9 691) |

### 4.3 Документация

| Что | Число |
|---|---:|
| документов в `docs/`, описывающих сборщик | **8** |
| упоминаний сборщика в них | 55 (`build.sh` / `build/_build` / `NUKE`) |
| ADR в `docs/adr/` про сборщик | **0** (не было) — будет ADR-023 |
| файлов конфигурации с упоминанием NUKE в комментариях | **3** (`Directory.Build.props`, `Directory.Packages.props`, `build/Directory.Build.props`) + `build/_build.csproj` |

### 4.4 Трудозатраты

**Это оценка по объёму правки, а не измерение.** Никто не выполнял этот переезд;
цифры ниже — это разложение объёма (файлы, строки, места вызова) на часы по
средней скорости правки.

| Шаг | Оценка | Что входит |
|---|---:|---|
| 0. Пилот `Pack` | **0,5-1 день** | второй build-проект, сравнение argv/состава/выхода, шаг в джобе `pack` |
| 1. Перенос `build/_build/` | **1-2 дня** | 20 файлов с правкой `using`, пересборка 15 таргетов, сверка каталога 1:1, переписывание `_build.csproj` и пинов |
| 2. CI и bootstrap | **0,5 дня** | 2 точки вызова, 3 комментария, 2 скрипта |
| 3. Документация | **0,5-1 день** | 8 документов, 3 файла конфигурации, прогон `check-md-links` и `md-lint` |
| 4. Удаление старого | **0,25 дня** | удаление каталога, чистка конфигурации, зелёная полоса |
| 5. Гейт | **1-2 дня** | Go-гейт, регистрация, тесты, self-test, пересборка бинаря, запись в `docs/CI_GATES.md` и `tools/README.md` |
| **Итого** | **3,5-7,5 дней** | плюс время на прогон CI между шагами |

**Что меняет факт «в `build/` можно свободно брать пакеты».** Смету **шага 1 это не
двигает**: перенос 20 файлов — это переименование пространств имён, а не
переписывание логики, и библиотека тут не помогает. Свободный выбор пакетов влияет
на стоимость **расширений**, а не самого переезда:

| Что | Стоимость | Комментарий |
|---|---|---|
| перенос 32 файлов на API fallout | **1-2 дня**, как в таблице | не зависит от пакетов: меняются `using`, имена типов и один пин |
| добавление пакета в `build/_build.csproj` | **одна строка** (CPM выключен, версия инлайном) | запись в `Directory.Packages.props` **не нужна** — см. §5.4 |
| цена транзитивного дерева нового пакета | **не измерена**; гейта на дерево зависимостей сборщика сегодня нет | `check-test-packages` покрывает тесты и сэмплы, не `build/` — это новый долг |
| «разбор YAML делается библиотекой, а не руками» | экономия **в расширениях**, не в переезде | в Go-гейте YAML пришлось бы писать руками: `docs/CI_GATES.md` запрещает гейтам внешние модули (§5.5) |

**Итого: смета переезда не меняется — 3,5-7,5 дней.** Свобода в пакетах не удешевляет
перенос и не удорожает его; она делает возможными будущие расширения сборщика без
правки `Directory.Packages.props`.

**Если бы мы всё-таки взяли Go-инструмент рядом с гейтами** (генератор CI-конфигурации
или иной), смета выросла бы на:

| Доп. работа | Оценка | Почему |
|---|---|---|
| `vendor/` + переход на `-mod=vendor` | **0,5-1 день** | правило «stdlib only» пришлось бы переписать в `docs/CI_GATES.md` и `tools/gates/README.md` — это изменение правила для всех гейтов; `GOFLAGS=-mod=mod` в `tools/build-gates.sh` пришлось бы поменять, иначе `verify-committed` (с `cache: false`) начнёт тянуть модуль из сети и сверка байтов станет зависеть от прокси |
| новый гейт-пакет + регистрация + self-test + пересборка бинаря | **1-2 дня** | уже учтено в строке «5. Гейт» |
| рост бинаря | **проекция**, не измерено | YAML-парсер в Go добавил бы сотни килобайт к 6 МБ; точное число зависит от библиотеки |

**Без `vendor/` (рекомендуемый путь — Go без внешних зависимостей) эти затраты не
возникают вовсе**, кроме самого гейта.

Стоимость ошибки: **остановленная полоса CI**. Поэтому порядок шагов важнее
трудозатрат: пилот ничего не ломает, переключение — одна точка входа, удаление —
последнее.

---

## 5. Что НЕ переезжает: граница YAML

### 5.1 Что вообще невозможно выразить в C#

| Конструкция | Почему не переезжает |
|---|---|
| `on:` (событие, ветки, пути) | Решение **GitHub** о том, когда запускать workflow. Сервер решает, а не клиент. |
| `runs-on:` | Выбор раннера — тоже решение GitHub: пул, тип машины, self-hosted. |
| `permissions:` | Токен выдаёт GitHub, набор прав — его политика. |
| `secrets.` | Секреты живут в GitHub; передача — через окружение раннера. |
| публикация артефактов и их подпись | `actions/upload-artifact` + подпись — операция GitHub. |
| `needs:` как топология | Граф зависимостей джобов вычисляет планировщик GitHub. |
| required checks в ruleset | Настройка GitHub — **но она лежит в репозитории файлом**: `.github/rulesets/dev.json` и `.github/rulesets/master.json` перечисляют 8 обязательных проверок по именам (`context`). То есть это ревьюится в PR, а не живёт в настройках сервера. |

### 5.2 Что fallout умеет генерировать — и чего не умеет

На странице
[docs.fallout.build/docs/cicd/github-actions](https://docs.fallout.build/docs/cicd/github-actions)
сказано дословно:

> **Configuration Generation**
>
> You can generate workflow files from your existing target definitions by
> adding the `GitHubActions` attribute. For instance, you can run the `Compile`
> target on every push with the latest Ubuntu image:
>
> ```csharp
> [GitHubActions(
>     "continuous",
>     GitHubActionsImage.UbuntuLatest,
>     On = new[] { GitHubActionsTrigger.Push },
>     InvokedTargets = new[] { nameof(Compile) })]
> class Build : FalloutBuild { /* ... */ }
> ```
>
> Generated output
>
> Whenever you make changes to the attribute, you have to run the build at least
> once to regenerate the workflow file.

Что именно пишет генератор — по исходникам форка
(`src/Fallout.Common/CI/GitHubActions/`), и что при этом **не** пишется:

| Конструкция | Пишет генератор? | Где в исходниках | Сколько таких строк у нас |
|---|---|---|---:|
| `name:` workflow'а | да | `GitHubActionsConfiguration.Write` | — |
| `on:` (push / pull_request / workflow_dispatch / schedule / cron, ветки, пути, теги) | да | там же, строки 35-48 | **299** |
| `env:` на уровне workflow | да | строка 49 | **8** |
| `permissions:` на уровне workflow | да | строки 59-62 | (см. ниже) |
| `concurrency:`, `defaults:` | да | строки 69, 93 | — |
| `jobs:` | да | строка 106 | — |
| `runs-on:` (из `Image` или `RunsOnLabels`) | да | `GitHubActionsJob.Write` | **63** |
| `timeout-minutes:`, `environment:`, `concurrency:` (на джоб) | да | `GitHubActionsJob.Write` | — |
| шаги: checkout, cache, setup-dotnet, `dotnet tool restore`, `dotnet fallout <targets>` | да | `GitHubActionsRunStep.Write` и соседи | — |
| артефакты (`.Produces("*.nupkg")`) | да | `GitHubActionsArtifactStep` | — |
| `env:` на уровне шага, `if:` шага, `continue-on-error`, `timeout-minutes` шага | да | `GitHubActionsCustomStep` | 22 / 21 / 3 |
| **`needs:` (топология джобов)** | **НЕТ** | grep по `src/Fallout.Common/CI/GitHubActions/`: ни `Needs`, ни `"needs"` | **19** |
| **`strategy:` / `matrix:` (на джоб)** | **НЕТ** | там же | 5 / 5 |
| **`if:` на уровне джобы** | **НЕТ** (только на шаге) | `GitHubActionsJob.Write` пишет 6 ключей: `name`, `runs-on`, `timeout-minutes`, `concurrency`, `environment`, `steps` | **21** |
| **`permissions:` на уровне джобы** | **НЕТ** (только на уровне workflow) | там же | **53** |
| **`outputs:` на уровне джобы** | **НЕТ** | там же | **7** |
| **`env:` на уровне джобы** | **НЕТ** (есть на уровне workflow и шага) | там же | **5** |
| `container:`, `services:` | НЕТ | — | 0 (нам не нужно) |

И главное ограничение, которое не про синтаксис, а про **имя джоба**:

```csharp
// src/Fallout.Common/CI/GitHubActions/Configuration/GitHubActionsJob.cs
public string Name { get; set; }
...
writer.WriteLine($"{Name}:");        // ← ключ джоба
writer.WriteLine($"name: {Name}");   // ← отображаемое имя
```

Одна строка `Name` даёт **и** ключ джоба, **и** его `name:`. А
`GitHubActionsAttribute.GetJobs` (строка 314) возвращает **один** джоб, имя которого
выведено из образа раннера: `Name = image.GetValue().Replace(".", "_")` — то есть
`ubuntu-latest` → `ubuntu_latest`. Один атрибут → один workflow-файл → **один**
джоб с машинным именем.

**Почему это ломает наш случай.** Обязательные проверки в ruleset называются
(список владельца, сверен с именами джобов в дереве):

| Обязательная проверка | Job id в дереве | Совпадают? |
|---|---|---|
| `gate` | `gate` (ci.yml) | да |
| `public-api freeze` | `public-api` (ci.yml) | **нет** |
| `checker self-test` | `selftest` (docs.yml) | **нет** |
| `internal links` | `links` (docs.yml) | **нет** |
| `markdown lint` | `lint` (docs.yml) | **нет** |
| `normative citations` | `cites` (docs.yml) | **нет** |
| `tree gates` | `tree` (docs.yml) | **нет** |
| `test project coverage` | `testprojects` (docs.yml) | **нет** |

GitHub требует, чтобы job id соответствовал `[a-zA-Z_][a-zA-Z0-9_-]*` — **пробел в
id невозможен**. Значит проверку с именем `checker self-test` нельзя получить от
генератора, который выводит id и имя из одной строки: либо id станет
`checker-self-test` (и тогда имя проверки в ruleset надо менять), либо имя станет
`checker-selftest` (и тогда требуемая проверка перестаёт находиться). **Это не
недостаток покрытия, это блокер для замены наших workflow'ов генератором.**

Вдобавок: 6 из 8 обязательных проверок живут в `docs.yml`, 2 — в `ci.yml`, то есть
это **два** разных файла, а генератор на атрибут пишет **один**.

**И ещё одно, что меняет рамку разговора: этот генератор есть и в NUKE.**
`source/Nuke.Common/CI/GitHubActions/GitHubActionsAttribute.cs` в NUKE 9.0.4/10.1.0
устроен так же — та же строка `ConfigurationFile => Build.RootDirectory /
".github" / "workflows" / $"{_name}.yml"`. То есть **генератор не является причиной
переезда**: он есть в обеих сборках, и переезд на fallout даёт не его, а точку
вставки своих шагов (`IConfigureGitHubActions.ConfigureSteps`) и пининг версий
actions. Это подтверждено и отчётом соседней задачи [#2185].

#### Сверка с отчётом соседней задачи [#2185]

Отчёт [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185) («генерировать
ли workflow-файлы сборщиком») измерял ту же границу на `dev` = `a55ea18c`. Наши
выводы **совпадают по существу**, расхождения — в датах замера и в способе счёта:

| Величина | [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185) (`a55ea18c`) | здесь (`82c7a1d0`) | Расхождение |
|---|---:|---:|---|
| `permissions:` на уровне джоба | 52 | **53** | dev сдвинулся на один джоб |
| `if:` на уровне джоба | 20 | **21** | то же |
| `needs:` | 18 | **19** | то же |
| `outputs:` на уровне джоба | 7 | **7** | совпало |
| `strategy:` | 5 | **5** | совпало |
| `env:` на уровне джоба | 5 | **5** | совпало |
| джобов | 62 | **63** | dev сдвинулся |
| строк в workflow-файлах | 9 656 | **9 691** | +35 |
| шагов | 362 | **302** с `- name:` + **62** с `- uses:` = **364** | разный способ счёта |

**Расхождений по существу нет:** список конструкций, которых генератор не пишет,
совпадает полностью, и вывод о модели джоба совпадает — имя джоба выводится из
образа раннера, поэтому обязательную проверку `gate` (а равно и остальные семь, у
которых имя отличается от job id) получить невозможно.

Одно дополнение с их стороны, которое стоит принять: **сегодня ничто не проверяет,
что эти восемь имён на месте** — `.github/rulesets/**` не читает ни один гейт, ни
один скрипт, ни один workflow. То есть правило «required checks в ruleset»
декларировано файлом, но **не охраняется**. Это независимая от переезда находка и
годится как отдельная задача.

**Вывод.** Генератор fallout'а покрывает «цель → пара шагов в одном джобе». Наш CI —
это **63 джоба в 17 файлах** с топологией `needs`, матрицами, джобными `if:`
и `permissions:`, с 8 обязательными проверками, имена которых не совпадают с
именами джобов. Подход, при котором мы описываем workflow в атрибуте, потребовал бы
либо расширения самого форка (7 недостающих конструкций + раздельные id и имя
джоба), либо ручной правки сгенерированного файла — а тогда генерация становится
источником расхождений.

**Поэтому в объём этого переезда генерация workflow-файлов НЕ входит.** План трогает
**только точки вызова** в CI (шаг 2). Где ей жить, если её вообще делать, —
отдельный вопрос, разобранный в §5.5.

**Пересекается с [#2185](https://github.com/refusedguy/Harbor-Harness/issues/2185)**
(«генерировать ли workflow-файлы сборщиком») — это отдельная задача, и наши замеры
дают ей точный список препятствий: **7 конструкций, которых генератор не пишет, и
одна архитектурная — id и имя джоба связаны одной строкой.** Сверка этого списка с
отчётом соседнего агента (когда он появится) — предмет [#2185], а не этого PR.

### 5.3 Минимальная YAML-обвязка, которая останется

Считаем по замеру на `dev` = `82c7a1d0`. Считаются **непустые и некомментарные**
строки, иначе число зависит от того, как посчитан.

| Конструкция | Строк | Пишет генератор fallout? | Остаётся руками |
|---|---:|---|---:|
| блоки `on:` (триггеры, ветки, пути, cron) | **299** | 299 | 0 |
| блоки `permissions:` (workflow + джоб, заголовок + тело) | **142** | только workflow-уровень | **53 джобных** |
| `runs-on:` | **63** | 63 | 0 |
| `needs:` | **19** | **0** | **19** |
| заголовки джобов (ключ + `name:`) | **94** | 94 | 0 |
| **Итого «решётки»** | **617** | — | **72** |
| `run:`-блоки (содержимое шелл-скриптов) | **1 700** | 0 | уезжают в C# |
| остальное (шаги, env, кэши, `with:`, комментарии) | 7 374 | — | — |

**Числа, которые надо помнить:**

- **Решётка, то есть то, что решает GitHub, а не клиент:** **617 строк из 9 691**
  — **6,4 %**. Из них руками остаётся **72** (19 `needs:` + 53 джобных
  `permissions:`).
- **Шелл-код, который логично унести в C#:** **1 700 строк из 9 691** — **17,5 %**.
  Это весь выигрыш от «перенести CI в C#», и он меньше, чем кажется: остальное —
  YAML, который остаётся YAML.
- Практический ориентир по файлам: минимальная решётка — от **3 строк**
  (`goldens.yml`: 1 джоб, 0 `needs:`, 0 джобных `permissions:`) до **~60 строк**
  (`ci.yml`: 13 джобов, 9 `needs:`, 12 джобных `permissions:`, 13 `runs-on:`,
  19 заголовков).

### 5.4 Правила зависимостей по каталогам

Сведено из дерева, файл за файлом. Это отвечает на вопрос «разрешено ли сборщику
брать пакеты» и «что делать с CPM».

| Каталог | Какие правила на него распространяются | Можно ли брать зависимости | Чем проверяется |
|---|---|---|---|
| `src/`, `apps/`, `tests/` | корневой `Directory.Build.props` (анализаторы, AOT/трим, banned-API, CPM **включён**). `src/`, `apps/`, `tests/` **импортируют родителя** — по одному `Import Project` в каждом | **да**, но версия обязана лежать в `Directory.Packages.props`; `Version=` в самом `.csproj` — ошибка NU1008 | `tools/gates/check-abstractions-contract` (периметр: `Harbor.Abstractions.Contracts`, `Harbor.Extensions`, `Harbor.Application`, `Harbor.PolicyConfig`), матрица слоёв в `tests/Harbor.Architecture.Tests/`, `check-analyzer-scope`, `check-no-test-sdk`, `check-test-packages` |
| `tools/` (C#: `Harbor.Evals`, `DesignSystemDocGen`) | тот же корневой props — своих `Directory.Build.props` у `tools/` нет, значит файл ищется вверх и доходит до корня | **да**, версия в `Directory.Packages.props` | restore; NU1008 при `Version=` в `.csproj` |
| **`build/`** (сборщик) | **корневой props НЕ применяется.** `build/Directory.Build.props` **не импортирует родителя** (0 `Import Project`) — комментарий в нём: *«We do NOT import the parent Directory.Build.props — the build project is a tool, not part of the solution proper, and inheriting solution-wide strictness would force analyzers / TreatWarningsAsErrors onto code we don't own.»* CPM **выключен в двух местах** — `build/Directory.Build.props:39-40` и `build/_build.csproj` (в PropertyGroup, который выигрывает по порядку импорта). Анализаторы off, `TreatWarningsAsErrors` off, `GenerateDocumentationFile` off | **да, свободно.** Версии ставятся прямо в `build/_build.csproj`; запись в `Directory.Packages.props` для сборщика **не нужна и не должна появляться** — она была бы мёртвым текстом (ровно та ошибка, которую #1896 убрал для `Nuke.Common`) | restore должен проходить; правило зафиксировано **документом** — `docs/CENTRAL_PACKAGE_MANAGEMENT.md` §4.4 — а не гейтом. Автоматического гейта на «версия сборщика в одном месте» нет |
| **`tools/gates/`** (Go) | отдельный Go-модуль `harbor.local/gates`, `go 1.27.3`, **ноль внешних `require`**. Правило репозитория: `docs/CI_GATES.md` правило 6 — *«with the standard library only and no external module»*; там же — *«The sources use the standard library only, and there is no `go.sum`: a gate that fetched something at run time could not run on a runner with no network»*; `tools/gates/README.md` правило 4 — то же | **нет.** Внешний модуль запрещён правилом, а не только неудобен | `go-quality` (gofmt, `go vet`, регистрация, полы `--min-files 75 --min-packages 28`); `gates-binary` → джоб `verify-committed` пересобирает бинарь и сверяет байты; `--verify` доказывает воспроизводимость |
| `contrib/` | не собирается CI, правила решения на него не распространяются | — | — |

**Ответ на вопрос про CPM.** `Directory.Packages.props` — **один файл на весь
репозиторий**, и MSBuild подхватывает его для любого проекта, включая `build/`
(импорт идёт через `Microsoft.Common.props` → `NuGet.props`). Но CPM **выключен**
для `build/_build.csproj`, и потому `PackageVersion`-записи этого файла к сборщику
**не применяются**. Следствия:

1. **Версия сборщика живёт только в `build/_build.csproj`.** Ровно «the one project
   outside CPM» (§4.4 CPM-гайда). Правило «версия живёт в одном месте» тут
   буквальное: добавление `Fallout.Common` в `Directory.Packages.props` было бы
   мёртвым текстом.
2. **NU1605 при CPM выключенном — это про прямой `PackageReference`.** Наш пин
   `NuGet.Packaging 6.12.5` ниже транзитивного требования `Fallout.Tooling`
   (6.14.3) → ошибка restore, а не предупреждение. Лечится поднятием пина до
   **6.14.3**; `CentralPackageTransitivePinningEnabled` это не спасёт, потому что
   он выключен и для `build/`.
3. **Свободный выбор пакетов в `build/` — да, но с оговоркой о гейтах.** Правила
   AOT, запретов и слоёв к сборщику не относятся (он вне матрицы слоёв и вне
   периметра `check-abstractions-contract`), CPM на него не распространяется, так
   что новый пакет — это одна строка в `build/_build.csproj`. **Но:** пакет придёт
   со своим транзитивным деревом, и ни один гейт сейчас не смотрит на дерево
   зависимостей сборщика — `check-test-packages` (периметр — тесты и сэмплы) его
   не покрывает. Это новый долг, а не бесплатный выигрыш.

### 5.5 Где жить генерации CI-конфигурации

Вопрос: **(а)** в сборщике на fallout (C#, зависимости свободны) или **(б)** в
`tools/gates/` (Go; внешняя зависимость потребовала бы `vendor/`, а джоб
`gates-binary` сверяет байты)?

**Рекомендация: (б), Go, в `tools/gates/`, и без внешних зависимостей.**

Почему не (а), с числами:

- Гейт должен срабатывать **там, где нет SDK и нет сборки дерева** — это правило
  репозитория (`docs/CI_GATES.md`), и ради него гейты и стали компилируемым
  бинарём в git (5.9 МБ arm64, 6.3 МБ x64, `tools/bin/`). C#-генератор требует
  restore + сборку оркестратора: в lanes документации и в PR, ломающем сборку, он
  не запустится — то есть **не сработает там, где расхождение и должно ловиться**.
- C#-генератор жил бы в том самом проекте, который мы переносим. Ошибка в сборщике
  не должна ломать определение CI: это разные вещи с разными периодами жизни.

Почему (б) — в Go, а не в C#, и **без `vendor/`, потому что внешняя зависимость
запрещена правилом репозитория**:

- `docs/CI_GATES.md` правило 6 и §«The gate is compiled»: *«with the standard
  library only and no external module»*, *«there is no `go.sum`: a gate that fetched
  something at run time could not run on a runner with no network»*.
  `tools/gates/README.md` правило 4 — то же самое.
- Значит `vendor/` — не «способ обойти», а **нарушение документированного правила**:
  чтобы его завести, пришлось бы переписать `docs/CI_GATES.md` и
  `tools/gates/README.md`, то есть изменить правило для **всех** гейтов репозитория.
  Это решение на порядок крупнее, чем «добавить генератор».
- **Практический выход: YAML в Go пишется руками.** Генератору не нужен парсер —
  он **выдаёт** фиксированную форму, как `gen-seam-graph` выдаёт markdown руками.
  Тогда внешних зависимостей нет, `go.mod` остаётся пустым, `vendor/` не нужен,
  воспроизводимость байтов не меняется, и ни одно документированное правило не
  трогается.

Затраты варианта (б) — **в форме изменений, а не в байтах**:

| Что меняется | Где | Цена |
|---|---|---|
| новый пакет гейта | `tools/gates/<name>/` | один новый каталог, регистрация в `tools/gates/main.go` по алфавиту |
| пол числа пакетов | `.github/workflows/gates-binary.yml`, `--min-packages 28` → 29 | одна цифра |
| пол числа файлов | там же, `--min-files 75` | одна цифра, если число файлов вырастет |
| бинарь | `tools/bin/harbor-gates-linux-{x64,arm64}` + `SHA256SUMS` | пересборка, ~6 МБ, как сейчас |
| `go.mod` | `tools/gates/go.mod` | **не меняется** — зависимостей нет |
| `GOFLAGS` в `tools/build-gates.sh` | строка `GOFLAGS=-mod=mod` | **не меняется** — при нулевых зависимостях `-mod=mod` герметичен |
| сверка «коммитированное = пересобранное» | `gates-binary.yml`, джоб `verify-committed` | работает как сейчас: `actions/setup-go@v6` с `cache: false`, но с `go.mod` без `require` сети не нужно вовсе |

**Риски варианта (б):**

- **R16.** Генератор workflow-файлов становится **вторым источником правды** о
  топологии CI: 63 джоба с именами, `needs`, матрицами, `if:` и `permissions:`.
  Если генератор не покрывает всё, остаток правится руками — и «сгенерированный»
  файл снова расходится с коммитированным. Мера: гейт-сверка, как у
  `gen-seam-graph` (генератор пишет `--write`, CI падает, когда коммитированная
  копия устарела).
- **R17.** Время и риск выше, чем выигрыш: генератор должен закодировать нашу
  модель джобов, а выигрыш — **1 700 строк шелла (17,5 %)**, из которых часть
  (кэши, матрицы, разбор вывода) всё равно остаётся в YAML.
- **R18.** Если правило «stdlib only» когда-нибудь смягчат, `vendor/` станет
  возможен — но тогда `GOFLAGS=-mod=mod` в `tools/build-gates.sh` придётся менять
  на `-mod=vendor`, иначе джоб `verify-committed` (с `cache: false`) начнёт тянуть
  модуль из сети, и сверка байтов станет зависеть от состояния прокси. **Это
  надо знать заранее, а не в момент, когда первая зависимость уже сломала
  воспроизводимость.**

**Итог по генератору: сейчас — не делаем.** Fallout-генератор закрывает «цель →
пара шагов», а не нашу модель из 63 джобов; если делать — то свой, на Go, в
`tools/gates/`, по образцу `gen-seam-graph`, без внешних зависимостей, и отдельной
задачей. В объём переезда сборщика это не входит.

---

## 6. Риск-реестр

| # | Риск | Вероятность | Цена | Мера |
|---|---|---|---|---|
| R1 | **Полоса CI перестаёт стартовать** | средняя | критическая | Пилот (шаг 0) добавляет, ничего не заменяет. Переключение — одна точка входа. Удаление — последним. Каждый шаг имеет явную откатку. |
| R2 | NUKE-версия `10.4.0` fallout'а ведёт себя иначе в 15 таргетов | средняя | средняя | Шаг 0 проверяет `Pack`; шаг 1 проверяет все 15 через argv + exit code + `list` 1:1. |
| R3 | **`NuGet.Packaging 6.12.5` → NU1605, restore не проходит** | **высокая** (уже найдено) | средняя | Поднять пин до 6.14.3 в шаге 1. Проверка: restore `build/_build.csproj` должен пройти. |
| R4 | Другая модель кэша / другая нумерация версий | средняя | средняя | Версии fallout'а — semver, `10.4.0` — стабильная. Нумерация CI-артефактов (`build-<run>`) — наша, не fallout'а. Проверяется первым прогоном `pack`. |
| R5 | Другие пути к артефактам (`.nuke/` → `.fallout/`) | **высокая** (уже известно) | низкая | Переименование каталога — шаг 1. Пути к артефактам (`artifacts/publish/...`) задаёт наш `ArtifactPathResolver`, не fallout. |
| R6 | **Лицензия** | — | — | **Проверено: MIT.** `LICENSE` в репозитории fallout'а — MIT, с атрибуцией NUKE; `.nuspec` — `<license type="expression">MIT</license>`. Совместима с MIT-частью нашего дерева. Риска нет. |
| R7 | `.slnx` не поддерживается в выбранной версии | **низкая** | высокая | **Подтверждено:** `Fallout.Persistence.Solution` (10.4.0 и 11.0.18) содержит `SlnxFile`/`SlnxSerializerSettings`. Плюс NUKE issue #1586 (`.slnx` в NUKE) остался без ответа мейнтейнера — то есть fallout здесь ближе к нужному. |
| R8 | `.NET 10` не поддерживается | **низкая** | высокая | **Подтверждено:** `lib/net10.0/` — единственный TFM у `Fallout.Common`. |
| R9 | NativeAOT-публикация ломается | средняя | средняя | Наш код ставит `PublishAot` как MSBuild-свойство, а не типизированным сеттером, — поведение не зависит от API. Но **прямого подтверждения нет**: шаг 0 обязан проверить `Publish` с `PublishVariant.AOT` и сравнить бинарник. |
| R10 | `DotNetTasks` не переброшен в shim (issue #69) | **подтверждено** | средняя | Shim-путь не используем; идём через `fallout-migrate`. |
| R11 | Пространство имён разъезжается между 10.x и 11.x (`Fallout.Common.ProjectModel` vs `Fallout.Solutions`) | **подтверждено** | низкая | Фиксируем версию **10.4.0** в `build/_build.csproj`; гейт шага 5 фиксирует, что пин не поедет. |
| R12 | Fallout'а никто не использует (103 тыс. скачиваний против 39,9 млн) | **подтверждено** | высокая | Это настоящий риск. Меры: пинить версию; не полагаться на `develop`; иметь план отката (NUKE-дерево остаётся в git). Осознанное решение владельца, а не случайность. |
| R13 | `fallout-migrate` не справится с нашим кодом (32 файла, не шаблон NUKE) | средняя | низкая | Инструмент идемпотентен и имеет `--dry-run`. Пилот на 20 файлах с `using`-ами покажет охват. |
| R14 | Транзитивные зависимости fallout'а тянут новые пакеты (`Octokit 14.0.0`, `Azure.Identity 1.17.1`, `YamlDotNet 16.3.0`) | **подтверждено** | низкая | CPM выключен для `build/` — пин-каталог не пострадает. Время restore вырастет; замеряем на шаге 1. |
| R15 | Генерация workflow-файлов втягивает нас в отдельный проект | средняя | высокая | **Явно вне объёма** (п. 5.2, 5.5). В план не входит, решается отдельной задачей. |
| R16 | Генератор CI-конфигурации становится вторым источником правды о топологии: 63 джоба, имена, `needs`, матрицы, `if:`, `permissions:` | высокая | средняя | Генератор обязан покрывать **всё**, иначе остаток правится руками и файл снова расходится. Мера — гейт-сверка по образцу `gen-seam-graph` (генератор с `--write`, CI падает на устаревшей копии). Пока такого гейта нет — генератора нет. |
| R17 | Время и риск генератора больше выигрыша | высокая | низкая | Выигрыш — **1 700 строк шелла (17,5 %)**; остальное — YAML, который остаётся YAML. Решение: **не делаем сейчас**. |
| R18 | Если правило «stdlib only» для гейтов смягчат, `vendor/` + `-mod=vendor` понадобятся, иначе `verify-committed` (с `cache: false`) начнёт тянуть модуль из сети, а сверка байтов станет зависеть от прокси | низкая | средняя | Знать заранее: `tools/build-gates.sh` сейчас собирает с `GOFLAGS=-mod=mod`, и это герметично **только** потому, что `go.mod` пуст. Первая внешняя зависимость без `vendor/` ломает воспроизводимость байтов — и это надо чинить до её добавления, а не после. |

---

## 7. Альтернативы, которые были рассмотрены

| Альтернатива | Почему не выбрана |
|---|---|
| **Остаться на NUKE** | Отклонено фактами: 0 коммитов за 6 месяцев, 324 дня без коммита, ни одного ответа мейнтейнера, 65 открытых issue без разбора, сайт проекта не работает, CVE-отчёт без ответа. Плюс наш билд уже несёт ручные пины, закрывающие долг, который никто не будет чинить. |
| **Cake** | Активен, но это скриптовый DSL (C#-скрипты, не класс-библиотека). Наш `build/_build/` — 32 файла классов с разделением по SRP; переписывать его в Cake-скрипты — потеря типизации и структуры. Не изучался детально; выбран не по этому пункту, а по пункту NUKE. |
| **FAKE / Bullseye / MSBuild-native оркестрация** | Не изучались детально в этом исследовании (в объём не входило). Общий довод: у нас уже есть готовый, разложенный по SRP оркестратор на C#, и Fallout — прямой форк того же самого с сохранённой историей и миграционным инструментом. **Это не измерение, а довод; см. §9.** |
| **Свой оркестратор на MSBuild-таргетах** | MSBuild-таргеты не дают того, что даёт build-фреймворк: параметризации, каталога таргетов, `--dry-run`, разбора артефактов. Наш `BuildOutput`/`DoctorChecks`/`ImpactMapper`/`TargetCatalog` (1 688 строк) пришлось бы переписывать с нуля. |

---

## 8. Что записано в репозитории и какие номера issue

- Отчёт: `docs/BUILDSYSTEM_STUDY.md` (этот файл).
- ADR: [`docs/adr/ADR-023-build-orchestrator-moves-to-fallout.md`](adr/ADR-023-build-orchestrator-moves-to-fallout.md).
- Строка в индексе ADR: `docs/adr/DECISIONS.md`, раздел «ADR-023».
- Эпик: **[#2178](https://github.com/refusedguy/Harbor-Harness/issues/2178)** —
  «Переезд оркестратора сборки с NUKE на Fallout (ADR-023)». В комментарии к
  эпику зафиксировано решение владельца и схема зависимостей.
- Подзадачи по шагам плана:

| Шаг | Issue | Название |
|---|---|---|
| 0 | [#2179](https://github.com/refusedguy/Harbor-Harness/issues/2179) | пилот: один таргет (`Pack`) на fallout, тот же артефакт |
| 1 | [#2180](https://github.com/refusedguy/Harbor-Harness/issues/2180) | перенос всего `build/_build/` на API fallout |
| 2 | [#2181](https://github.com/refusedguy/Harbor-Harness/issues/2181) | точки вызова в CI и bootstrap-скрипты |
| 3 | [#2182](https://github.com/refusedguy/Harbor-Harness/issues/2182) | документация и комментарии в конфигурации |
| 4 | [#2183](https://github.com/refusedguy/Harbor-Harness/issues/2183) | удаление NUKE-пути |
| 5 | [#2184](https://github.com/refusedguy/Harbor-Harness/issues/2184) | гейт, который не даёт вернуться к NUKE |

---

## 9. Что осталось неизмеренным

1. **Время прогона fallout-сборки против NUKE-сборки.** Не измерено: локальный
   `dotnet` в этом окружении запрещён (OOM), сборка измеряется только CI. Сравнение
   будет получено на шаге 0.
2. **Побайтовое равенство `.nupkg`.** Не измерено: зависит от того, что NuGet
   пишет в `metadata`/`.nuspec`. Критерий приёмки сформулирован как «тот же состав
   + те же CRC», а не «тот же SHA-256»; решение примет шаг 0.
3. **Cake, FAKE, Bullseye, MSBuild-native** — не изучались детально. В §7 приведён
   общий довод, а не сравнение по численным критериям. Это сознательный объём:
   решение владельца принято по факту «NUKE заброшен», а не по результатам
   сравнения альтернатив.
4. **Сколько времени займёт CI-прогон каждого шага.** Не измерено; зависит от
   длины очереди и от нагрузки на пулы.
5. **Что именно ломается в `DoctorChecks` (481 строка) и `ImpactMapper` (441 строка)**,
   если они используют NUKE-API, который в fallout'е переименован. По статическому
   чтению — оба файла не содержат `using Nuke.*`, то есть собственного NUKE-API не
   трогают; но фактической компиляции не было.
6. **Поведение `fallout-migrate` на нашем дереве.** Не прогонялось: инструмент
   требует `dotnet` (локальный запрещён). Проверяется в CI на шаге 0.
7. **Полный перечень API-расхождений между `Nuke.Common 10.1.0` и
   `Fallout.Common 10.4.0`.** Сверялась только та поверхность, которую используем мы
   (7 идентификаторов, 20 файлов). Полное сравнение публичного API не делалось.
