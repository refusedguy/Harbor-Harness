Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Security & Sandboxing.

## Context
У Harbor уже есть IPluginTrustPolicy + TrustingPluginSource (fail-closed gate, sha256 path binding,
~/.harbor/plugins/trust.json, approve y/N at startup, docs/ROADMAP.md v0.5.0).
Но плагины загружаются в default AssemblyLoadContext — у них есть полный доступ к File.Delete,
Process.Start, reflection на любой тип (ReflectionPluginInstantiator.cs, §3.8).

Для соло-дева, который ставит плагины из интернета, этого недостаточно.
Нужна defence in depth: ALC sandbox + capability manifest + execution timeout + audit log.
Не "всё или ничего" — гранулярные права, которые пользователь approve'ит один раз.

## Tasks (выполняй по порядку)

1. **CollectiblePluginLoadContext + deny-list.**
   Создай ALC с isCollectible=true, в Load() отказывайся резолвить System.IO.FileSystem,
   System.Diagnostics.Process, System.Net.Http если plugin manifest не declares capability.
   Shared типы (Harbor.Abstractions, Harbor.Plugins.Abstractions) резолви из host ALC.
   Acceptance: плагин, который вызывает File.Delete без declared capability, получает FileNotFoundException
   на момент вызова; alc.Unload() освобождает всю память.

2. **Capability manifest + trust.json v2.**
   Расширь IPluginTrustPolicy: каждый plugin manifest declares capabilities (read_files, write_files,
   run_processes, http_requests, sub_agents, read_env). При установке пользователь approve'ит каждый capability,
   результат пишется в ~/.harbor/plugins/trust.json с path + sha256.
   Acceptance: редактирование .cs файла плагина invalidates trust, требует re-approval при следующем старте.

3. **Plugin execution timeout + memory guard.**
   Wrap каждый plugin tool execution в CancellationTokenSource(30s) + GC.GetAllocatedBytesForCurrentThread
   budget (например 10 MB на tool call). При превышении — прервать, вернуть Result.Failure в agent loop,
   опубликовать PluginBlockedEvent.
   Acceptance: plugin tool с бесконечным циклом убивается через 30s, agent продолжает работать.

4. **Plugin audit log.**
   Пиши каждый capability use в ~/.harbor/logs/plugin-audit.jsonl: timestamp, plugin name, capability,
   target path/URL, result. Append-only, plugins не могут удалить.
   Acceptance: установка GoogleSearch плагина → audit log содержит 1 запись read_files на .cs файл,
   1 запись http_requests на google.com.

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. Fail-closed по умолчанию: неизвестная capability = deny. Никаких allow-by-default.
3. Audit log — append-only. Плагин НИКОГДА не может его удалить или изменить.
4. Не ломай существующий plugin pipeline: CachingCompiler, PluginHostBuilder, Roslyn compile — остаются как есть.
5. После каждого коммита: dotnet test tests/Harbor.Plugins.Runtime.Tests/ -c Release --no-build.

## Deliverables
- CollectiblePluginLoadContext с deny-list + shared-type resolver
- Capability manifest schema + trust.json v2 с per-capability approval
- Plugin tool execution timeout + memory guard (30s / 10 MB)
- Plugin audit log (~/.harbor/logs/plugin-audit.jsonl)
- unit tests: capability deny, timeout kill, audit append, ALC unload leak-free
- security report в `.kilo-docs/sprints/security/report.html`
