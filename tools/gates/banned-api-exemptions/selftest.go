package bannedapiexemptions

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture below is a shape the gate must say NO to, and the clean
// fixture is the one it must say nothing about. The rule the self-test
// holds is the rule the gate holds: a catalogue row and a suppression in
// the tree must agree, in both directions and in number.
//
// The three shapes are the three findings the gate has:
//
//	unjustified  a file carries a suppression and the catalogue does not
//	             name it -- the ban is switched off with no record;
//	stale        a catalogue row names a file that carries no suppression
//	             (or does not exist) -- a permission for a problem that is
//	             gone;
//	count        a file carries a different number of suppressions than
//	             the number of rows naming it -- one row cannot justify
//	             two suppressions.
//
// And the two shapes that must NOT be green:
//
//	blind        a catalogue without the exemptions section is a gate
//	             that cannot see its subject, not a tree with no
//	             exemptions;
//	floor        a catalogue with fewer rows than the floor is a
//	             catalogue that lost rows on purpose or by accident, and
//	             the floor is what says so.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// catalogueFixture is a catalogue with the exemptions section and two
// rows, each under a class block that carries the reason.
const catalogueFixture = `# Banned APIs -- Harbor project

T:System.Web.Script.Serialization.JavaScriptSerializer
N:Newtonsoft.Json

# ============================================================================
# ACCEPTED LEGACY EXEMPTIONS (ROP-D Z2 catalogue)
# ---
# Every exemption below carries a matching ` + "`#pragma warning disable RS0030`" + `
# with a reason at its call site.
# --- <48>
# Class 1 -- composition-root startup loads:
#   src/Harbor.Hosting/Modules/ConfigurationModule.cs  common config eager load
#   apps/Harbor.App.Cli/Hosting/HostBuilder.CliConfig.cs  cli config eager load
#   Reason: the host is built synchronously; there is no SynchronizationContext.
# ============================================================================ <67>
`

// catalogueWithGhostRow is the same catalogue plus a row naming a file the
// tree does not have.
const catalogueWithGhostRow = catalogueFixture + `#   src/Harbor.Gone/Gone.cs  a row for a file that is gone
`

// catalogueWithoutSection is a catalogue whose exemptions section is gone.
const catalogueWithoutSection = `# Banned APIs -- Harbor project

T:Newtonsoft.Json.JsonConvert
N:Newtonsoft.Json
`

// suppression is a file that carries one RS0030 suppression.
const suppression = `namespace Harbor.Hosting;

internal static class ConfigurationModule
{
    #pragma warning disable RS0030
    private static void Load() => Task.FromResult(0).GetAwaiter().GetResult();
    #pragma warning restore RS0030
}
`

// twoSuppressions is a file that carries two of them.
const twoSuppressions = `namespace Harbor.App.Avalonia;

internal sealed class SettingsViewModel
{
    #pragma warning disable RS0030
    private void Ctor() { }
    #pragma warning restore RS0030

    #pragma warning disable RS0030
    private void Probe() { }
    #pragma warning restore RS0030
}
`

// cleanFiles is the tree of the clean fixture: two files with one
// suppression each, and two rows naming them.
func cleanFiles() map[string]string {
	return map[string]string{
		"src/Harbor.Hosting/Modules/ConfigurationModule.cs":    suppression,
		"apps/Harbor.App.Cli/Hosting/HostBuilder.CliConfig.cs": suppression,
		"src/Harbor.Other/Other.cs":                            "namespace Harbor.Other;\n",
	}
}

