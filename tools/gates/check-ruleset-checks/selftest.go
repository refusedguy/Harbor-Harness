package checkrulesetchecks

// selftest.go -- the gate's proof that it can fail on the defect it exists to
// catch, and that it stays quiet when the tree is whole.
//
// The planted control is a ruleset that requires a status-check name no job
// publishes. Every fixture is driven through the SAME parser and auditor the
// gate runs, so a green self-test means "the matcher fired on the broken
// ruleset and stayed quiet on the clean ones".

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// A ruleset that requires one real check and one that no job publishes.
const plantedRuleset = `{
  "name": "dev",
  "target": "branch",
  "rules": [
    {
      "type": "required_status_checks",
      "parameters": {
        "required_status_checks": [
          {"context": "gate"},
          {"context": "required check that does not exist"}
        ]
      }
    },
    {
      "type": "deletion"
    }
  ]
}
`

// A ruleset whose every required check is published by a job.
const cleanRuleset = `{
  "name": "dev",
  "rules": [
    {
      "type": "required_status_checks",
      "parameters": {
        "required_status_checks": [
          {"context": "gate"},
          {"context": "checker self-test"}
        ]
      }
    }
  ]
}
`

// A workflow with three named jobs and one that falls back to its id.
const syntheticWorkflow = `name: dev
on:
  push:
    branches: [dev]
permissions: {}
jobs:
  gate:
    name: gate
    steps:
      - run: echo gate
  selftest:
    name: checker self-test
    steps:
      - run: echo selftest
  public-api:
    name: public-api freeze
    steps:
      - run: echo api
  tree:
    steps:
      - run: echo tree
`

// selfTestRun drives the fixtures and reports what the gate did.
func selfTestRun() int {
	var failures []string
	ran := 0

	check := func(what string, ok bool, detail string) {
		ran++
		if !ok {
			failures = append(failures, what)
			fmt.Printf("  FAIL  %s\n", what)
			if detail != "" {
				fmt.Printf("        %s\n", detail)
			}
		}
	}

	// 1. The parser reads every required context, and only those.
	planted, err := parseRuleset("dev.json", []byte(plantedRuleset))
	check("the planted ruleset parses", err == nil, fmt.Sprint(err))
	contexts := make([]string, 0, len(planted))
	for _, req := range planted {
		contexts = append(contexts, req.context)
	}
	check("both required contexts are read",
		len(contexts) == 2 && contexts[0] == "gate" && contexts[1] == "required check that does not exist",
		strings.Join(contexts, ", "))
	check("the ruleset name travels with each context",
		len(planted) == 2 && planted[0].ruleset == "dev.json" && planted[1].ruleset == "dev.json", "")

	// 2. The workflow parser reads job names, and falls back to the id when a
	// job has no `name:`.
	jobs := parseWorkflowJobs("ci.yml", syntheticWorkflow)
	check("all four jobs are read", len(jobs) == 4, fmt.Sprintf("got %d", len(jobs)))
	displays := make([]string, 0, len(jobs))
	for _, job := range jobs {
		displays = append(displays, job.display())
	}
	check("explicit names and the id fallback are collected",
		strings.Join(displays, ",") == "gate,checker self-test,public-api freeze,tree",
		strings.Join(displays, ","))

	// 3. The real defect: a required check no job publishes is reported, and
	// the finding names the ruleset and the context.
	found := audit(planted, jobs)
	check("a rule that requires a nonexistent check is reported", len(found) == 1, fmt.Sprintf("%d findings", len(found)))
	if len(found) == 1 {
		check("the finding names the ruleset", found[0].ruleset == "dev.json", found[0].ruleset)
		check("the finding names the check", strings.Contains(found[0].msg, "required check that does not exist"), found[0].msg)
		check("the finding carries the missing-check code", found[0].code == "RULESET-CHECK-NO-JOB", found[0].code)
	} else {
		check("the finding names the ruleset", false, "no finding")
		check("the finding names the check", false, "no finding")
		check("the finding carries the missing-check code", false, "no finding")
	}

	// 4. A clean ruleset stays green, and the comparison ignores case.
	clean, err := parseRuleset("dev.json", []byte(cleanRuleset))
	check("the clean ruleset parses", err == nil, fmt.Sprint(err))
	check("a ruleset whose checks all have jobs stays green", len(audit(clean, jobs)) == 0, "")
	cased := []requiredCheck{{ruleset: "dev.json", context: "GATE"}}
	check("the comparison is case-insensitive", len(audit(cased, jobs)) == 0, "")

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, "; "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}
