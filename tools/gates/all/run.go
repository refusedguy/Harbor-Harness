package all

import (
	"bytes"
	"fmt"
	"io"
	"os"
	"os/exec"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"harbor.local/gates/internal/gate"
)

// The defaults of the runner.
const (
	// defaultTimeout is how long one gate may take before the run gives up
	// on it. A gate that hangs is a gate that cannot run, and a run that
	// hangs is a CI job that never finishes.
	defaultTimeout = 15 * time.Minute
	// maxOutput is how much of a gate's output is kept. A gate that prints
	// megabytes has a bug, and the report wants the first line and the
	// findings, not the flood.
	maxOutput = 1 << 20
	// gitSlots is how many gates that read the git history run at the same
	// time. They block on I/O and they open a git process each; a run of
	// many gates must not open many git processes at once.
	gitSlots = 4
)

// options are the flags of the command.
type options struct {
	selfTest        bool
	list            bool
	pending         bool
	requireCompiled bool
	base            string
	workers         int
	timeout         time.Duration
}

// parseArgs reads the flags.
//
// A flag this command does not know is a broken invocation and not a gate
// that found something: exit code 2, and the reason on stderr.
func parseArgs(args []string) (options, error) {
	opts := options{workers: runtime.GOMAXPROCS(0), timeout: defaultTimeout}
	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--self-test":
			opts.selfTest = true
		case arg == "--list":
			opts.list = true
		case arg == "--pending":
			opts.pending = true
		case arg == "--require-compiled":
			opts.requireCompiled = true
		case arg == "--base":
			if i+1 >= len(args) {
				return opts, fmt.Errorf("--base needs a commit")
			}
			i++
			opts.base = args[i]
		case arg == "--workers":
			if i+1 >= len(args) {
				return opts, fmt.Errorf("--workers needs a number")
			}
			i++
			n := 0
			for _, r := range args[i] {
				if r < '0' || r > '9' {
					return opts, fmt.Errorf("--workers needs a number")
				}
				n = n*10 + int(r-'0')
			}
			if n < 1 {
				return opts, fmt.Errorf("--workers needs a number of at least 1")
			}
			opts.workers = n
		case arg == "--help" || arg == "-h":
			printUsage(os.Stdout)
			return opts, errHelp
		default:
			return opts, fmt.Errorf("unknown argument: %s", arg)
		}
	}
	return opts, nil
}

// errHelp is the sentinel that says "the flags were read, there is nothing
// left to do".
var errHelp = fmt.Errorf("help")

// printUsage prints the help of the command.
func printUsage(w *os.File) {
	fmt.Fprintln(w, "usage: harbor-gates all [flags]")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Runs every gate that needs nothing but the tree, in one command,")
	fmt.Fprintln(w, "and prints one table: gate, exit code, time, first output line.")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Exit codes: 0 green, 1 red, 2 the gate could not run.")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "flags:")
	fmt.Fprintln(w, "  --list               print the list of tree gates and what each needs")
	fmt.Fprintln(w, "  --pending            print the gates that are still python, one per line:")
	fmt.Fprintln(w, "                       <name> TAB <command>, for the local runner")
	fmt.Fprintln(w, "  --require-compiled   fail when a gate of the list is still python")
	fmt.Fprintln(w, "  --base <commit>      the commit the git gates read their range from")
	fmt.Fprintln(w, "  --workers <n>        how many gates run at the same time (default: GOMAXPROCS)")
	fmt.Fprintln(w, "  --self-test          prove the failure paths of this command")
	fmt.Fprintln(w, "  --help               print this text")
}

// Run is the entry point of the command.
func Run(args []string) int {
	opts, err := parseArgs(args)
	if err != nil {
		if err == errHelp {
			return gate.Green
		}
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	if opts.selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	base := opts.base
	if base == "" {
		base = resolveBase(root)
	}

	if opts.list {
		printList(os.Stdout)
		return gate.Green
	}
	if opts.pending {
		return printPending(os.Stdout, root, base)
	}

	self, err := os.Executable()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot find this binary to run the gates with:", err)
		return gate.Broken
	}
	runner := childRunner{self: self, root: root, timeout: opts.timeout}
	return runAll(os.Stdout, root, base, opts, runner)
}

// printList prints the list of tree gates and what each of them needs.
func printList(w *os.File) {
	fmt.Fprintf(w, "%d gate(s) that need nothing but the tree:\n\n", len(treeGates))
	for _, g := range sortedGates() {
		fmt.Fprintf(w, "  %-28s %s\n", g.name, g.needs)
		if len(g.args) > 0 {
			fmt.Fprintf(w, "  %-28s args: %s\n", "", strings.Join(g.args, " "))
		}
	}
	fmt.Fprintln(w)
	fmt.Fprintln(w, "The list is in tools/gates/all/gates.go, and it is checked against")
	fmt.Fprintln(w, "the workflows: a gate that a workflow runs with an artifact on the")
	fmt.Fprintln(w, "command line is not a tree gate, and does not belong here.")
}

