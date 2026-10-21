package aotpublish

// selftest.go -- the gate's own proof that it can fail.
//
// Every case below is a fixture where the outcome is guaranteed, and every
// assertion checks the exit code AND the substance of the message, so a
// matcher that fails for the wrong reason cannot pass.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// selftestLog is a publish log whose bytes exceed the floor, so only the
// intended defect can fail it.
const selftestLog = `Using .NET SDK 10.0.302 [/home/runner/work/Harbor-Harness/Harbor-Harness/global.json]
  Determining projects to restore...
  Restored /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Harbor.Application.csproj (in 412 ms).
  Harbor.Application -> /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/bin/Release/net10.0/Harbor.Application.dll
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(88,13): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(91,9): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Compilation.RoslynPluginCompiler(140,22): warning IL3050: The 'PublishAot' analyzer warned: 'call site can cause AOT analysis warnings' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Instantiation.Reflect(9,1): error IL2070: Trim analysis error: 'this' argument does not satisfy PublicParameterlessConstructor [HARBOR_APP_CLI]
Trimmer warnings summary:
  ILLink: 118 warning(s) from 6 assembly(s) — Microsoft.CodeAnalysis.dll, ...
  Total: 118
  {marker} rid={rid} recipe={recipe} status={status} exit={code}
`

// fixture writes a publish log whose bytes exceed the floor, so only the
// intended defect can fail it.
func fixture(rid, recipe, status string, code int, marker string, extraLines []string, body string) string {
	if body == "" {
		body = strings.NewReplacer(
			"{marker}", marker,
			"{rid}", rid,
			"{recipe}", recipe,
			"{status}", status,
			"{code}", fmt.Sprintf("%d", code),
		).Replace(selftestLog)
	}
	var padding strings.Builder
	for i := 0; i < 200; i++ {
		fmt.Fprintf(&padding, "  linker: pass 0x%x ilc: emit rodata\n", i)
	}
	return body + "\n" + padding.String() + "\n" + strings.Join(extraLines, "\n") + "\n"
}

// baselineRows is the committed record the fixtures are measured against.
const baselineRows = "IL2026 | warning | 2 | ACCEPTED | Harbor.Application.Session | " +
	"first-party site, argument recorded in docs/AOT_PUBLISH.md\n" +
	"IL3050 | warning | 1 | ACCEPTED | Harbor.Plugins.Compilation.RoslynPluginCompiler | " +
	"Roslyn needs the JIT; the split point is #419 and the ADR is #418\n" +
	"IL2070 | error | 1 | REJECTED | Harbor.Plugins.Instantiation.Reflect | " +
	"missing DynamicallyAccessedMembers annotation; #48/S4 owns the plugin split point\n"

