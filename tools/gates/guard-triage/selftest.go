package guardtriage

// selftest.go -- prove the gate still finds a failure, still refuses to guess
// a class, and still classifies a planted divergence correctly.
//
// The first four fixtures are the python original's: a TRX failure, a log
// failure, a registry that loads and one that is rejected, and an unregistered
// firing that stays `unknown`.
//
// The last fixture is the planted divergence this port adds. A rule that fires
// on code the repository EXPLICITLY ALLOWS must be classified `noise`, and a
// rule that fires on its own stale ledger must be `noise` too. If either
// mechanical detector stops working, those firings fall back to `unknown`,
// the report's class counts change, and the self-test is RED. The same fixture
// drives a per-firing entry and a per-rule entry, so a class that stops being
// read is caught as well.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const sampleTRX = `<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="TheLedgerHolds" outcome="Failed" className="Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule">
      <Output><ErrorInfo><Message>Expected to contain "stale entries", because a ledger entry that is stale is an allowance that outlived its target. Found: src/Foo.cs:12</Message></ErrorInfo></Output>
      <Output><StdOut>ledger scan output</StdOut></Output>
      <StackTrace>   at Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule.TheLedgerHolds()</StackTrace>
    </UnitTestResult>
    <UnitTestResult testName="PassingTest" outcome="Passed" className="Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule" />
  </Results>
