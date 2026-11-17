package testdup

// selftest.go -- the non-vacuity proof: this gate MUST fail on a planted
// duplicate.
//
// The fixtures are the failure paths of the gate, one each: an injected
// duplicate MUST be reported and MUST fail the gate; two bodies that merely
// have the same shape MUST NOT be reported; a group already in the baseline
// MUST pass; a group whose count grew MUST fail; REWRITING the body of every
// copy of a known group MUST still pass (the edit is not new duplication); a
// known group whose FILES MOVED MUST still pass, and the same moved group
// with ONE MORE COPY MUST fail; a relocated group whose method names DIFFER
// MUST fail; a tree below the floors MUST fail; a missing baseline MUST
// fail; a three-line assert helper MUST NOT be flagged; a brace inside a
// string literal and a `//` inside a raw string MUST NOT move the scanner;
// and the gate's own source MUST NOT be inside the scanned scope.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const bodyA = `
    [Test]
    public async Task Alpha_Works()
    {
        var store = CreateStore();
        try
        {
            var result = await store.CreateAsync("/test/dir", "code", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/test/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
`

// The same code with different literals: an EXACT copy modulo literals.
const bodyB = `
    [Test]
    public async Task Beta_Works()
    {
        var store = CreateStore();
        try
        {
            var result = await store.CreateAsync("/other/dir", "plan", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/other/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("plan");
        }
        finally
        {
            if (Directory.Exists(store.GetRootDirectory())) Directory.Delete(store.GetRootDirectory(), true);
        }
    }
`

// The same SHAPE with a renamed helper: a near copy.
const bodyC = `
    [Test]
    public async Task Gamma_Works()
    {
        var tool = CreateTool();
        try
        {
            var result = await tool.CreateAsync("/third/dir", "code", "anthropic", "claude-opus-4");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsNotNull();
            await Assert.That(result.Value.Directory).IsEqualTo("/third/dir");
            await Assert.That(result.Value.Agent).IsEqualTo("code");
        }
        finally
        {
            if (Directory.Exists(tool.GetRootDirectory())) Directory.Delete(tool.GetRootDirectory(), true);
        }
    }
`

// The same SHAPE and almost nothing else in common: a coincidence, not a
// duplicate.
const bodyD = `
    [Test]
    public void Delta_Works()
    {
        var counter = new Counter();
        counter.Add(1);
        counter.Add(2);
        counter.Add(3);
        var total = counter.Total();
        Assert.That(total).IsEqualTo(6);
    }
`

const bodyE = `
    [Test]
    public void Epsilon_Works()
    {
        var ledger = new Ledger();
        ledger.Add(1);
        ledger.Add(2);
        ledger.Add(3);
        var sum = ledger.Sum();
        Assert.That(sum).IsEqualTo(6);
    }
`

// Three lines: below the floor, and therefore not a duplicate worth a
// report.
const tinyA = `
    [Test]
    public void Tiny_One()
    {
        Assert.That(1).IsEqualTo(1);
    }
`

const tinyB = `
    [Test]
    public void Tiny_Two()
    {
        Assert.That(2).IsEqualTo(2);
    }
`

const header = `using System;
using System.IO;
using System.Threading.Tasks;
using TUnit.Assertions;

namespace Harbor.Demo.Tests;

public class %s
{
    private static object CreateStore() => new object();

%s}
`

// writeTree writes the files of a fixture.
func writeTree(root string, files map[string]string) {
	for name, text := range files {
		path := filepath.Join(root, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			continue
		}
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			continue
		}
	}
}

// fixture builds a throwaway tree plus an EMPTY baseline that goes with it.
func fixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "test-dup-gate-")
	if err != nil {
		return ""
	}
	writeTree(root, files)
	payload := jsonObject()
	payload.set("version", jsonNumber(1))
	metrics := jsonObject()
	metrics.set("duplicate_lines", jsonNumber(0))
	payload.set("metrics", metrics)
	payload.set("blocking_duplicate_lines", jsonNumber(0))
	payload.set("groups", jsonObject())
	payload.set("exemptions", jsonArray())
	if err := os.MkdirAll(filepath.Join(root, "tools"), 0o755); err != nil {
		return root
	}
	_ = os.WriteFile(filepath.Join(root, "tools", baselineName), []byte(payload.render(0)+"\n"), 0o644)
	return root
}

// writeFixtureBaseline writes a baseline that knows the given groups.
func writeFixtureBaseline(root string, groups []*dupGroup, duplicateLines int) {
	entries := jsonObject()
	for _, group := range groups {
		members := jsonArray()
		for _, key := range group.memberKeys {
			members.append(jsonString(key))
		}
		entry := jsonObject()
		entry.set("members", members)
		entry.set("count", jsonNumber(group.count))
		entry.set("lines", jsonNumber(group.lines))
		entry.set("files", jsonNumber(group.fileCount))
		entry.set("exact", jsonNumber(group.exactCount))
		entry.set("shape", jsonString(group.fingerprint))
		entries.set(group.memberKey, entry)
	}
	metrics := jsonObject()
	metrics.set("duplicate_lines", jsonNumber(duplicateLines))
	payload := jsonObject()
	payload.set("version", jsonNumber(1))
	payload.set("metrics", metrics)
	payload.set("blocking_duplicate_lines", jsonNumber(duplicateLines))
	payload.set("groups", entries)
	payload.set("exemptions", jsonArray())
	_ = os.WriteFile(filepath.Join(root, "tools", baselineName), []byte(payload.render(0)+"\n"), 0o644)
}

// hasPrefix reports whether any finding starts with the given prefix.
func hasPrefix(findings []string, prefix string) bool {
	for _, finding := range findings {
		if strings.HasPrefix(finding, prefix) {
			return true
		}
	}
	return false
}

// containsText reports whether any finding holds the given text.
func containsText(findings []string, text string) bool {
	for _, finding := range findings {
		if strings.Contains(finding, text) {
			return true
		}
	}
	return false
}

func selfTestRun() int {
	var failures []string
	ran := 0

	check := func(name string, condition bool, detail string) {
		ran++
		if condition {
			fmt.Printf("  ok    %s\n", name)
			return
		}
		fmt.Printf("  FAIL  %s %s\n", name, detail)
		failures = append(failures, name)
	}

	treeA := fmt.Sprintf(header, "AlphaTests", bodyA)
	treeB := fmt.Sprintf(header, "BetaTests", bodyB)
	treeC := fmt.Sprintf(header, "GammaTests", bodyC)
	treeD := fmt.Sprintf(header, "DeltaTests", bodyD)
	treeE := fmt.Sprintf(header, "EpsilonTests", bodyE)

	// 1. THE POSITIVE CONTROL. Two files with the same body: the gate MUST
	//    find it and MUST fail.
	root := fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	result := runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("an injected duplicate is reported",
		hasPrefix(result.findings, "DUP-NEW"), fmt.Sprint(result.findings))
	check("an injected duplicate is a group with two members",
		len(result.groups) == 1 && result.groups[0].count == 2 && result.groups[0].fileCount == 2,
		fmt.Sprint(result.groups))
	check("an injected duplicate names both files and the line range",
		len(result.groups) > 0 &&
			strings.Contains(result.groups[0].members[0].path, "AlphaTests.cs") &&
			result.groups[0].members[0].startLine > 0 &&
			result.groups[0].members[0].endLine > result.groups[0].members[0].startLine,
		fmt.Sprint(result.groups))
	described := ""
	if len(result.groups) > 0 {
		described = describeGroup(result.groups[0])
	}
	check("the report says which other file duplicates it",
		strings.Contains(described, "also tests/Demo.Tests/BetaTests.cs"), described)
	check("the injected duplicate is an EXACT copy modulo literals",
		len(result.groups) > 0 && result.groups[0].exactCount == 2, fmt.Sprint(result.groups))
	os.RemoveAll(root)

	// 2. A near copy — same shape, different identifiers — is reported too.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/GammaTests.cs": treeC,
	})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a near copy (same shape, renamed identifiers) is reported",
		len(result.groups) == 1 && result.groups[0].count == 2 && result.groups[0].exactCount == 0,
		fmt.Sprint(result.groups))
	os.RemoveAll(root)

	// 3. THE NEGATIVE CONTROL. Two bodies that merely have the same shape
	//    and no vocabulary in common must NOT be reported.
	root = fixture(map[string]string{
		"tests/Demo.Tests/DeltaTests.cs":   treeD,
		"tests/Demo.Tests/EpsilonTests.cs": treeE,
	})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a same-shape body with unrelated identifiers is not a duplicate",
		len(result.groups) == 0, fmt.Sprint(result.groups))
	check("the rejected coincidence is counted, not hidden",
		result.measured.shapeCoincidences == 1, fmt.Sprint(result.measured))
	os.RemoveAll(root)

	// 4. The threshold floor: a three-line assert helper copied into
	//    another file is a legitimate small overlap and must stay quiet.
	root = fixture(map[string]string{
		"tests/Demo.Tests/TinyOne.cs": fmt.Sprintf(header, "TinyOne", tinyA),
		"tests/Demo.Tests/TinyTwo.cs": fmt.Sprintf(header, "TinyTwo", tinyB),
	})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a three-line assert helper is not flagged",
		len(result.findings) == 0 && len(result.groups) == 0,
		fmt.Sprint(result.findings)+fmt.Sprint(result.groups))
	os.RemoveAll(root)

	// 5. A group already in the baseline MUST pass.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	methods, _ := scanTree(root)
	groupsNow, _ := buildGroups(methods, minBodyLines, minMembers, minSimilarity)
	duplicateLines := 0
	for _, group := range groupsNow {
		duplicateLines += group.duplicateLines
	}
	writeFixtureBaseline(root, groupsNow, duplicateLines)
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a group the baseline already knows stays green",
		len(result.findings) == 0, fmt.Sprint(result.findings))

	// 6. The same group with ONE MORE COPY MUST fail.
	treeE = fmt.Sprintf(header, "EpsilonTests", bodyA)
	writeTree(root, map[string]string{"tests/Demo.Tests/EpsilonTests.cs": treeE})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("one more copy of a known body fails",
		containsText(result.findings, "more were added"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 7. A missing baseline MUST fail.
	root = fixture(map[string]string{"tests/Demo.Tests/AlphaTests.cs": treeA})
	_ = os.Remove(filepath.Join(root, "tools", baselineName))
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a missing baseline fails",
		hasPrefix(result.findings, "DUP-BASELINE-MISSING"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 8. The blindness case: a tree below the floors MUST fail.
	root = fixture(map[string]string{"tests/Demo.Tests/AlphaTests.cs": treeA})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 100, 0, nil)
	check("a scan below the file floor fails",
		hasPrefix(result.findings, "DUP-FLOOR"), fmt.Sprint(result.findings))
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 100, nil)
	check("a scan below the method floor fails",
		hasPrefix(result.findings, "DUP-FLOOR"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 9. A brace inside a string literal and a `//` inside a raw string must
	//    not move the scanner.
	tricky := "namespace Harbor.Demo.Tests;\n" +
		"\n" +
		"public class TrickyTests\n" +
		"{\n" +
		"    [Test]\n" +
		"    public void Tricky()\n" +
		"    {\n" +
		"        var brace = \"}\";\n" +
		"        var raw = \"\"\"\n" +
		"        // not a comment }\n" +
		"        \"\"\";\n" +
		"        Assert.That(brace).IsNotNull();\n" +
		"        Assert.That(raw).IsNotNull();\n" +
		"    }\n" +
		"\n" +
		"    [Test]\n" +
		"    public void Other()\n" +
		"    {\n" +
		"        Assert.That(1).IsEqualTo(1);\n" +
		"    }\n" +
		"}\n"
	root = fixture(map[string]string{"tests/Demo.Tests/TrickyTests.cs": tricky})
	methods, _ = scanTree(root)
	names := []string{}
	for _, one := range methods {
		names = append(names, one.name)
	}
	sortStringsInPlace(names)
	check("a brace in a string and a // in a raw string do not move the scanner",
		len(names) == 2 && names[0] == "Other" && names[1] == "Tricky", fmt.Sprint(names))
	os.RemoveAll(root)

	// 10. The gate's own source MUST NOT be inside the scanned scope.
	here, err := os.Getwd()
	if err != nil {
		here = ""
	}
	inside := strings.HasPrefix(here, filepath.Join("..", "..", scopeDir)+string(os.PathSeparator))
	check("the gate's own source is outside the scanned scope", !inside, here)

	// 11. An exemption without a reason or an expiry MUST fail, an expired
	//     one MUST fail, a valid one MUST cover its zone, and one that
	//     matches nothing MUST be reported.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	writeExemptions(root, []exemption{{Paths: []string{"tests/Demo.Tests/**"}}})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	invalid := 0
	for _, finding := range result.findings {
		if strings.HasPrefix(finding, "DUP-EXEMPT-INVALID") {
			invalid++
		}
	}
	check("an exemption without a reason or an expiry fails", invalid == 2, fmt.Sprint(result.findings))
	writeExemptions(root, []exemption{{
		Paths:   []string{"tests/Demo.Tests/**"},
		Reason:  "busy zone owned by another programme",
		Expires: "2999-01-01",
	}})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a valid exemption covers the group",
		!hasPrefix(result.findings, "DUP-NEW"), fmt.Sprint(result.findings))
	writeExemptions(root, []exemption{{
		Paths:   []string{"tests/Demo.Tests/**"},
		Reason:  "busy zone owned by another programme",
		Expires: "2000-01-01",
	}})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("an expired exemption fails",
		hasPrefix(result.findings, "DUP-EXEMPT-EXPIRED"), fmt.Sprint(result.findings))
	writeExemptions(root, []exemption{{
		Paths:   []string{"tests/Nowhere/**"},
		Reason:  "busy zone owned by another programme",
		Expires: "2999-01-01",
	}})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("an exemption that matches nothing is reported",
		hasPrefix(result.warnings, "DUP-EXEMPT-UNUSED"), fmt.Sprint(result.warnings))
	os.RemoveAll(root)

	// 12. A method call must not be mistaken for a declaration.
	root = fixture(map[string]string{"tests/Demo.Tests/AlphaTests.cs": treeA})
	methods, _ = scanTree(root)
	names = []string{}
	for _, one := range methods {
		names = append(names, one.name)
	}
	sortStringsInPlace(names)
	check("a call is not a declaration",
		len(names) == 2 && names[0] == "Alpha_Works" && names[1] == "CreateStore",
		fmt.Sprint(names))
	os.RemoveAll(root)

	// 13. The intra-file case: two copies of a body IN ONE FILE are also
	//     duplication, and the group must say so.
	twice := fmt.Sprintf(header, "TwiceTests", bodyA+bodyA)
	root = fixture(map[string]string{"tests/Demo.Tests/TwiceTests.cs": twice})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("two copies in one file are a group too",
		len(result.groups) == 1 && result.groups[0].count == 2 && result.groups[0].intraFile,
		fmt.Sprint(result.groups))
	os.RemoveAll(root)

	// 14. REWRITING a duplicated body must NOT make the gate red.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	methods, _ = scanTree(root)
	groupsNow, _ = buildGroups(methods, minBodyLines, minMembers, minSimilarity)
	duplicateLines = 0
	for _, group := range groupsNow {
		duplicateLines += group.duplicateLines
	}
	writeFixtureBaseline(root, groupsNow, duplicateLines)
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a known group is green before the rewrite",
		len(result.findings) == 0, fmt.Sprint(result.findings))
	rewrittenA := strings.Replace(treeA, `"/test/dir"`, `"/rewritten/dir"`, 1)
	rewrittenB := strings.Replace(treeB, `"/other/dir"`, `"/rewritten/dir"`, 1)
	writeTree(root, map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": rewrittenA,
		"tests/Demo.Tests/BetaTests.cs":  rewrittenB,
	})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("rewriting the body of every copy stays green",
		len(result.findings) == 0, fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 15. Same-named methods in ONE file must not share a member key.
	overloads := fmt.Sprintf(header, "OverloadTests",
		bodyA+strings.Replace(bodyA, "Alpha_Works", "Alpha_Works_Again", 1))
	root = fixture(map[string]string{"tests/Demo.Tests/OverloadTests.cs": overloads})
	methods, _ = scanTree(root)
	groupsNow, _ = buildGroups(methods, minBodyLines, minMembers, minSimilarity)
	check("same-named methods in one file get distinct keys",
		len(groupsNow) > 0 && len(groupsNow[0].memberKeys) == len(groupsNow[0].members),
		fmt.Sprint(groupsNow))
	duplicateLines = 0
	for _, group := range groupsNow {
		duplicateLines += group.duplicateLines
	}
	writeFixtureBaseline(root, groupsNow, duplicateLines)
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("two same-named copies are green when the baseline knows them",
		len(result.findings) == 0, fmt.Sprint(result.findings))
	writeTree(root, map[string]string{
		"tests/Demo.Tests/OverloadTests.cs": overloads +
			strings.Replace(bodyA, "Alpha_Works", "Alpha_Works_Third", 1),
	})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a third copy of a same-named body fails",
		hasPrefix(result.findings, "DUP-NEW"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 16. `--warn-only` must report exactly the same findings and exit 0,
	//     while the default mode must still exit 1.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	warnCode, warnOutput := runCapturing(root, options{
		minLines: minBodyLines, minSimilarity: minSimilarity, warnOnly: true,
	})
	defaultCode, defaultOutput := runCapturing(root, options{
		minLines: minBodyLines, minSimilarity: minSimilarity,
	})
	check("--warn-only exits 0 on a finding", warnCode == 0, fmt.Sprint(warnCode))
	check("--warn-only still reports the finding",
		strings.Contains(warnOutput, "WARN DUP-NEW"), warnOutput)
	check("the default mode still exits 1 on the same finding",
		defaultCode == 1, fmt.Sprint(defaultCode))
	check("the default mode reports the same finding as FAIL",
		strings.Contains(defaultOutput, "FAIL DUP-NEW"), defaultOutput)
	os.RemoveAll(root)

	// 17. A group the baseline knows must stay green when its FILES MOVE.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	methods, _ = scanTree(root)
	groupsNow, _ = buildGroups(methods, minBodyLines, minMembers, minSimilarity)
	duplicateLines = 0
	for _, group := range groupsNow {
		duplicateLines += group.duplicateLines
	}
	writeFixtureBaseline(root, groupsNow, duplicateLines)
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a known group is green before the move",
		len(result.findings) == 0, fmt.Sprint(result.findings))
	writeTree(root, map[string]string{
		"tests/Demo.Tests/Moved/AlphaTests.cs": treeA,
		"tests/Demo.Tests/Moved/BetaTests.cs":  treeB,
	})
	_ = os.Remove(filepath.Join(root, "tests", "Demo.Tests", "AlphaTests.cs"))
	_ = os.Remove(filepath.Join(root, "tests", "Demo.Tests", "BetaTests.cs"))
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a known group whose files moved stays green",
		len(result.findings) == 0, fmt.Sprint(result.findings))

	// 18. The same moved group with ONE MORE COPY MUST fail.
	writeTree(root, map[string]string{"tests/Demo.Tests/Moved/GammaTests.cs": treeC})
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a moved group with an extra copy fails",
		containsText(result.findings, "more were added"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	// 19. A relocated group whose method names DIFFER must still fail.
	root = fixture(map[string]string{
		"tests/Demo.Tests/AlphaTests.cs": treeA,
		"tests/Demo.Tests/BetaTests.cs":  treeB,
	})
	methods, _ = scanTree(root)
	groupsNow, _ = buildGroups(methods, minBodyLines, minMembers, minSimilarity)
	duplicateLines = 0
	for _, group := range groupsNow {
		duplicateLines += group.duplicateLines
	}
	writeFixtureBaseline(root, groupsNow, duplicateLines)
	writeTree(root, map[string]string{
		"tests/Demo.Tests/Moved/AlphaTests.cs": strings.Replace(treeA, "Alpha_Works", "Renamed_Works", 1),
		"tests/Demo.Tests/Moved/BetaTests.cs":  strings.Replace(treeB, "Beta_Works", "Also_Renamed_Works", 1),
	})
	_ = os.Remove(filepath.Join(root, "tests", "Demo.Tests", "AlphaTests.cs"))
	_ = os.Remove(filepath.Join(root, "tests", "Demo.Tests", "BetaTests.cs"))
	result = runGate(root, minBodyLines, minMembers, minSimilarity, 0, 0, nil)
	check("a relocated group whose method names differ fails",
		hasPrefix(result.findings, "DUP-NEW"), fmt.Sprint(result.findings))
	os.RemoveAll(root)

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, ", "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}

