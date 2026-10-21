package rune2eshards

// selftest.go -- the runner's own proof that it can fail.
//
// The python original had no self-test: the tool runs a real test assembly,
// and the guards it carries are the ones a human reads when a shard goes
// wrong. The port adds one, because a runner that stops noticing a dropped
// class is a runner that reports a green suite with fewer tests than it has.
//
// The fixtures are the plan the runner cannot run, and the numbers it reads:
//   1. a plan whose class is in two shards is refused;
//   2. a plan with no shards is refused;
//   3. a plan whose shard names no class is refused;
//   4. a plan with an unknown schema version is refused;
//   5. the filter expression is the one the tree's shards expect;
//   6. the TRX reader counts the tests, the failures and the skipped ones;
//   7. the upstream poison signature is recognised.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// writeFile writes a file, creating the directories it needs.
func writeFile(path string, text string) {
	os.MkdirAll(filepath.Dir(path), 0o755)
	os.WriteFile(path, []byte(text), 0o644)
}

// trxFixture is a TRX with three tests: one passed, one failed, one not
// executed.
const trxFixture = `<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="x" name="x" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTest id="u0" name="A"><TestMethod codeBase="x" className="Harbor.E2E.App.Avalonia.AlphaTests" name="A" /></UnitTest>
    <UnitTest id="u1" name="B"><TestMethod codeBase="x" className="Harbor.E2E.App.Avalonia.AlphaTests" name="B" /></UnitTest>
    <UnitTest id="u2" name="C"><TestMethod codeBase="x" className="Harbor.E2E.App.Avalonia.BetaTests" name="C" /></UnitTest>
  </Results>
  <Results>
    <UnitTestResult executionId="e0" testId="u0" testName="A" outcome="Passed" duration="00:00:01.5000000" />
    <UnitTestResult executionId="e1" testId="u1" testName="B" outcome="Failed" duration="00:00:02.5000000" />
    <UnitTestResult executionId="e2" testId="u2" testName="C" outcome="NotExecuted" duration="00:00:00" />
  </Results>
</TestRun>
`

// goodPlan is a plan the runner can run.
const goodPlan = `{
  "schemaVersion": 1,
  "shards": [
    { "id": "shard-0", "classes": ["AlphaTests", "BetaTests"] }
  ]
}
`

func selfTestRun() int {
	var failures []string
	fail := func(message string) {
		failures = append(failures, message)
	}

	root, err := os.MkdirTemp("", "harbor-e2e-runner-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	// 1. A good plan loads.
	good := filepath.Join(root, "good.json")
	writeFile(good, goodPlan)
	if _, err := loadPlan(good); err != nil {
		fail("the good plan was refused: " + err.Error())
	}

	// 2. A class in two shards is a divergence the runner must notice.
	duplicate := filepath.Join(root, "duplicate.json")
	writeFile(duplicate, `{
  "schemaVersion": 1,
  "shards": [
    { "id": "shard-0", "classes": ["AlphaTests"] },
    { "id": "shard-1", "classes": ["AlphaTests"] }
  ]
}`)
	_, err = loadPlan(duplicate)
	if err == nil {
		fail("a class in two shards was accepted")
	} else if !strings.Contains(err.Error(), "is in both shard-0 and shard-1") {
		fail("the duplicated class was not named: " + err.Error())
	}

	// 3. A plan with no shards is refused.
	empty := filepath.Join(root, "empty.json")
	writeFile(empty, `{"schemaVersion": 1, "shards": []}`)
	if _, err := loadPlan(empty); err == nil {
		fail("a plan with no shards was accepted")
	}

	// 4. A shard that names no class is refused.
	noClasses := filepath.Join(root, "no-classes.json")
	writeFile(noClasses, `{
  "schemaVersion": 1,
  "shards": [ { "id": "shard-0", "classes": [] } ]
}`)
	if _, err := loadPlan(noClasses); err == nil {
		fail("a shard that names no class was accepted")
	}

	// 5. An unknown schema version is refused, and the version is named the
	//    way python names it.
	version := filepath.Join(root, "version.json")
	writeFile(version, `{"schemaVersion": 2, "shards": [ { "id": "shard-0", "classes": ["A"] } ]}`)
	_, err = loadPlan(version)
	if err == nil {
		fail("a plan with an unknown schema version was accepted")
	} else if !strings.Contains(err.Error(), "unsupported schemaVersion 2") {
		fail("the schema version was not named: " + err.Error())
	}

	// 6. The filter expression.
	filter := filterFor([]string{"AlphaTests", "BetaTests"})
	if filter != "/*/*/(AlphaTests)|(BetaTests)/*" {
		fail("the filter expression is " + filter)
	}

	// 7. The TRX reader: the counts, the failures and the skipped ones.
	trx := filepath.Join(root, "shard.trx")
	writeFile(trx, trxFixture)
	counts, total, failed, notExecuted := readTrx(trx)
	if total != 3 {
		fail(fmt.Sprintf("the TRX reader counted %d test(s), expected 3", total))
	}
	if failed != 1 {
		fail(fmt.Sprintf("the TRX reader counted %d failure(s), expected 1", failed))
	}
	if notExecuted != 1 {
		fail(fmt.Sprintf("the TRX reader counted %d not-executed test(s), expected 1", notExecuted))
	}
	if counts["AlphaTests"] != 2 || counts["BetaTests"] != 1 {
		fail(fmt.Sprintf("the TRX reader counted %v per class", counts))
	}

	// 8. The upstream poison signature.
	poisoned := false
	for _, signature := range poisonSignatures {
		if strings.Contains("failed in EnsureSharedApplication", signature) {
			poisoned = true
			break
		}
	}
	if !poisoned {
		fail("the upstream poison signature is not among the ones the runner looks for")
	}

	// 9. The discovery document: the class of a discovered test.
	document := parseJSONDocument(`{"tests":[{"type":{"typeName":"Harbor.E2E.App.Avalonia.AlphaTests"},"uid":"/a/b/AlphaTests/T"}]}`)
	if document == nil {
		fail("the discovery document did not parse")
	} else if classOf(document["tests"].([]any)[0].(map[string]any)) != "AlphaTests" {
		fail("the class of a discovered test was not read")
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "self-test FAILED: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("self-test OK: the runner refuses a plan it cannot run, names the " +
		"class that is in two shards, counts the tests a shard wrote and " +
		"recognises the upstream poison signature")
	return gate.Green
}
