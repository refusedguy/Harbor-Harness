using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Evals;

// Source-generation contexts behind issue #1565 batch-4: every
// JsonSerializer.Serialize/Deserialize site in this tool now resolves its
// JsonTypeInfo from one of these contexts instead of the runtime-reflection
// contract. Behavior-identical to the previous per-file options objects, byte
// for byte — property names, declaration/iteration order and indent settings
// are the same the reflection contract produced:
//   - EvalContractJsonContext  — task/profile/constraint FILES: the loaders'
//     former `PropertyNameCaseInsensitive = true` options object.
//   - ArtifactJsonContext      — attempt artifacts (attempt/verdict/manifests/
//     verification/changes/summary/parser-diagnostics): the former
//     `WriteIndented = true` options object, PascalCase names untouched.
//   - EventJsonContext         — events.jsonl lines: DEFAULT (compact) options,
//     deliberately NOT ArtifactJsonContext — an indent flip here would have
//     changed the file shape.
// The old payloads were anonymous types; source-gen cannot speak
// compiler-generated types (nearest-ancestor resolution would emit `{}` under
// an `object` contract), so each became a named record with the identical JSON
// property names and declaration order — the JSON files these contexts write
// are still read back by SummaryBuilder.Resummarize (attempt.json/verdict.json)
// and evals tooling, which key on those exact strings.

// Deserialization roots: evals/tasks/task.json + constraints.json and the
// --profile JSON.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EvalProfile))]
[JsonSerializable(typeof(EvalTask))]
[JsonSerializable(typeof(EvalConstraints))]
internal sealed partial class EvalContractJsonContext : JsonSerializerContext;

// Artifact + summary writers: everything under results/batch/attempt-*/.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SummaryBuilder.BatchSummary))]
[JsonSerializable(typeof(SummaryBuilder.AttemptSummary))]
[JsonSerializable(typeof(ArtifactWriter.AttemptRecord))]
[JsonSerializable(typeof(ArtifactWriter.AttemptVerdict))]
[JsonSerializable(typeof(ArtifactWriter.ParserDiagnostics))]
[JsonSerializable(typeof(ArtifactWriter.ManifestDiff))]
[JsonSerializable(typeof(ArtifactWriter.VerificationReport))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(VerifierRunner.CheckResult))]
internal sealed partial class ArtifactJsonContext : JsonSerializerContext;

// events.jsonl lines — written with DEFAULT options (compact, one object per
// line); a shared WriteIndented context would have changed the file shape.
[JsonSerializable(typeof(EventParser.ParseEvent))]
internal sealed partial class EventJsonContext : JsonSerializerContext;