func selfTestRun() int {
	type testCase struct {
		name      string
		catalogue string
		files     map[string]string
		// wantRule is the rule the gate must report; empty means the gate
		// must report nothing.
		wantRule string
		// wantBlind says the gate must call the catalogue blind.
		wantBlind bool
	}

	cases := []testCase{
		{
			name:      "the clean tree is clean",
			catalogue: catalogueFixture,
			files:     cleanFiles(),
		},
		{
			name:      "a suppression with no row is unjustified",
			catalogue: catalogueFixture,
			files: map[string]string{
				"src/Harbor.Hosting/Modules/ConfigurationModule.cs":    suppression,
				"apps/Harbor.App.Cli/Hosting/HostBuilder.CliConfig.cs": suppression,
				"src/Harbor.Rogue/Rogue.cs":                            suppression,
			},
			wantRule: "EXEMPTION-UNJUSTIFIED",
		},
		{
			name:      "a row whose file carries no suppression is stale",
			catalogue: catalogueFixture,
			files: map[string]string{
				"apps/Harbor.App.Cli/Hosting/HostBuilder.CliConfig.cs": suppression,
			},
			wantRule: "EXEMPTION-STALE",
		},
		{
			name:      "a row whose file does not exist is stale",
			catalogue: catalogueWithGhostRow,
			files:     cleanFiles(),
			wantRule:  "EXEMPTION-STALE",
		},
		{
			name:      "two suppressions under one row is a count mismatch",
			catalogue: catalogueFixture,
			files: map[string]string{
				"src/Harbor.Hosting/Modules/ConfigurationModule.cs":    twoSuppressions,
				"apps/Harbor.App.Cli/Hosting/HostBuilder.CliConfig.cs": suppression,
			},
			wantRule: "EXEMPTION-COUNT",
		},
		{
			name:      "a catalogue without the section is BLIND, not green",
			catalogue: catalogueWithoutSection,
			files:     cleanFiles(),
			wantBlind: true,
		},
	}

	ok := true
	fmt.Printf("fixtures: %d\n", len(cases))
	for _, c := range cases {
		findings, rows, _, sectionFound := scan(c.catalogue, c.files)

		// What the gate must say.
		want := "PASSED"
		switch {
		case c.wantBlind:
			want = "BLIND"
		case c.wantRule != "":
			want = "FIRED"
		}

		// What the gate said.
		verdict := "PASSED"
		detail := ""
		switch {
		case c.wantBlind && !sectionFound:
			verdict = "BLIND"
		case c.wantBlind:
			verdict = "NOT-BLIND"
			detail = "the catalogue has no exemptions section and the gate did not say so"
		case c.wantRule == "" && len(findings) == 0:
			verdict = "PASSED"
		case c.wantRule == "":
			verdict = "FIRED"
			detail = "the gate reported " + findings[0].rule + " on a clean tree"
		case len(findings) == 0:
			verdict = "SILENT"
			detail = "the gate found nothing where it had to find " + c.wantRule
		case findings[0].rule != c.wantRule:
			verdict = "WRONG-RULE"
			detail = "the gate reported " + findings[0].rule + " where it had to report " + c.wantRule
		default:
			verdict = "FIRED"
			detail = findings[0].rule + " " + findings[0].path
		}

		bad := verdict != want
		if bad {
			ok = false
		}
		mark := "ok"
		if bad {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-46s] %-11s rows=%d %s\n", c.name, verdict, rows, mark)
		if detail != "" {
			fmt.Printf("      %s\n", detail)
		}
	}

	// The floor is the second thing that must not be green: a catalogue
	// that lost rows is not a tree that grew clean.
	const floor = 2
	_, rows, _, _ := scan(catalogueFixture, cleanFiles())
	if rows < floor {
		fmt.Printf("  [%-46s] << MISMATCH\n", "the floor fires on a short catalogue")
		ok = false
	} else {
		fmt.Printf("  [%-46s] ok         rows=%d floor=%d\n", "the floor fires on a short catalogue", rows, floor)
	}

	// The catalogue parse must not read a prose line as a row. The reason
	// block of the catalogue is prose, and a row is a path.
	parsed, found := parseCatalogue(catalogueFixture)
	proseOK := found && len(parsed) == 2
	for _, r := range parsed {
		if !strings.HasSuffix(r.path, ".cs") || strings.Contains(r.path, " ") {
			proseOK = false
		}
	}
	if !proseOK {
		fmt.Printf("  [%-46s] << MISMATCH\n", "the prose of the catalogue is not read as rows")
		fmt.Printf("      parsed %d row(s) from a catalogue with 2\n", len(parsed))
		ok = false
	} else {
		fmt.Printf("  [%-46s] ok         rows=%d\n", "the prose of the catalogue is not read as rows", len(parsed))
	}

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}
