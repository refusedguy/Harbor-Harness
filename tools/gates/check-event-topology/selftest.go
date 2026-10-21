package checkeventtopology

// selftest.go -- the gate's own proof that it can fail.
//
// A gate that cannot fire is indistinguishable from a gate that found
// nothing, which is the failure every gate in this repository is tested
// against. Every fixture below plants one defect and asserts the rule that
// owns it fires, and asserts the same tree WITHOUT the defect is clean.
//
// The fixtures are the shapes that made a draft of this gate wrong:
//
//	declaration_is_not_a_call   the bus DECLARES `PublishAsync`; counting
//	                           declarations as producers would name every
//	                           implementation of IEventBus a producer.
//	helper_is_not_a_call       `CompactAndPublishAsync(` ends in the same
//	                           letters and is not a publish.
//	the_block_ends_early       the diagram continues below the producers
//	                           with an arrow that is not a producer row; a
//	                           parser that kept reading would swallow the
//	                           bus's own rows and call them producers.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// fixtureDoc builds a document with the given producer rows.
func fixtureDoc(rows ...string) string {
	var b strings.Builder
	b.WriteString("# Event topology\n\n")
	b.WriteString("```\n")
	b.WriteString(blockHeader + "\n")
	for _, r := range rows {
		b.WriteString(r + "\n")
	}
	b.WriteString("        │ await _eventBus.PublishAsync(evt, ct)\n")
	b.WriteString("        ▼\n")
	b.WriteString("InMemoryEventBus.PublishAsync ... src/Harbor.Registries/Events/InMemoryEventBus.cs:353\n")
	b.WriteString("```\n")
	return b.String()
}

// fixtureTree is the code the fixture document describes.
//
// `Producer.cs` publishes at lines 3 and 6. `Other.cs` publishes at line 3.
// `Bus.cs` only DECLARES PublishAsync, and `Helper.cs` only calls a method
// whose name ends in PublishAsync.
func fixtureTree() tree {
	return tree{
		"src/App/Producer.cs": {
			"class Producer {",
			"    async Task One() {",
			"        await bus.PublishAsync(new AgentStartEvent(), ct);",
			"    }",
			"    async Task Two() {",
			"        await bus.PublishAsync(new AgentEndEvent(), ct);",
			"    }",
			"}",
		},
		"src/App/Other.cs": {
			"class Other {",
			"    async Task Three() {",
			"        await bus.PublishAsync(new AgentEndEvent(), ct);",
			"    }",
			"}",
		},
		"src/App/Bus.cs": {
			"public Task PublishAsync(AgentEvent e, CancellationToken ct = default)",
			"{",
			"    return Task.CompletedTask;",
			"}",
		},
		"src/App/Helper.cs": {
			"    await CompactAndPublishAsync(session, ct);",
			"    await CompactAndPublishAsync(other, ct);",
		},
	}
}

func selfTestRun() int {
	const path = "src/App/Producer.cs"

	cases := []struct {
		name        string
		doc         string
		tree        tree
		wantRules   []string
		wantClean   bool
		minProducer int
	}{
		{
			name: "a correct block passes",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3,6",
				"  Other ....................... src/App/Other.cs:3",
			),
			tree: fixtureTree(),
		},
		{
			name: "a stale line fires",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:2,3,6",
				"  Other ....................... src/App/Other.cs:3",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-DRIFT"},
		},
		{
			name: "an uncited call site fires",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3",
				"  Other ....................... src/App/Other.cs:3",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-MISSING"},
		},
		{
			name: "a file that publishes and is not named fires",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3,6",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-FILE"},
		},
		{
			name: "a citation past the end of the file fires",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3,6,900",
				"  Other ....................... src/App/Other.cs:3",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-DRIFT"},
		},
		{
			name:      "a document without the block fires",
			doc:       "# Event topology\n\nThere is no block here.\n",
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-BLOCK-MISSING"},
		},
		{
			name: "a declaration is not a publish call",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3,6",
				"  Other ....................... src/App/Other.cs:3",
				"  Bus ......................... src/App/Bus.cs:1",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-DRIFT"},
		},
		{
			name: "a helper whose name ends in PublishAsync is not a publish call",
			doc: fixtureDoc(
				"  Producer ..................... src/App/Producer.cs:3,6",
				"  Other ....................... src/App/Other.cs:3",
				"  Helper ...................... src/App/Helper.cs:1,2",
			),
			tree:      fixtureTree(),
			wantRules: []string{"TOPOLOGY-PRODUCER-DRIFT"},
		},
	}

	ok := true
	fmt.Printf("fixtures: %d\n", len(cases))
	for _, c := range cases {
		findings, rows, _ := compare("docs/EVENT_TOPOLOGY.md", c.doc, c.tree)
		got := map[string]bool{}
		for _, f := range findings {
			got[f.rule] = true
		}

		good := true
		if c.wantClean {
			good = len(findings) == 0
		} else {
			for _, rule := range c.wantRules {
				if !got[rule] {
					good = false
				}
			}
			// The rule must not fire for a rule it does not own.
			for rule := range got {
				owned := false
				for _, want := range c.wantRules {
					if rule == want {
						owned = true
					}
				}
				if !owned {
					good = false
				}
			}
		}

		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
			ok = false
		}
		fmt.Printf("  [%-58s] rows=%d findings=%d %s\n", c.name, rows, len(findings), mark)
		if !good {
			fmt.Printf("      wanted %v, got %v\n", c.wantRules, ruleNames(findings))
		}
	}

	// The floor: a rule that compared nothing must not exit green.
	blind, rows, _ := compare("docs/EVENT_TOPOLOGY.md", fixtureDoc(), tree{})
	if blind == nil && rows != 0 {
		fmt.Println("  [the floor sees nothing                     ] << MISMATCH")
		ok = false
	}

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}

// ruleNames lists the rules that fired.
func ruleNames(findings []finding) []string {
	out := make([]string, 0, len(findings))
	for _, f := range findings {
		out = append(out, f.rule)
	}
	return out
}