// printPending prints the gates of the list that are still python.
//
// The lines are machine-readable: the name, a tab, and the command that
// runs the gate. The local runner (tools/check-local.sh) reads them and
// runs the ones the compiled binary does not run yet, so that the local
// command and the CI command cover the same gates.
func printPending(w *os.File, root, base string) int {
	gates := sortedGates()
	pending := 0
	for _, g := range gates {
		if g.writer {
			continue
		}
		state, _ := resolve(root, g)
		if state != statePython {
			continue
		}
		line, err := pendingLine(g, root, base)
		if err != nil {
			fmt.Fprintf(os.Stderr, "%s: %v\n", g.name, err)
			continue
		}
		fmt.Fprintln(w, line)
		pending++
	}
	if pending == 0 {
		fmt.Fprintln(os.Stderr, "no gate of the list is still python")
	}
	return gate.Green
}

// resolveBase finds the commit the range of the git gates starts at.
//
// The CI resolves it the same way: the merge base of the pull request and
// the branch it targets. Locally the same rule gives the merge base of the
// working tree and origin/dev, which is the commit the local tree is
// compared against.
func resolveBase(root string) string {
	for _, ref := range []string{"origin/dev", "dev", "@{upstream}"} {
		out, err := runGit(root, "merge-base", "HEAD", ref)
		if err == nil && out != "" {
			return out
		}
	}
	return ""
}

// runGit runs a git command in the repository and returns its output.
func runGit(root string, args ...string) (string, error) {
	cmd := exec.Command("git", args...)
	cmd.Dir = root
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(out)), nil
}

// result is what one gate produced.
type result struct {
	gate    treeGate
	code    int
	elapsed time.Duration
	stdout  string
	stderr  string
	err     error
}

// planned is a gate of the list and what it turned out to be in this tree.
type planned struct {
	gate   treeGate
	state  state
	reason string
}

// firstLine is the first line of the gate's output: the line a reader of
// the report sees.
//
// The stdout comes first because that is where the gates print their
// verdict, and the stderr is the fallback for the gate that prints its
// verdict there.
func (r result) firstLine() string {
	if line := firstNonEmpty(r.stdout); line != "" {
		return line
	}
	return firstNonEmpty(r.stderr)
}

// output is the whole of what the gate printed, for the gate that found
// something.
func (r result) output() string {
	out := strings.TrimRight(r.stdout, "\n")
	errText := strings.TrimRight(r.stderr, "\n")
	switch {
	case out == "":
		return errText
	case errText == "":
		return out
	}
	return out + "\n" + errText
}

func firstNonEmpty(text string) string {
	for _, line := range strings.Split(text, "\n") {
		if trimmed := strings.TrimSpace(line); trimmed != "" {
			return trimmed
		}
	}
	return ""
}

// gateRunner runs one gate. The self-test replaces it with a fake, so the
// runner can be proven without a tree.
type gateRunner interface {
	run(g treeGate, args []string) (code int, stdout string, stderr string, err error)
}

// childRunner runs a gate as a child process of this binary.
//
// A child process, not a call into the gate's Run: the gates print their
// verdict to the standard output, and a run that prints one table for
// twenty-five gates needs the output of each of them. A child process gives
// that for free, and it gives two more things: a gate that panics takes
// down its own process and not the run, and the exit code of the gate is
// the exit code of the binary, which is the contract.
type childRunner struct {
	self    string
	root    string
	timeout time.Duration
}

func (c childRunner) run(g treeGate, args []string) (int, string, string, error) {
	stdout := &limitedBuffer{limit: maxOutput}
	stderr := &limitedBuffer{limit: maxOutput}
	cmd := exec.Command(c.self, append([]string{g.name}, args...)...)
	cmd.Dir = c.root
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	if err := cmd.Start(); err != nil {
		return gate.Broken, stdout.String(), stderr.String(),
			fmt.Errorf("cannot start the gate: %w", err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		if err == nil {
			return gate.Green, stdout.String(), stderr.String(), nil
		}
		var exitErr *exec.ExitError
		if ok := asExitError(err, &exitErr); ok {
			code := exitErr.ExitCode()
			if code < 0 {
				// The gate was killed by a signal. It did not find
				// something; it did not finish.
				return gate.Broken, stdout.String(), stderr.String(),
					fmt.Errorf("the gate was killed by a signal")
			}
			return code, stdout.String(), stderr.String(), nil
		}
		return gate.Broken, stdout.String(), stderr.String(), err
	case <-time.After(c.timeout):
		_ = cmd.Process.Kill()
		<-done
		return gate.Broken, stdout.String(), stderr.String(),
			fmt.Errorf("the gate did not finish in %s", c.timeout)
	}
}

// asExitError is errors.As for an *exec.ExitError, without importing
// errors for one call.
func asExitError(err error, target **exec.ExitError) bool {
	for err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			*target = exitErr
			return true
		}
		unwrapper, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = unwrapper.Unwrap()
	}
	return false
}

