package guardtriage

// selftest.go -- the tool's own proof that it can fail.
//
// The python original built a TRX and a log with a planted firing and
// asserted four things: the TRX yields one firing with the right rule and
// the right location, the log yields one firing with the right rule and
// the right project, the registry is loadable and rejects a bad class, and
// a firing with no registry entry stays `unknown`.
//
// The port plants the same TRX and the same log, byte for byte, and
// asserts the same things.

import (
	"os"
	"path/filepath"
)

// The TRX fixture: one failed test with a message that names a stale
// ledger entry, and one passed test.
const trxFixture = `<?xml version="1.0" encoding="UTF-8"?>
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

// The log fixture: the same firing, in the shape a CI job log has.
const logFixture = "test (core)\tRun shard core\t2026-10-20T15:49:53.5Z ##[group]dotnet exec Harbor.Architecture.Tests\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z \x1b[31mfailed\x1b[m TheLedgerHolds \x1b[90m(12ms)\x1b[m\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z   TUnit.Engine.Exceptions.TestFailedException: Expected to contain \"stale entries\". Found: src/Foo.cs:12\n" +
	"test (core)\tRun shard core\t2026-10-20T15:49:53.6Z     at Harbor.Architecture.Tests.PanelKeyRouteReachabilityRule.TheLedgerHolds()\n"

func selfTestRun() int {
	var failures []string
	fail := func(message string) { failures = append(failures, message) }

	tmp, err := os.MkdirTemp("", "harbor-guardtriage-selftest")
	if err != nil {
		return gateBroken(err.Error())
	}
	defer os.RemoveAll(tmp)

	trxPath := filepath.Join(tmp, "sample.trx")
	if err := os.WriteFile(trxPath, []byte(trxFixture), 0o644); err != nil {
		return gateBroken(err.Error())
	}
	logPath := filepath.Join(tmp, "sample.log")
	if err := os.WriteFile(logPath, []byte(logFixture), 0o644); err != nil {
		return gateBroken(err.Error())
	}

	found := firingsFromTRX(trxPath)
	if len(found) != 1 {
		fail("trx: expected 1 firing, got " + itoa(len(found)))
	} else {
		if found[0].rule != "PanelKeyRouteReachabilityRule.TheLedgerHolds" {
			fail("trx: rule is " + found[0].rule)
		}
		if !hasLocation(found[0].locations, "src/Foo.cs", 12) {
			fail("trx: locations are wrong")
		}
	}

	logFirings := firingsFromLog(logPath)
	if len(logFirings) != 1 {
		fail("log: expected 1 firing, got " + itoa(len(logFirings)))
	} else {
		if logFirings[0].rule != "PanelKeyRouteReachabilityRule.TheLedgerHolds" {
			fail("log: rule is " + logFirings[0].rule)
		}
		if logFirings[0].project != "Harbor.Architecture.Tests" {
			fail("log: project is " + logFirings[0].project)
		}
	}

	// The registry must be loadable and must reject a bad class.
	registryPath := filepath.Join(tmp, "registry.json")
	good := `{"schema": "harbor.guard-triage/registry/1", "rules": [` +
		`{"rule": "PanelKeyRouteReachabilityRule.TheLedgerHolds", "class": "noise",` +
		` "reason": "the ledger fired on its own allowance", "ratchet": "ledger-edit"}]}`
	if err := os.WriteFile(registryPath, []byte(good), 0o644); err != nil {
		return gateBroken(err.Error())
	}
	reg, err := loadRegistry(registryPath)
	if err != nil {
		fail("registry: " + err.Error())
	} else {
		entry, ok := reg.rules["PanelKeyRouteReachabilityRule.TheLedgerHolds"]
		if !ok || entry.class != "noise" {
			fail("registry: class not read")
		}
	}

	badPath := filepath.Join(tmp, "bad-registry.json")
	bad := `{"schema": "harbor.guard-triage/registry/1", "rules": [{"rule": "X.Y", "class": "bogus"}]}`
	if err := os.WriteFile(badPath, []byte(bad), 0o644); err != nil {
		return gateBroken(err.Error())
	}
	if _, err := loadRegistry(badPath); err == nil {
		fail("registry: a bad class was accepted")
	}

	// A firing with no registry entry must stay `unknown`, not be guessed.
	unknown := firing{
		rule:      "SomeRule.SomeTest",
		testName:  "SomeTest",
		project:   "Harbor.Architecture.Tests",
		evidence:  "something went wrong",
		source:    "log",
		origin:    "x.log",
		locations: nil,
	}
	classifyFiring(&unknown, reg, buildGuardIndex(tmp, tmp), map[string]string{})
	if unknown.classification != "unknown" {
		fail("unregistered firing classified as " + unknown.classification)
	}

	// The allowance detector: a firing whose path is named in an allowance
	// file is noise.
	allowance := map[string]string{
		"tools/test-package-exceptions.txt": "src/Foo.cs | the allowance\n",
	}
	listed := firing{
		rule:      "OtherRule.OtherTest",
		evidence:  "Found: src/Foo.cs:12",
		locations: []location{{path: "src/Foo.cs", line: 12, has: true}},
	}
	classifyFiring(&listed, reg, buildGuardIndex(tmp, tmp), allowance)
	if listed.classification != "noise" || listed.classifiedBy != "detector:allowance-listed" {
		fail("the allowance detector did not fire: " + listed.classification)
	}

	if len(failures) > 0 {
		for _, failure := range failures {
			printErr("guard-triage self-test: FAIL " + failure)
		}
		return 2
	}
	printOut("guard-triage self-test: ok")
	return 0
}

// hasLocation reports whether the locations hold the path and the line.
func hasLocation(locations []location, path string, line int) bool {
	for _, loc := range locations {
		if loc.path == path && loc.line == line {
			return true
		}
	}
	return false
}

// itoa is the number as a string.
func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	negative := n < 0
	if negative {
		n = -n
	}
	var digits []byte
	for n > 0 {
		digits = append([]byte{byte('0' + n%10)}, digits...)
		n /= 10
	}
	if negative {
		return "-" + string(digits)
	}
	return string(digits)
}

// printOut writes a line to stdout.
func printOut(text string) {
	os.Stdout.WriteString(text + "\n")
}

// printErr writes a line to stderr.
func printErr(text string) {
	os.Stderr.WriteString(text + "\n")
}

// gateBroken is the exit code of a tool that could not run.
func gateBroken(message string) int {
	printErr(message)
	return 2
}
