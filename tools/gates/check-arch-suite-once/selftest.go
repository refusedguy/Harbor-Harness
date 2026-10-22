package archsuiteonce

// selftest.go -- the gate's own proof that it can fail.
//
// Each rule fires on a planted defect; the clean fixture stays clean. The
// python original plants the same defects against the same fixture text and
// asserts the same thing; the port does the same.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// edit replaces the first occurrence of old with new, and fails loudly when
// the fixture does not contain old: a mutation that did not apply would
// make the self-test pass on a gate that had not been tested.
func edit(text string, old string, new string) (string, bool) {
	if !strings.Contains(text, old) {
		return text, false
	}
	return strings.Replace(text, old, new, 1), true
}

func selfTestRun() int {
	var failures []string
	fail := func(description string, findings []string, wantCode string, wantAny bool) {
		if wantAny {
			for _, finding := range findings {
				if strings.Contains(finding, wantCode) {
					return
				}
			}
			failures = append(failures, fmt.Sprintf(
				"the planted defect (%s) did not fire %s: %s",
				description, wantCode, join(findings)))
			return
		}
		for _, finding := range findings {
			if strings.Contains(finding, wantCode) {
				failures = append(failures, fmt.Sprintf(
					"a %s finding fired where it must not: %s",
					wantCode, join(findings)))
				return
			}
		}
	}

	clean := evaluate(fixtureWorkflow, fixtureProps, 4)
	if len(clean) > 0 {
		failures = append(failures, "the clean fixture is not clean: "+join(clean))
	}

	type mutation struct {
		code        string
		description string
		apply       func(string) (string, bool)
	}
	cases := []mutation{
		{
			code:        "EXEC-ONCE",
			description: "the suite listed in two shards runs it twice",
			apply: func(w string) (string, bool) {
				return edit(w,
					"              Harbor.Storage.Tests\n",
					"              Harbor.Storage.Tests\n              "+
						"Harbor.Architecture.Tests\n")
			},
		},
		{
			code:        "EXEC-ONCE",
			description: "the suite listed in no shard leaves the lane with no gate",
			apply: func(w string) (string, bool) {
				return edit(w, "              Harbor.Architecture.Tests\n", "")
			},
		},
		{
			code:        "EXEC-ONCE",
			description: "a command that runs the suite is a second execution",
			apply: func(w string) (string, bool) {
				return edit(w,
					"      - name: Run shard ${{ matrix.shard }}\n",
					"      - name: The gate, again\n"+
						"        run: dotnet exec "+
						"tests/Harbor.Architecture.Tests/bin/Release/net10.0/"+
						"Harbor.Architecture.Tests.dll\n"+
						"      - name: Run shard ${{ matrix.shard }}\n")
			},
		},
		{
			code:        "GATE-OFF-PR",
			description: "a build job that says nothing about the gate runs it",
			apply: func(w string) (string, bool) {
				return edit(w,
					"      HarborArchGate: 'false'\n",
					"")
			},
		},
		{
			code:        "GATE-OFF-PR",
			description: "a build job whose gate is on for every event runs it twice",
			apply: func(w string) (string, bool) {
				return edit(w,
					"      HarborArchGate: 'false'\n",
					"      HarborArchGate: 'true'\n")
			},
		},
		{
			code:        "GATE-OFF-PR",
			description: "a second job that compiles the solution without the flag runs it",
			apply: func(w string) (string, bool) {
				return edit(w,
					"            -p:HarborArchGate=false \\\n",
					"")
			},
		},
		{
			code:        "GATE-OFF-PUSH",
			description: "a gate that is on for a push runs the suite twice on the merge path",
			apply: func(w string) (string, bool) {
				return edit(w,
					"      HarborArchGate: 'false'\n",
					"      HarborArchGate: ${{ github.event_name == 'pull_request' "+
						"&& 'false' || 'true' }}\n")
			},
		},
	}

	for _, c := range cases {
		mutated, ok := c.apply(fixtureWorkflow)
		if !ok {
			failures = append(failures,
				"the fixture does not carry the shape the mutation needs: "+c.description)
			continue
		}
		fail(c.description, evaluate(mutated, fixtureProps, 4), c.code, true)
	}

	// A near miss: a `run:` block that only NAMES the suite is not an
	// execution. The guard-triage reporter passes the project's name to
	// another tool, and counting that made this gate red on a tree where the
	// suite runs exactly once.
	namedOnly, ok := edit(fixtureWorkflow,
		"      - name: Run shard ${{ matrix.shard }}\n",
		"      - name: Triage the guard firings\n"+
			"        run: |\n"+
			"          python3 tools/guard-triage.py \\\n"+
			"            --project Harbor.Architecture.Tests\n"+
			"      - name: Run shard ${{ matrix.shard }}\n")
	if !ok {
		failures = append(failures, "the fixture does not carry the 'named only' shape")
	} else {
		fail("a block that only NAMES the suite was counted as an execution",
			evaluate(namedOnly, fixtureProps, 4), "EXEC-ONCE", false)
	}

	// GATE-DEFAULT and GATE-CONDITION read the props, not the workflow.
	propsDefault, ok := edit(fixtureProps, "true</HarborArchGate>", "false</HarborArchGate>")
	if !ok {
		failures = append(failures, "the fixture does not carry the default to turn off")
	} else {
		fail("the gate's default turned off",
			evaluate(fixtureWorkflow, propsDefault, 4), "GATE-DEFAULT", true)
	}

	propsCondition, ok := edit(fixtureProps,
		"AND '$(HarborArchGate)' == 'true' AND ", "")
	if !ok {
		failures = append(failures, "the fixture does not carry the target's condition")
	} else {
		fail("the target no longer asks the property",
			evaluate(fixtureWorkflow, propsCondition, 4), "GATE-CONDITION", true)
	}

	propsGone, ok := edit(fixtureProps,
		`Name="HarborArchitectureGate"`, `Name="SomethingElse"`)
	if !ok {
		failures = append(failures, "the fixture does not carry the gate target")
	} else {
		fail("the gate target is gone",
			evaluate(fixtureWorkflow, propsGone, 4), "GATE-CONDITION", true)
	}

	// The floor: a workflow with fewer shards than the floor must be a
	// finding, not a quiet pass.
	fail("a workflow with fewer shards than the floor",
		evaluate(fixtureWorkflow, fixtureProps, 9), "floor", true)

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Printf("::error::self-test failed: %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("ok: the architecture-once gate still fails on every planted defect")
	return gate.Green
}

// join renders a list of findings for a failure message.
func join(findings []string) string {
	if len(findings) == 0 {
		return "the checker found nothing"
	}
	return strings.Join(findings, "; ")
}