// writeExemptions writes the exemptions of a fixture baseline.
func writeExemptions(root string, exemptions []exemption) {
	node := jsonArray()
	for _, entry := range exemptions {
		paths := jsonArray()
		for _, path := range entry.Paths {
			paths.append(jsonString(path))
		}
		one := jsonObject()
		one.set("paths", paths)
		one.set("reason", jsonString(entry.Reason))
		one.set("expires", jsonString(entry.Expires))
		node.append(one)
	}
	payload := jsonObject()
	payload.set("version", jsonNumber(1))
	metrics := jsonObject()
	metrics.set("duplicate_lines", jsonNumber(0))
	payload.set("metrics", metrics)
	payload.set("blocking_duplicate_lines", jsonNumber(0))
	payload.set("groups", jsonObject())
	payload.set("exemptions", node)
	_ = os.WriteFile(filepath.Join(root, "tools", baselineName), []byte(payload.render(0)+"\n"), 0o644)
}

// runCapturing runs the gate and captures what it prints.
func runCapturing(root string, opts options) (int, string) {
	original := os.Stdout
	reader, writer, err := os.Pipe()
	if err != nil {
		return gate.Broken, ""
	}
	os.Stdout = writer
	done := make(chan string)
	go func() {
		var buffer strings.Builder
		chunk := make([]byte, 4096)
		for {
			n, err := reader.Read(chunk)
			if n > 0 {
				buffer.Write(chunk[:n])
			}
			if err != nil {
				break
			}
		}
		done <- buffer.String()
	}()
	code := runWith(root, opts)
	writer.Close()
	os.Stdout = original
	return code, <-done
}

// sortStringsInPlace sorts a list of names.
func sortStringsInPlace(names []string) {
	for i := 1; i < len(names); i++ {
		for j := i; j > 0 && names[j] < names[j-1]; j-- {
			names[j], names[j-1] = names[j-1], names[j]
		}
	}
}
