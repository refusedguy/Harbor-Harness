package benchset

// workflows.go -- what the workflow files say CI runs, and with which
// arguments.
//
// The reader is deliberately small. It does not parse YAML: it reads the
// shape the workflow files of this repository have, which is the shape the
// python policy checker reads too, and it reads it the same way, so the two
// see the same invocations.
//
// A workflow file is:
//
//   jobs:
//     <job id>:
//       steps:
//         - name: ...
//           run: |
//             python3 tools/<gate>.py <args>
//
// The reader finds the job a step belongs to, joins the step's `run:` block
// into one command, and looks for the three shapes an invocation has:
//
//	python3 tools/<name>.py      a python gate
//	bash tools/<name>.sh          a shell gate
//	tools/bin/harbor-gates-linux-<arch> <gate>   a compiled gate
//
// The arguments of an invocation are the text between it and the NEXT
// invocation in the same command, so a step that runs two gates gives each
// of them its own arguments.

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// invocation is one gate a workflow runs.
type invocation struct {
	workflow string
	job      string
	name     string
	kind     string
	args     string
}

// selfTest is true when the invocation runs the gate's own self-test.
//
// A self-test proves the gate can fail. It is not a run of the gate, and a
// benchmark that timed it would time the wrong thing.
func (i invocation) selfTest() bool {
	return strings.Contains(i.args, "--self-test") || strings.Contains(i.args, "--selftest")
}

// place is where the invocation happens, for a message that names it.
func (i invocation) place() string {
	return fmt.Sprintf("%s · %s", i.workflow, i.job)
}

// The three shapes of an invocation.
var (
	pythonRe   = regexp.MustCompile(`python3(?:\s+-[^\s]+)*\s+tools/([A-Za-z0-9_.\-]+\.py)`)
	shellRe    = regexp.MustCompile(`bash\s+tools/([A-Za-z0-9_.\-]+\.sh)`)
	compiledRe = regexp.MustCompile(`tools/bin/harbor-gates-linux-(x64|arm64)\s+([A-Za-z0-9_.\-]+)`)
)

// A job id: two spaces, an id, a colon.
var jobRe = regexp.MustCompile(`^  ([A-Za-z0-9_.\-]+):\s*$`)

// A flag: two dashes and a name. A bare `--` is the shell's end-of-options
// marker, not a flag.
var flagRe = regexp.MustCompile(`^--[A-Za-z0-9][A-Za-z0-9_.\-]*$`)

// readWorkflows returns every invocation every workflow makes.
func readWorkflows(root string) ([]invocation, error) {
	dir := filepath.Join(root, ".github", "workflows")
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, fmt.Errorf("cannot read %s: %w", dir, err)
	}
	var out []invocation
	for _, entry := range entries {
		name := entry.Name()
		if !strings.HasSuffix(name, ".yml") && !strings.HasSuffix(name, ".yaml") {
			continue
		}
		text, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			return nil, fmt.Errorf("cannot read %s: %w", name, err)
		}
		out = append(out, parseWorkflow(name, string(text))...)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].workflow != out[j].workflow {
			return out[i].workflow < out[j].workflow
		}
		if out[i].job != out[j].job {
			return out[i].job < out[j].job
		}
		return out[i].name < out[j].name
	})
	return out, nil
}

// parseWorkflow returns the invocations of one workflow file.
func parseWorkflow(name, text string) []invocation {
	var out []invocation
	inJobs := false
	job := ""
	var step []string

	flush := func() {
		if job != "" && len(step) > 0 {
			out = append(out, invocationsIn(name, job, stepCommand(step))...)
		}
		step = nil
	}

	for _, line := range strings.Split(text, "\n") {
		if line != "" && !isSpace(line[0]) {
			inJobs = strings.TrimSpace(line) == "jobs:"
			flush()
			job = ""
			continue
		}
		if !inJobs {
			continue
		}
		if m := jobRe.FindStringSubmatch(line); m != nil {
			flush()
			job = m[1]
			continue
		}
		if strings.HasPrefix(line, "      - ") {
			flush()
			step = []string{line}
			continue
		}
		if step != nil {
			step = append(step, line)
		}
	}
	flush()
	return out
}