func selfTestRun() int {
	var failures []string
	checks := 0

	check := func(name string, logPath, basePath, rid, recipe, status string,
		wantExit int, mustContain, mustNotContain []string) {
		checks++
		var lines []string
		code := run(logPath, basePath, defaultMarker, 2000, rid, recipe, status,
			func(line string) { lines = append(lines, line) })
		blob := strings.Join(lines, "\n")
		if code != wantExit {
			failures = append(failures, fmt.Sprintf("%s: exit %d, wanted %d\n    %s",
				name, code, wantExit, blob))
			return
		}
		for _, needle := range mustContain {
			if !strings.Contains(blob, needle) {
				failures = append(failures, fmt.Sprintf(
					"%s: exit was right but the message never said %q\n    %s", name, needle, blob))
			}
		}
		for _, needle := range mustNotContain {
			if strings.Contains(blob, needle) {
				failures = append(failures, fmt.Sprintf(
					"%s: message must not say %q\n    %s", name, needle, blob))
			}
		}
	}

	root, err := os.MkdirTemp("", "aot-gate-selftest-")
	if err != nil {
		fmt.Printf("%s: SELFTEST FAILED — %v\n", toolName, err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	write := func(name, text string) string {
		path := filepath.Join(root, name)
		os.WriteFile(path, []byte(text), 0o644)
		return path
	}
	emptyBaseline := "# nothing emitted yet\n"

	// 1. The green case: the recorded state reproduces exactly.
	check("green_recorded_state",
		write("log1", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, nil, "")),
		write("base1", baselineRows),
		"linux-x64", "fulltree", "failed", 0,
		[]string{"found 3 distinct diagnostic id(s), 4 occurrence(s)",
			"status=failed exit=1", "1 error-severity", "PASS (exit 0)", "REJECTED", "ratchet, not an"},
		nil)

	// 2. THE IMPROVEMENT CASE. The whole point of the ratchet: the publish
	//    succeeding is a CHANGE, and the gate must not absorb it silently.
	check("publish_starting_to_succeed_is_still_red",
		write("log2", fixture("linux-x64", "fulltree", "published", 0, defaultMarker, nil, "")),
		write("base2", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"FAIL (exit 1)"}, nil)

	// 3. THE VACUITY CASE (#988 form): no marker, i.e. no publish reported
	//    anything. Must be exit 2 and must never be 0, even though the log is
	//    large, well-formed and matches the inventory exactly.
	check("no_marker_cannot_go_green",
		write("log3", fixture("linux-x64", "fulltree", "failed", 1, "SOMETHING_ELSE", nil, "")),
		write("base3", baselineRows),
		"linux-x64", "fulltree", "failed", 2,
		[]string{"CANNOT RUN (exit 2)", "carries no 'HARBOR_AOT_PUBLISH_DONE"}, nil)

	// 4. A different platform's log, and the other recipe's log, are both
	//    refused — the two build different programs.
	check("wrong_rid_is_rejected",
		write("log4", fixture("win-x64", "fulltree", "failed", 1, defaultMarker, nil, "")),
		write("base4", baselineRows),
		"linux-x64", "fulltree", "failed", 2,
		[]string{"expected rid=linux-x64"}, nil)
	check("wrong_recipe_is_rejected",
		write("log4b", fixture("linux-x64", "minimal", "failed", 1, defaultMarker, nil, "")),
		write("base4b", baselineRows),
		"linux-x64", "fulltree", "failed", 2,
		[]string{"expected recipe=fulltree", "DIFFERENT programs"}, nil)

	// 5. THE PLANTED-HIT CASE (#591 form): one extra id in the log, in scope,
	//    that the baseline knows nothing about. The message must NAME it.
	check("planted_new_id_fails",
		write("log5", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Tui.CellForge.Screen(40,5): " +
				"error IL3000: planted for the self-test [HARBOR_APP_CLI]"}, "")),
		write("base5", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"NEW  IL3000 error", "Harbor.Tui.CellForge.Screen", "FAIL (exit 1)"}, nil)

	// 5b. A new id at a THIRD-PARTY site is still red.
	check("planted_third_party_id_fails",
		write("log5b", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/.nuget/packages/microsoft.codeanalysis.csharp/5.6.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll(9,9): " +
				"error IL3000: linker warning: planted [HARBOR_APP_CLI]"}, "")),
		write("base5b", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"NEW  IL3000 error", "third-party only"}, nil)

	// 5d. MSBuild's trailing project tag must not become a site.
	check("msbuild_project_tag_is_not_a_site",
		write("log5d", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"/home/runner/.nuget/packages/messagepack/3.1.4/lib/net8.0/MessagePack.dll: " +
				"warning IL2104: Assembly 'MessagePack' produced trim warnings. For more " +
				"information see https://aka.ms/il2104 " +
				"[/home/runner/work/Harbor-Harness/Harbor-Harness/apps/Harbor.App.Cli/Harbor.App.Cli.csproj]"}, "")),
		write("base5d", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"NEW  IL2104 warning", "third-party only"},
		[]string{"Harbor.App.Cli"})

	// 5c. An out-of-scope id is not drift.
	check("out_of_scope_id_is_ignored",
		write("log5c", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Hosting.HostBuilder(9,1): " +
				"warning IL9999: not a trim or AOT id [HARBOR_APP_CLI]"}, "")),
		write("base5c", baselineRows),
		"linux-x64", "fulltree", "failed", 0,
		[]string{"found 3 distinct diagnostic id(s), 4 occurrence(s)"}, nil)

	// 6. THE HALVED-RULE CASE (#591 form): the rule is "count occurrences".
	check("occurrence_count_is_not_deduplicated",
		write("log6", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, nil, "")),
		write("base6", baselineRows),
		"linux-x64", "fulltree", "failed", 0,
		[]string{"4 occurrence(s)"}, nil)
	check("a_fourth_occurrence_turns_it_red",
		write("log7", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(120,5): " +
				"warning IL2026: analyzer warned: one more [HARBOR_APP_CLI]"}, "")),
		write("base7", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"COUNT IL2026 warning: 3 occurrence(s) now, 2 in the baseline"}, nil)

	// 7. Severity is part of the key.
	check("severity_is_part_of_the_key",
		write("log8", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Hosting.HostBuilder(1,1): " +
				"error IL2026: same id as the warning row, but an error [HARBOR_APP_CLI]"}, "")),
		write("base8", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"NEW  IL2026 error"},
		[]string{"NEW  IL2026 warning"})

	// 8. A first-party site that appears is drift even at an unchanged count.
	check("new_first_party_site_fails",
		write("log9", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, []string{
			"  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Tui.CellForge.Screen(41,5): " +
				"warning IL3050: our code now calls Roslyn [HARBOR_APP_CLI]"}, "")),
		write("base9", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"SITES IL3050 warning", "Harbor.Tui.CellForge.Screen"}, nil)

	// 9. Liveness: a row for a diagnostic nothing emits is a stale permission.
	check("stale_baseline_row_fails",
		write("log10", fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, nil, "")),
		write("base10", baselineRows+
			"IL2104 | warning | 7 | ACCEPTED | - | per-assembly trim rollup inside a referenced package, tracked in #413\n"),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"GONE  IL2104 warning", "delete the row in this same diff"}, nil)

	// 10. A REJECTED row that stops appearing is an IMPROVEMENT and must also
	//     be red — otherwise the fix lands unrecorded.
	withoutReflect := strings.Replace(
		fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, nil, ""),
		"/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Instantiation.Reflect(9,1): "+
			"error IL2070: Trim analysis error: 'this' argument does not satisfy "+
			"PublicParameterlessConstructor [HARBOR_APP_CLI]\n", "", 1)
	check("rejected_row_disappearing_is_still_red",
		write("log11", withoutReflect),
		write("base11", baselineRows),
		"linux-x64", "fulltree", "failed", 1,
		[]string{"GONE  IL2070 error", "the AOT state IMPROVING"}, nil)

	// 11. A short log is a truncated file, not a publish.
	check("truncated_log_cannot_run",
		write("log12", defaultMarker+" rid=linux-x64 recipe=fulltree status=failed exit=1\n"),
		write("base12", emptyBaseline),
		"linux-x64", "fulltree", "failed", 2,
		[]string{"under the 2000-byte floor"}, nil)

	// 12. No log at all.
	check("absent_log_cannot_run",
		filepath.Join(root, "does-not-exist.log"),
		write("base13", emptyBaseline),
		"linux-x64", "fulltree", "failed", 2,
		[]string{"CANNOT RUN (exit 2)"}, nil)

	// 13. Baseline rows that would not survive review are refused, so a
	//     permission cannot be added by editing the record alone.
	malformed := []struct {
		name   string
		row    string
		needle string
	}{
		{"no_verdict",
			"IL2026 | warning | 2 | TOLERATED | - | some reason that is long enough to pass the floor\n",
			"verdict 'TOLERATED' is not one of"},
		{"zero_count",
			"IL2026 | warning | 0 | ACCEPTED | - | a row for something that does not occur is a permission not a measurement\n",
			"is not a positive integer"},
		{"url_reason",
			"IL2026 | warning | 2 | ACCEPTED | - | https://github.com/refusedguy/Harbor-Harness/issues/413\n",
			"the reason is a URL"},
		{"short_reason",
			"IL2026 | warning | 2 | ACCEPTED | - | later\n",
			"is 5 characters"},
		{"out_of_scope",
			"CA1822 | warning | 2 | ACCEPTED | - | an ordinary analyzer id is not a trim or AOT diagnostic at all\n",
			"is not an IL2xxx/IL3xxx/CS id"},
		{"wrong_field_count",
			"IL2026 | warning | 2 | ACCEPTED\n",
			"expected 6 '|'-separated fields"},
		{"path_claimed_as_a_site",
			"IL2026 | warning | 2 | ACCEPTED | src/Harbor.Hosting/Configuration/JsonAppConfigStore.cs | a file path is not a site: ILC prints the type, not the file\n",
			"is not a first-party site"},
		{"bad_severity",
			"IL2026 | note | 2 | ACCEPTED | - | severity must be one of the two the log can actually produce\n",
			"severity 'note' is not one of"},
	}
	for _, entry := range malformed {
		check("baseline_"+entry.name,
			write("log14"+entry.name, fixture("linux-x64", "fulltree", "failed", 1, defaultMarker, nil, "")),
			write("base14"+entry.name, entry.row),
			"linux-x64", "fulltree", "failed", 2,
			[]string{entry.needle, "the baseline is not well formed"}, nil)
	}

	if len(failures) > 0 {
		fmt.Printf("%s: SELFTEST FAILED — %d of %d case(s) wrong\n", toolName, len(failures), checks)
		for _, failure := range failures {
			fmt.Printf("  %s\n", failure)
		}
		return gate.Red
	}

	fmt.Printf("%s: SELFTEST PASSED — %d cases, each on a fixture where the outcome is "+
		"guaranteed (planted extra id, planted extra occurrence, severity collision, planted "+
		"first-party site, no marker, wrong rid, wrong recipe, unexpected publish success, "+
		"truncated log, absent log, 8 malformed baseline rows).\n", toolName, checks)
	return gate.Green
}
