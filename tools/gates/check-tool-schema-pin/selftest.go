package pinschema

// selftest.go -- the analysis must REJECT every synthetic input that breaks
// a rule.
//
// The fixtures are the failure paths of the gate, one each:
//
//   the honest tree       a tool and its pin agree
//   a schema that moved  the declaration moved, the pin did not
//   a tool without a pin the declaration has no hash
//   a pin without a tool the pin names a tool that is gone
//   an empty pin         the gate that reads nothing protects nothing
//
// An analysis that cannot fail is not an analysis.

import (
	"fmt"
	"os"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/toolschema"
)

// sampleSchema is the schema of the honest fixture: one string property.
func sampleSchema() *toolschema.Value {
	schema := toolschema.NewObject()
	schema.Set("type", toolschema.NewString("object"))
	properties := toolschema.NewObject()
	path := toolschema.NewObject()
	path.Set("type", toolschema.NewString("string"))
	properties.Set("path", path)
	schema.Set("properties", properties)
	return schema
}

// movedSchema is the same schema with a description added: the declaration
// moved and the pin did not.
func movedSchema() *toolschema.Value {
	schema := toolschema.NewObject()
	schema.Set("type", toolschema.NewString("object"))
	properties := toolschema.NewObject()
	path := toolschema.NewObject()
	path.Set("type", toolschema.NewString("string"))
	path.Set("description", toolschema.NewString("moved"))
	properties.Set("path", path)
	schema.Set("properties", properties)
	return schema
}

func selfTestRun() int {
	var failures []string

	expectReject := func(what string, entries []toolschema.Entry, pins map[string]string) {
		if len(Analyze(entries, pins)) == 0 {
			failures = append(failures, what)
		}
	}
	expectAccept := func(what string, entries []toolschema.Entry, pins map[string]string) {
		problems := Analyze(entries, pins)
		if len(problems) > 0 {
			failures = append(failures, fmt.Sprintf("%s (%s)", what, problems[0]))
		}
	}

	schema := sampleSchema()
	good := toolschema.Digest(schema)

	// The honest tree: one tool, one pin, the same hash.
	expectAccept("a tool and its pin agree",
		[]toolschema.Entry{{Name: "ReadTool", Schema: schema}},
		map[string]string{"ReadTool": good})

	// A planted divergence: the declaration moved, the pin did not.
	expectReject("a schema that diverged from its pin",
		[]toolschema.Entry{{Name: "ReadTool", Schema: movedSchema()}},
		map[string]string{"ReadTool": good})

	// A tool with no pin.
	expectReject("a tool without a pin",
		[]toolschema.Entry{{Name: "ReadTool", Schema: schema}},
		map[string]string{})

	// A pin that names a tool that is not there.
	expectReject("a pin without a tool",
		[]toolschema.Entry{{Name: "ReadTool", Schema: schema}},
		map[string]string{"ReadTool": good, "GoneTool": good})

	// An empty pin: the gate that reads nothing protects nothing.
	expectReject("an empty pin",
		[]toolschema.Entry{{Name: "ReadTool", Schema: schema}},
		map[string]string{})

	if len(failures) > 0 {
		for _, what := range failures {
			fmt.Fprintf(os.Stderr, "selftest: the analysis accepted %s\n", what)
		}
		return gate.Red
	}
	fmt.Println("selftest: every broken input was rejected")
	return gate.Green
}