// stepCommand is the shell command of a step, with the block joined.
//
// A step's `run:` is either a one-liner or a block (`|`, `|-`, `>`, `>-`).
// The block's lines are joined with a space, which is what the shell would
// see for a folded scalar, and the flags inside it are the flags the gate
// is run with.
func stepCommand(lines []string) string {
	var command []string
	started := false
	runIndent := 0
	for _, line := range lines {
		trimmed := strings.TrimSpace(line)
		indent := len(line) - len(strings.TrimLeft(line, " \t"))
		if !started {
			if strings.HasPrefix(trimmed, "run:") {
				started = true
				runIndent = indent
				rest := strings.TrimSpace(strings.TrimPrefix(trimmed, "run:"))
				switch rest {
				case "", "|", ">", "|-", ">-", "|+", ">+":
					// A block: the lines below carry the command.
				default:
					command = append(command, rest)
				}
			}
			continue
		}
		if indent <= runIndent {
			break
		}
		command = append(command, trimmed)
	}
	joined := strings.Join(command, " ")
	joined = strings.ReplaceAll(joined, "\\ ", " ")
	return strings.TrimSpace(joined)
}

// match is one invocation found in a command.
type match struct {
	name  string
	kind  string
	start int
	end   int
}

// invocationsIn returns the invocations of one command, with their
// arguments.
func invocationsIn(workflow, job, command string) []invocation {
	var matches []match
	for _, m := range pythonRe.FindAllStringSubmatchIndex(command, -1) {
		matches = append(matches, match{name: command[m[2]:m[3]], kind: "python", start: m[0], end: m[1]})
	}
	for _, m := range shellRe.FindAllStringSubmatchIndex(command, -1) {
		matches = append(matches, match{name: command[m[2]:m[3]], kind: "shell", start: m[0], end: m[1]})
	}
	for _, m := range compiledRe.FindAllStringSubmatchIndex(command, -1) {
		name := command[m[4]:m[5]]
		// `--list` is the binary's own listing, not a gate.
		if strings.HasPrefix(name, "-") {
			continue
		}
		matches = append(matches, match{name: name, kind: "compiled", start: m[0], end: m[1]})
	}
	sort.Slice(matches, func(i, j int) bool { return matches[i].start < matches[j].start })

	out := make([]invocation, 0, len(matches))
	for index, m := range matches {
		end := len(command)
		if index+1 < len(matches) {
			// The arguments run from the end of this invocation to the
			// START of the next one.
			end = matches[index+1].start
		}
		out = append(out, invocation{
			workflow: workflow,
			job:      job,
			name:     m.name,
			kind:     m.kind,
			args:     strings.TrimSpace(command[m.end:end]),
		})
	}
	return out
}

// flagsOf returns the arguments of an invocation as the keys the gate
// compares.
//
// The VALUES of the artifact paths are not compared: the value of an
// artifact path is the harness's business (an empty directory of the right
// shape), and the flag is the gate's business. The VALUE of a floor IS
// compared: `--min-files 250` is the gate's non-vacuity number, and a
// benchmark that ran the gate with a different floor timed a different gate.
//
// A flag written with an `=` sign (`--min-rows=1`) is one key, and so is a
// flag written with a space (`--min-rows 1`).
func flagsOf(args string) []string {
	tokens := strings.Fields(args)
	var out []string
	for index := 0; index < len(tokens); index++ {
		token := strings.Trim(tokens[index], `"'`)
		if !flagRe.MatchString(token) {
			continue
		}
		if strings.Contains(token, "=") {
			out = append(out, token)
			continue
		}
		if strings.HasPrefix(token, "--min-") &&
			index+1 < len(tokens) && !strings.HasPrefix(tokens[index+1], "--") {
			value := strings.Trim(tokens[index+1], `"'`)
			out = append(out, token+"="+value)
			index++
			continue
		}
		out = append(out, token)
	}
	return out
}

func isSpace(b byte) bool {
	return b == ' ' || b == '\t'
}