// limitedBuffer is a buffer that stops growing after its limit.
type limitedBuffer struct {
	limit int
	buf   bytes.Buffer
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	room := b.limit - b.buf.Len()
	if room <= 0 {
		return len(p), nil
	}
	if len(p) > room {
		b.buf.Write(p[:room])
		return len(p), nil
	}
	return b.buf.Write(p)
}

func (b *limitedBuffer) String() string { return b.buf.String() }

// runAll runs the tree gates and prints the report.
//
// The order of the work is the order of the list; the order of the REPORT
// is by name, whatever order the gates finished in. The two are different
// on purpose: the work is parallel because the gates are independent, and
// the report is sorted because a report two people read has to be the same
// report.
func runAll(w io.Writer, root, base string, opts options, runner gateRunner) int {
	gates := sortedGates()

	// The gates of the list, and what each of them turned out to be.
	plan := make([]planned, 0, len(gates))
	var missing []planned
	var pending []planned
	var writers []planned
	for _, g := range gates {
		state, reason := resolve(root, g)
		p := planned{gate: g, state: state, reason: reason}
		plan = append(plan, p)
		switch state {
		case stateMissing:
			missing = append(missing, p)
		case statePython:
			pending = append(pending, p)
		case stateWriter:
			writers = append(writers, p)
		}
	}

	// A gate that cannot run at all is the loudest thing in the run: it is
	// named, and the run says it could not run. The other gates still run,
	// because a gate that cannot start must not hide the gates that can.
	results := runPool(plan, opts.workers, runner, root, base)

	report := buildReport(plan, results, opts, base)
	printReport(w, report)

	if len(missing) > 0 {
		fmt.Fprintln(w)
		fmt.Fprintf(w, "%d gate(s) of the list cannot run at all:\n", len(missing))
		for _, p := range missing {
			fmt.Fprintf(w, "  %-28s %s\n", p.gate.name, p.reason)
		}
		fmt.Fprintln(w)
		fmt.Fprintln(w, "The list is in tools/gates/all/gates.go. A gate that is in the")
		fmt.Fprintln(w, "list and in neither tools/gates/ nor tools/ is a gate that stopped")
		fmt.Fprintln(w, "existing, and a list that names it is a list that lies.")
		return gate.Broken
	}

	if opts.requireCompiled && len(pending) > 0 {
		fmt.Fprintln(w)
		fmt.Fprintf(w, "%d gate(s) of the list are still python:\n", len(pending))
		for _, p := range pending {
			fmt.Fprintf(w, "  %-28s %s\n", p.gate.name, p.reason)
		}
		return gate.Broken
	}

	return report.code
}

// runPool runs the compiled gates, several at a time.
//
// The gates are independent, each one is a child process, and the work is
// I/O over the tree: a pool of workers is worth more here than any cleverness
// inside one gate.
//
// The gates that read the git history get their own small pool: they block
// on I/O and they open a git process each, and a run of twenty-five gates
// must not open twenty-five git processes at once.
func runPool(plan []planned, workers int, runner gateRunner, root, base string) []result {
	slots := gitSlots
	if workers < slots {
		slots = workers
	}
	gitLimit := make(chan struct{}, slots)

	tasks := make(chan planned)
	var wg sync.WaitGroup
	var mu sync.Mutex
	results := make([]result, 0, len(plan))

	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for p := range tasks {
				res := runOne(p.gate, runner, root, base, gitLimit)
				mu.Lock()
				results = append(results, res)
				mu.Unlock()
			}
		}()
	}

	for _, p := range plan {
		if p.state != stateCompiled {
			continue
		}
		tasks <- p
	}
	close(tasks)
	wg.Wait()

	sort.Slice(results, func(i, j int) bool { return results[i].gate.name < results[j].gate.name })
	return results
}

// runOne runs one gate and measures it.
func runOne(g treeGate, runner gateRunner, root, base string, gitLimit chan struct{}) result {
	args, err := expand(g, root, base)
	if err != nil {
		return result{gate: g, code: gate.Broken, err: err}
	}
	if g.git {
		gitLimit <- struct{}{}
		defer func() { <-gitLimit }()
	}
	start := time.Now()
	code, stdout, stderr, err := runner.run(g, args)
	elapsed := time.Since(start)
	if err != nil && code == gate.Green {
		code = gate.Broken
	}
	return result{gate: g, code: code, elapsed: elapsed, stdout: stdout, stderr: stderr, err: err}
}
