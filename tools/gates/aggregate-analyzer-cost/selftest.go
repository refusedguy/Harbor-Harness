package aggregateanalyzercost

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing fixture is the compiler's own report: the rows that carry
// a ", Version=" identity are the ones that belong to a PACKAGE, and the row
// without one is not an analyzer at all. A reader that took every row of the
// report would attribute the compiler's own time to a family.

import (
	"fmt"
	"os"
	"path/filepath"

	"harbor.local/gates/internal/gate"
)

const reportFixture = `   1.234   48   Roslynator.Analyzers.CSharp, Version=1.2.0.0, Culture=neutral
   2.500   48   Microsoft.CodeAnalysis.NetAnalyzers, Version=9.0.0.0, Culture=neutral
   0.100   48   Something Without A Version
   0.500   48   Microsoft.CodeAnalysis.Analyzers, Version=3.11.0.0, Culture=neutral
Total analyzer execution time: 4.334 seconds
`

// selfTestRun proves the report parser and the family mapping.
func selfTestRun() int {
	var problems []string
	check := func(condition bool, message string) {
		if !condition {
			problems = append(problems, message)
		}
	}

	tmp, err := os.MkdirTemp("", "aggregate-analyzer-cost-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	logPath := filepath.Join(tmp, "x-report-arm.log")
	if err := os.WriteFile(logPath, []byte(reportFixture), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the fixture:", err)
		return gate.Red
	}
	totals := parseAnalyzerReport(logPath)
	check(len(totals.order) == 3, fmt.Sprintf("3 assemblies carry a version, got %d", len(totals.order)))
	if len(totals.order) == 3 {
		check(totals.order[0] == "Roslynator.Analyzers.CSharp",
			fmt.Sprintf("the first assembly is Roslynator, got %q", totals.order[0]))
		check(totals.order[1] == "Microsoft.CodeAnalysis.NetAnalyzers",
			fmt.Sprintf("the second assembly is NetAnalyzers, got %q", totals.order[1]))
		check(totals.order[2] == "Microsoft.CodeAnalysis.Analyzers",
			fmt.Sprintf("the third assembly is the SDK's own, got %q", totals.order[2]))
	}
	check(totals.seconds["Microsoft.CodeAnalysis.NetAnalyzers"] == 2.5,
		fmt.Sprintf("the NetAnalyzers time is 2.5 s, got %v", totals.seconds["Microsoft.CodeAnalysis.NetAnalyzers"]))

	// The family of an assembly is the one the table names, and the SDK's
	// own assembly is its own row.
	check(familyOf("Roslynator.Analyzers.CSharp") == "Roslynator.Analyzers",
		fmt.Sprintf("Roslynator's family is Roslynator.Analyzers, got %q", familyOf("Roslynator.Analyzers.CSharp")))
	check(familyOf("Microsoft.CodeAnalysis.Analyzers") == "Microsoft.CodeAnalysis.Analyzers",
		fmt.Sprintf("the SDK's assembly is its own family, got %q", familyOf("Microsoft.CodeAnalysis.Analyzers")))
	check(familyOf("Something.Unknown") == "Something.Unknown",
		fmt.Sprintf("an unknown assembly is its own family, got %q", familyOf("Something.Unknown")))

	// A log with the elapsed-seconds prefix is read the same way.
	prefixed := "  12.345 |    1.234   48   Roslynator.Analyzers.CSharp, Version=1.2.0.0, Culture=neutral\n"
	prefixedPath := filepath.Join(tmp, "prefixed.log")
	if err := os.WriteFile(prefixedPath, []byte(prefixed), 0o644); err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot write the prefixed fixture:", err)
		return gate.Red
	}
	prefixedTotals := parseAnalyzerReport(prefixedPath)
	check(len(prefixedTotals.order) == 1, fmt.Sprintf("the prefixed log carries 1 assembly, got %d", len(prefixedTotals.order)))
	if len(prefixedTotals.order) == 1 {
		check(prefixedTotals.order[0] == "Roslynator.Analyzers.CSharp",
			fmt.Sprintf("the prefixed log names the assembly, got %q", prefixedTotals.order[0]))
	}

	for _, problem := range problems {
		fmt.Printf("  SELF-TEST FAIL: %s\n", problem)
	}
	if len(problems) > 0 {
		return gate.Red
	}
	fmt.Println("self-test OK: the compiler report is read, the versionless row is ignored, the families are mapped.")
	return gate.Green
}