</TestRun>
`

const sampleLog = "test (core)\tRun shard core\t2026-10-20T15:49:53.5Z ##[group]dotnet exec Harbor.Architecture.Tests\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z \x1b[31mfailed\x1b[m TheLedgerHolds \x1b[90m(12ms)\x1b[m\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z   TUnit.Engine.Exceptions.TestFailedException: Expected to contain \"stale entries\". Found: src/Foo.cs:12\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z     at Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule.TheLedgerHolds()\n"

const validRegistry = `{
  "schema": "harbor.guard-triage/registry/1",
  "rules": [
    {
      "rule": "PanelKeyRouteReachabilityRule.TheLedgerHolds",
      "class": "noise",
      "reason": "the ledger fired on its own allowance",
      "ratchet": "ledger-edit"
    }
  ]
}
`

const badRegistry = `{"schema":"harbor.guard-triage/registry/1","rules":[{"rule":"X.Y","class":"bogus"}]}`

func selfTestRun() int {
	tmp, err := os.MkdirTemp("", "guard-triage-gate-")
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	defer os.RemoveAll(tmp)

	var failures []string
	check := func(ok bool, message string) {
		if !ok {
			failures = append(failures, message)
		}
	}

	trxPath := filepath.Join(tmp, "sample.trx")
	logPath := filepath.Join(tmp, "sample.log")
	registryPath := filepath.Join(tmp, "registry.json")
	badPath := filepath.Join(tmp, "bad-registry.json")
	allowancePath := filepath.Join(tmp, "allowance.txt")
	files := map[string]string{
		trxPath:       sampleTRX,
		logPath:       sampleLog,
		registryPath:  validRegistry,
		badPath:       badRegistry,
		allowancePath: "src/Allowed.cs is explicitly allowed by the repository\n",
	}
	for path, body := range files {
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
	}

	found := firingsFromTRX(trxPath)
	check(len(found) == 1, fmt.Sprintf("trx: expected 1 firing, got %d", len(found)))
	if len(found) == 1 {
		check(found[0].rule == "PanelKeyRouteReachabilityRule.TheLedgerHolds",
			fmt.Sprintf("trx: rule is '%s'", found[0].rule))
		hasLocation := false
		for _, place := range found[0].locations {
			if place.path == "src/Foo.cs" && place.line == 12 {
				hasLocation = true
			}
		}
		check(hasLocation, fmt.Sprintf("trx: locations are %v", found[0].locations))
	}

	logFirings := firingsFromLog(logPath)
	check(len(logFirings) == 1, fmt.Sprintf("log: expected 1 firing, got %d", len(logFirings)))
	if len(logFirings) == 1 {
		check(logFirings[0].rule == "PanelKeyRouteReachabilityRule.TheLedgerHolds",
			fmt.Sprintf("log: rule is '%s'", logFirings[0].rule))
		check(logFirings[0].project == "Harbor.Architecture.Tests",
			fmt.Sprintf("log: project is '%s'", logFirings[0].project))
	}

	reg, err := loadRegistry(registryPath)
	check(err == nil, fmt.Sprintf("registry: valid registry was rejected: %v", err))
	if err == nil {
		check(reg.rules["PanelKeyRouteReachabilityRule.TheLedgerHolds"].class == "noise",
			"registry: class not read")
	}
	_, err = loadRegistry(badPath)
	check(err != nil, "registry: a bad class was accepted")

	// A firing with no registry entry must stay `unknown`, not be guessed.
	unknown := newFiring("SomeRule.SomeTest", "SomeTest", "Harbor.Architecture.Tests",
		"something went wrong", "log", "x.log", nil)
	classify(unknown, reg, &guardIndex{source: map[string]string{}}, nil)
	check(unknown.classification == "unknown",
		fmt.Sprintf("unregistered firing classified as '%s'", unknown.classification))

	// The planted divergence: five firings that MUST be classified five
	// different ways. A detector that stops firing, or a registry entry that
	// stops being read, changes the report's class counts and fails here.
	divergenceRegistry := &registry{
		rules: map[string]*ruleEntry{
			"RuleLevelRule.Check": {
				rule: "RuleLevelRule.Check", class: "style", reason: "planted per-rule", ratchet: "fix-the-rule",
			},
			// AllowedPathRule.Check deliberately has NO rule entry: a rule
			// entry would short-circuit the detectors in classify(), exactly
			// as it does in the python original. The allowance is passed to
			// classify() below, which is how the detector is reached.
		},
		firings: []*firingEntry{
			{
				rule: "FiringLevelRule.Check", evidenceContains: "the planted marker",
				class: "defect", reason: "planted per-firing", ratchet: "keep",
			},
		},
	}
	allowances := []allowance{{path: "allowance.txt", text: "src/Allowed.cs is explicitly allowed by the repository\n"}}
	index := &guardIndex{source: map[string]string{
		"guards/LedgerGuard.cs": "the source lists src/Ledger.cs as an allowed path",
	}}

	perFiring := newFiring("FiringLevelRule.Check", "Check", "Harbor.Architecture.Tests",
		"Expected nothing. Found: the planted marker src/Bar.cs:3", "log", "x.log", nil)
	perRule := newFiring("RuleLevelRule.Check", "Check", "Harbor.Architecture.Tests",
		"Expected a symbol anchor. Found: src/Baz.cs:4", "log", "x.log", nil)
	allowed := newFiring("AllowedPathRule.Check", "Check", "Harbor.Architecture.Tests",
		"Found: src/Allowed.cs:9", "log", "x.log",
		[]location{{path: "src/Allowed.cs", line: 9}})
	ledger := newFiring("LedgerExitRule.Check", "Check", "Harbor.Architecture.Tests",
		"Expected no stale entries: src/Ledger.cs is no longer an orphan. Found: src/Ledger.cs:8",
		"log", "x.log", []location{{path: "src/Ledger.cs", line: 8}})
	ledger.guardFile = "guards/LedgerGuard.cs"

	for _, f := range []*firing{perFiring, perRule, allowed, ledger, unknown} {
		classify(f, divergenceRegistry, index, allowances)
	}
	check(perFiring.classification == "defect" && perFiring.classifiedBy == "registry:firing",
		fmt.Sprintf("divergence: per-firing entry gave %s/%s", perFiring.classification, perFiring.classifiedBy))
	check(perRule.classification == "style" && perRule.classifiedBy == "registry:rule",
		fmt.Sprintf("divergence: per-rule entry gave %s/%s", perRule.classification, perRule.classifiedBy))
	check(allowed.classification == "noise" && allowed.classifiedBy == "detector:allowance-listed",
		fmt.Sprintf("divergence: allowance-listed detector gave %s/%s", allowed.classification, allowed.classifiedBy))
	check(ledger.classification == "noise" && ledger.classifiedBy == "detector:ledger-retirement",
		fmt.Sprintf("divergence: ledger-retirement detector gave %s/%s", ledger.classification, ledger.classifiedBy))

	report := buildReport([]*firing{perFiring, perRule, allowed, ledger, unknown},
		nil, nil, "", "", 1)
	check(report.total == 5, fmt.Sprintf("report: total is %d", report.total))
	check(report.byClass.defect == 1 && report.byClass.style == 1 &&
		report.byClass.noise == 2 && report.byClass.unknown == 1,
		fmt.Sprintf("report: by_class is %+v", report.byClass))
	payload := report.reportJSON().render(0)
	check(strings.Contains(payload, `"classified_by": "detector:allowance-listed"`) &&
		strings.Contains(payload, `"classified_by": "detector:ledger-retirement"`),
		"report: the detectors' evidence is missing from the JSON")
	check(strings.Contains(renderMarkdown(report), "Guard firings, classified") &&
		strings.Contains(renderSummary(report), "| **total** | **5** |"),
		"report: the Markdown or summary table is empty")

	// The run-id scanner is the rewrite of the python lookbehind/lookahead:
	// a maximal digit run of eight or more, and nothing shorter.
	check(guessRunID("logs/39447321149.log") == "39447321149",
		fmt.Sprintf("run id: got '%s'", guessRunID("logs/39447321149.log")))
	check(guessRunID("logs/v123.log") == "",
		fmt.Sprintf("run id: short digits gave '%s'", guessRunID("logs/v123.log")))
	check(guessRunID("logs/123456789012345.log") == "123456789012345",
		fmt.Sprintf("run id: long digits gave '%s'", guessRunID("logs/123456789012345.log")))

	for _, failure := range failures {
		fmt.Fprintf(os.Stderr, "guard-triage self-test: FAIL %s\n", failure)
	}
	if len(failures) > 0 {
		return gate.Red
	}
	fmt.Println("guard-triage self-test: ok")
	return gate.Green
}
