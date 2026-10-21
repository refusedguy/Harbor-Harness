package checkxmldoccoverage

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture is a shape this gate must not get wrong. The second one is
// the load-bearing non-vacuity proof the slice requires: a tree with a
// single undocumented public member MUST fail, naming the member. If that
// fixture ever stops failing, the gate is decorative.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

const (
	fixOK = "namespace Demo;\n" +
		"/// <summary>Base.</summary>\n" +
		"public abstract record Event\n" +
		"{\n" +
		"    /// <summary>Stamp.</summary>\n" +
		"    public int Stamp { get; }\n" +
		"}\n" +
		"\n" +
		"/// <summary>A box.</summary>\n" +
		"/// <param name=\"Id\">Id.</param>\n" +
		"public sealed record Box(string Id) : Event;\n"

	fixOneGap = "namespace Demo;\n" +
		"/// <summary>Base.</summary>\n" +
		"public abstract record Event\n" +
		"{\n" +
		"    public int Stamp { get; }\n" +
		"}\n"

	fixInheritdoc = "namespace Demo;\n" +
		"/// <summary>Cache.</summary>\n" +
		"public interface ICache\n" +
		"{\n" +
		"    /// <inheritdoc />\n" +
		"    int Count { get; }\n" +
		"}\n"

	fixEnum = "namespace Demo;\n" +
		"/// <summary>Kinds.</summary>\n" +
		"public enum Kind\n" +
		"{\n" +
		"    /// <summary>First.</summary>\n" +
		"    First,\n" +
		"    Second,\n" +
		"}\n"

	fixCtor = "namespace Demo;\n" +
		"/// <summary>Cache.</summary>\n" +
		"public sealed class Cache\n" +
		"{\n" +
		"    public Cache(int capacity)\n" +
		"    {\n" +
		"    }\n" +
		"}\n"
)

// scanText is the python _scan_text: the members of one file, with the
// documentation flag.
func scanText(text string) []member {
	lines := gatekit.SplitLines(text)
	items := iterMembers(lines)
	for index := range items {
		items[index].relpath = "x/A.cs"
		items[index].documented = hasDoc(lines, items[index].line-1)
	}
	return items
}

// names are the member names of a scan.
func names(items []member) []string {
	out := make([]string, 0, len(items))
	for _, item := range items {
		out = append(out, item.name)
	}
	return out
}

// gaps are the undocumented members of a scan.
func gaps(items []member) []member {
	var out []member
	for _, item := range items {
		if !item.documented {
			out = append(out, item)
		}
	}
	return out
}

// selfTestRun runs the gate against known-broken fixtures.
func selfTestRun() int {
	ok := true
	check := func(name string, condition bool, detail string) {
		ok = ok && condition
		mark := "ok"
		if !condition {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-44s] %s %s\n", name, mark, detail)
	}

	items := scanText(fixOK)
	allDocumented := true
	for _, item := range items {
		allDocumented = allDocumented && item.documented
	}
	check("documented tree is clean", allDocumented, fmt.Sprintf("%d members", len(items)))

	items = scanText(fixOneGap)
	holes := gaps(items)
	check("single undocumented member fails, naming it",
		len(holes) == 1 && holes[0].name == "Stamp",
		fmt.Sprintf("gaps=%v", names(holes)))

	items = scanText(fixInheritdoc)
	allDocumented = true
	for _, item := range items {
		allDocumented = allDocumented && item.documented
	}
	check("inheritdoc counts as documented", allDocumented, fmt.Sprintf("%d members", len(items)))
	check("implicit interface member is gated", len(items) == 2,
		fmt.Sprintf("names=%v", names(items)))

	items = scanText(fixEnum)
	holes = gaps(items)
	check("undocumented enum value fails", len(holes) == 1 && holes[0].name == "Second",
		fmt.Sprintf("gaps=%v", names(holes)))

	items = scanText(fixCtor)
	holes = gaps(items)
	check("undocumented ctor fails", len(holes) == 1 && strings.Contains(holes[0].name, "Cache"),
		fmt.Sprintf("gaps=%v", names(holes)))

	// Suppression consumes exactly its member; a stale one is a finding.
	key := "x/A.cs::Stamp"
	saved := suppressed
	suppressed = map[string]string{key: "test reason"}
	real := []member{{relpath: "x/A.cs", line: 3, name: "Stamp", documented: false}}
	hits := 0
	for _, item := range real {
		if _, ok := suppressed[item.relpath+"::"+item.name]; ok {
			hits++
		}
	}
	check("suppression consumes its member", hits == 1, "")
	fixed := []member{{relpath: "x/A.cs", line: 3, name: "Stamp", documented: true}}
	orphans := 0
	for _, item := range fixed {
		if !item.documented {
			if _, ok := suppressed[item.relpath+"::"+item.name]; ok {
				orphans++
			}
		}
	}
	check("documented member orphans its suppression", orphans == 0, "")
	suppressed = saved

	// Floors: a matcher that sees nothing must fail the floor, not pass.
	repo, err := gate.RepoRoot()
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot determine the repository root:", err)
		return gate.Red
	}
	findings, _, _, _, _ := runGate(repo, 1000000000, 0, 0)
	floorFired := false
	for _, item := range findings {
		if strings.Contains(item.text, "XMLDOC-FLOOR") {
			floorFired = true
		}
	}
	check("absurd floor fails (matcher blindness is red)", floorFired, "")

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}
