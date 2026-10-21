package checkreferencetrimmer

// selftest.go -- the gate's own proof that it can fail.
//
// The gate is red when it should be, and green when it should be. The
// fixtures below are the shapes that matter: a finding the baseline carries
// (green), a finding it does not (red), a baseline row the tree no longer
// produces (red), a row without a reason (red), and the two statements the
// analyzer makes about the quality of its own answer.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const fixtureFindings = `/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : warning RT0003: PackageReference Ghost.Package can be removed [/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]
/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : warning RT0003: PackageReference Ghost.Package can be removed [/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]
/home/runner/work/Repo/Repo/src/Harbor.Two/Harbor.Two.csproj : warning RT0002: ProjectReference ../Harbor.One/Harbor.One.csproj can be removed [/home/runner/work/Repo/Repo/src/Harbor.Two/Harbor.Two.csproj]
`

const fixtureCoverage = "src/Harbor.One\nsrc/Harbor.Two\n"

const fixtureBaseline = `# fixture
RT0003 | src/Harbor.One/Harbor.One.csproj :: Ghost.Package | a reason that is long enough to be a reason and not a category | @refusedguy
RT0002 | src/Harbor.Two/Harbor.Two.csproj :: ../Harbor.One/Harbor.One.csproj | a reason that is long enough to be a reason and not a category | @refusedguy
`

// writeFile writes a fixture file.
func writeFile(root string, relative string, text string) {
	path := filepath.Join(root, relative)
	os.MkdirAll(filepath.Dir(path), 0o755)
	os.WriteFile(path, []byte(text), 0o644)
}

// fixtureRoot is a throwaway tree.
//
// The project files exist so that the path normalisation can anchor on them:
// the fixture's log carries the paths a CI runner would produce, and the
// gate has to bring them back to repository-relative ones.
func fixtureRoot() string {
	root, err := os.MkdirTemp("", "rt-gate-")
	if err != nil {
		return ""
	}
	for _, relative := range []string{
		"src/Harbor.One/Harbor.One.csproj",
		"src/Harbor.Two/Harbor.Two.csproj",
	} {
		writeFile(root, relative, "<Project />\n")
	}
	return root
}

// hasRule is whether one finding carries a rule.
func hasRule(found []finding, rule string) bool {
	for _, item := range found {
		if item.rule == rule {
			return true
		}
	}
	return false
}

// selfTestRun proves the gate is red when it should be.
func selfTestRun() int {
	var failures []string
	check := func(name string, condition bool, detail string) {
		if condition {
			fmt.Printf("  ok   %s\n", name)
			return
		}
		fmt.Printf("  FAIL %s %s\n", name, detail)
		failures = append(failures, name)
	}

	base, err := os.MkdirTemp("", "rt-selftest-")
	if err != nil {
		fmt.Println("  FAIL cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(base)

	// 1. The clean case: every finding the tree produces is carried.
	root := fixtureRoot()
	defer os.RemoveAll(root)
	writeFile(root, "findings.log", fixtureFindings)
	writeFile(root, "coverage.txt", fixtureCoverage)
	writeFile(root, filepath.Join("tools", "reference-trimmer-baseline.txt"), fixtureBaseline)
	found, _ := scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("a carried finding is not a new finding", !hasRule(found, "RT-NEW-FINDING"), "")

	// The same finding twice in the log is one finding: a project built
	// twice, or a warning reprinted, must not become two candidates.
	parsed, _, _ := parsedFindings(root, fixtureFindings)
	check("a duplicate log line is one finding", len(parsed) == 2,
		fmt.Sprintf("the fixture holds two distinct findings and one duplicate line; parsed %d", len(parsed)))

	// The projects another gate answers for are dropped, and the drop is
	// counted rather than silent.
	guardedRoot := fixtureRoot()
	defer os.RemoveAll(guardedRoot)
	writeFile(guardedRoot, filepath.Join("tools", "gates", "check-abstractions-contract", "main.go"),
		"package checkabstractionscontract\n\nvar scope = []string{\n\t\"src/Harbor.One\",\n}\n")
	guardedFindings := "/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : " +
		"warning RT0003: PackageReference Ghost.Package can be removed " +
		"[/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]\n"
	parsed, _, excluded := parsedFindings(guardedRoot, guardedFindings)
	check("another gate's scope is excluded", excluded == 1 && len(parsed) == 0,
		fmt.Sprintf("excluded %d, parsed %d", excluded, len(parsed)))

	// 2. A finding the baseline does not carry.
	writeFile(root, filepath.Join("tools", "reference-trimmer-baseline.txt"), "# fixture with no rows\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("an uncarried finding is red", hasRule(found, "RT-NEW-FINDING"), "")

	// 3. A baseline row the tree no longer produces.
	writeFile(root, filepath.Join("tools", "reference-trimmer-baseline.txt"),
		fixtureBaseline+"RT0002 | src/Harbor.Three/Harbor.Three.csproj :: Gone.Package | a reason that is long enough to be a reason and not a category | @refusedguy\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("a stale baseline row is red", hasRule(found, "RT-STALE-BASELINE"), "")

	// 4. A baseline row without a reason.
	writeFile(root, filepath.Join("tools", "reference-trimmer-baseline.txt"),
		"RT0003 | src/Harbor.One/Harbor.One.csproj :: Ghost.Package | too short | @refusedguy\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("a baseline row without a reason is red", hasRule(found, "RT-UNJUSTIFIED"), "")

	// 5. RT0000 — the instrument says it is running weak.
	writeFile(root, "findings.log", fixtureFindings+"src/Harbor.One/Harbor.One.csproj : warning RT0000: Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection [src/Harbor.One/Harbor.One.csproj]\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("RT0000 is red", hasRule(found, "RT-DOC-OFF"), "")

	// 6. RT9999 — the analyzer itself failed.
	writeFile(root, "findings.log", fixtureFindings+"src/Harbor.One/Harbor.One.csproj : warning RT9999: ReferenceTrimmer encountered an unexpected error: boom. Please file a bug at https://github.com/dfederm/ReferenceTrimmer/issues [src/Harbor.One/Harbor.One.csproj]\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("RT9999 is red", hasRule(found, "RT-ANALYZER-ERROR"), "")

	// 7. A missing findings file.
	found, _ = scan(root, filepath.Join(root, "does-not-exist.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("a missing findings file is red", hasRule(found, "RT-SCAN-BLIND"), "")

	// 8. An empty findings file.
	writeFile(root, "empty.log", "")
	found, _ = scan(root, filepath.Join(root, "empty.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("an empty findings file is red", hasRule(found, "RT-SCAN-BLIND"), "")

	// 9. A coverage count below the floor.
	writeFile(root, "findings.log", fixtureFindings)
	writeFile(root, "coverage.txt", "src/Harbor.One\n")
	found, _ = scan(root, filepath.Join(root, "findings.log"),
		filepath.Join(root, "coverage.txt"),
		filepath.Join(root, "tools", "reference-trimmer-baseline.txt"), 2)
	check("a coverage count below the floor is red", hasRule(found, "RT-SCAN-BLIND"), "")

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %s\n", strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Println("\nself-test OK")
	return gate.Green
}
