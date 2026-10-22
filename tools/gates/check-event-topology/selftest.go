package eventtopology

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture below is a divergence between the document and the tree, and
// the gate has to NAME it. A gate that cannot say no is a gate that enforces
// nothing.
//
// The fixtures run against a SYNTHETIC tree, not against this repository: a
// self-test that edited the real document would make the gate red on a tree
// that is fine. The synthetic tree is built in a temporary directory and
// scan() is called directly, so nothing here depends on the working
// directory or on the state of the checkout.
//
// The shapes that made a draft of this gate wrong are pinned too:
//
//   comment_call     a `.PublishAsync(` that appears only inside a line
//                    comment or a string literal is not a call site. The
//                    first draft counted it, so a document that cited the
//                    comment line passed.
//   helper_name      a method whose NAME ends in PublishAsync but which does
//                    not call the bus is not a call site, and the DECLARATION
//                    of PublishAsync is not one either.
//   truncated_block  a producer block that stops early must be a finding, not
//                    a shorter list. A gate that silently drops rows is a
//                    gate that reads as complete.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The synthetic producer file.
//
//	line  7  a real PublishAsync call
//	line  8  a real PublishAsync call
//	line 13  the same text inside a line COMMENT
//	line 14  the same text inside a string LITERAL
const fixtureProducer = `using Harbor.Abstractions.Events;

public sealed class Producer
{
    public async Task Run(IEventBus bus)
    {
        await bus.PublishAsync(new TurnStartEvent(1)).ConfigureAwait(false);
        await bus.PublishAsync(new TurnEndEvent(1)).ConfigureAwait(false);
    }

    private Task Helper() => Task.CompletedTask;

    // await bus.PublishAsync(new AgentEndEvent(1));
    public string Text => "await bus.PublishAsync(new TurnStartEvent(2))";
}
`

// The synthetic bus file: the declaration of PublishAsync is on line 5.
const fixtureBus = `namespace Harbor.Fixture;

public sealed class Bus
{
    public Task PublishAsync(object e) => Task.CompletedTask;
}
`

// The synthetic subscriber file: the Subscribe call is on line 7.
const fixtureSubscriber = `namespace Harbor.Fixture;

public sealed class Subscriber
{
    public IDisposable Subscribe(IEventBus bus)
    {
        return bus.Subscribe((e, c) => ValueTask.CompletedTask);
    }
}
`

// The synthetic producer that the document does NOT name. It exists only for
// the fixture that proves a producer file with no row is a finding.
const fixtureHidden = `namespace Harbor.Fixture;

public sealed class Hidden
{
    public async Task Run(IEventBus bus)
    {
        await bus.PublishAsync(new ToolExecutionEndEvent(1)).ConfigureAwait(false);
    }
}
`

// The line numbers the fixtures state.
const (
	producerSiteA  = 7
	producerSiteB  = 8
	busDecl        = 5
	subscriberSite = 7
)

// fixtureDoc renders a document whose producer block names the given spec
// for the producer file.
func fixtureDoc(producerSpec, busSpec, subscriberSpec string) string {
	return strings.Join([]string{
		"# Event topology",
		"",
		"```",
		"producers (await PublishAsync directly)",
		"  Producer ......................... src/Fixture/Producer.cs:" + producerSpec,
		"        │ await _eventBus.PublishAsync(evt, ct)",
		"        ▼",
		"Bus.PublishAsync ................... src/Fixture/Bus.cs:" + busSpec,
		"subscribers (all in-process)",
		"  Subscriber ....................... src/Fixture/Subscriber.cs:" + subscriberSpec,
		"```",
		"",
	}, "\n")
}

// fixtureTree writes the synthetic tree and returns its root and the source
// list the resolver reads. The hidden producer is written only when
// `hidden` is true.
func fixtureTree(hidden bool) (string, []string) {
	root, err := os.MkdirTemp("", "event-topology-selftest-")
	if err != nil {
		fmt.Println("cannot create a temporary directory:", err)
		os.Exit(int(gate.Broken))
	}
	files := map[string]string{
		"src/Fixture/Producer.cs":   fixtureProducer,
		"src/Fixture/Bus.cs":        fixtureBus,
		"src/Fixture/Subscriber.cs": fixtureSubscriber,
		// The sinks document is read by the gate as well, so the synthetic
		// tree carries a correct one. Without it every fixture would report
		// a missing document, and a finding that every fixture carries is
		// a finding nobody reads.
		sinksDocPath: fixtureSinksDoc(fmt.Sprint(subscriberSite)),
	}
	if hidden {
		files["src/Fixture/Hidden.cs"] = fixtureHidden
	}
	sources := make([]string, 0, len(files))
	for name, body := range files {
		path := filepath.Join(root, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			fmt.Println("cannot create", filepath.Dir(path), ":", err)
			os.Exit(int(gate.Broken))
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			fmt.Println("cannot write", path, ":", err)
			os.Exit(int(gate.Broken))
		}
		if name == sinksDocPath {
			continue
		}
		sources = append(sources, name)
	}
	return root, sources
}

// scanFixture runs the gate against the synthetic tree and returns the rule
// codes it reported.
func scanFixture(doc string, hidden bool) ([]string, stats) {
	root, sources := fixtureTree(hidden)
	defer os.RemoveAll(root)
	findings, st := scan(scanInput{root: root, docText: doc, sources: sources})
	codes := make([]string, 0, len(findings))
	for _, f := range findings {
		codes = append(codes, f.rule)
	}
	return codes, st
}

// has reports whether a list of rule codes holds one.
func has(codes []string, code string) bool {
	for _, c := range codes {
		if c == code {
			return true
		}
	}
	return false
}

// fixtureSinksDoc renders the subscriber table of the sinks document.
func fixtureSinksDoc(subscriberSpec string) string {
	return strings.Join([]string{
		"# Event bus sinks",
		"",
		"| Site | Verdict | Reason |",
		"|---|---|---|",
		"| `src/Fixture/Subscriber.cs:" + subscriberSpec + "` | mandatory | the fixture |",
		"",
	}, "\n")
}

// scanSinksFixture runs the gate's sinks-document check against the synthetic
// tree.
func scanSinksFixture(doc string) ([]string, int) {
	root, sources := fixtureTree(false)
	defer os.RemoveAll(root)
	if err := os.WriteFile(filepath.Join(root, sinksDocPath), []byte(doc), 0o644); err != nil {
		fmt.Println("cannot write", sinksDocPath, ":", err)
		os.Exit(int(gate.Broken))
	}
	res := newResolver(root, sources)
	findings, count, err := checkSinksSubscribers(root, res)
	if err != nil {
		fmt.Println("cannot read the sinks document:", err)
		os.Exit(int(gate.Broken))
	}
	codes := make([]string, 0, len(findings))
	for _, f := range findings {
		codes = append(codes, f.rule)
	}
	return codes, count
}

func selfTestRun() int {
	ok := true
	fail := func(format string, args ...any) {
		ok = false
		fmt.Printf("      "+format+"\n", args...)
	}

	correct := fixtureDoc(
		fmt.Sprintf("%d,%d", producerSiteA, producerSiteB),
		fmt.Sprint(busDecl),
		fmt.Sprint(subscriberSite))

	cases := []struct {
		name     string
		doc      string
		hidden   bool
		wantCode string
		mustFail bool
	}{
		{
			name: "a correct document reports nothing",
			doc:  correct,
		},
		{
			name:     "a stale line number fires",
			doc:      fixtureDoc(fmt.Sprintf("%d,%d", producerSiteA, producerSiteB+1), "5", "7"),
			wantCode: "EVT-TOPO-NOT-PUBLISH",
			mustFail: true,
		},
		{
			name:     "a missing call site fires",
			doc:      fixtureDoc(fmt.Sprint(producerSiteA), "5", "7"),
			wantCode: "EVT-TOPO-SITE-UNDOCUMENTED",
			mustFail: true,
		},
		{
			name:     "a producer file with no row fires",
			doc:      correct,
			hidden:   true,
			wantCode: "EVT-TOPO-PRODUCER-MISSING",
			mustFail: true,
		},
		{
			name:     "a bus row that is not the declaration fires",
			doc:      fixtureDoc(fmt.Sprintf("%d,%d", producerSiteA, producerSiteB), "2", "7"),
			wantCode: "EVT-TOPO-BUS-DECL",
			mustFail: true,
		},
		{
			name:     "a subscriber line without a Subscribe call fires",
			doc:      fixtureDoc(fmt.Sprintf("%d,%d", producerSiteA, producerSiteB), "5", "3"),
			wantCode: "EVT-TOPO-NOT-SUBSCRIBE",
			mustFail: true,
		},
		{
			name:     "a cited line past the end of the file fires",
			doc:      fixtureDoc(fmt.Sprintf("%d,%d", producerSiteA, producerSiteB), "5", "900"),
			wantCode: "EVT-TOPO-CITE-BLANK",
			mustFail: true,
		},
		{
			name:     "a truncated producer block fires",
			doc:      correct[:strings.Index(correct, "        │")],
			wantCode: "EVT-TOPO-BLOCK-BROKEN",
			mustFail: true,
		},
		{
			name:     "a document with no producer table fires",
			doc:      "# Event topology\n\nNothing here.\n",
			wantCode: "EVT-TOPO-BLOCK-MISSING",
			mustFail: true,
		},
	}

	fmt.Printf("fixtures: %d\n", len(cases))
	for _, c := range cases {
		codes, st := scanFixture(c.doc, c.hidden)
		fired := has(codes, c.wantCode)
		good := fired == c.mustFail
		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-42s] %-30s rows=%d sites=%d %s\n", c.name, verdictOf(codes), st.producerRows, st.treeSites, mark)
		if !good {
			fail("expected %s, got %v", c.wantCode, codes)
		}
	}

	// The subscriber table of the sinks document: the same claim, the same
	// shape, and it had drifted the same way.
	sinksCases := []struct {
		name     string
		spec     string
		mustFail bool
	}{
		{name: "the sinks table with a correct row is clean", spec: fmt.Sprint(subscriberSite)},
		{name: "the sinks table with a drifted row fires", spec: "3", mustFail: true},
	}
	for _, c := range sinksCases {
		codes, rows := scanSinksFixture(fixtureSinksDoc(c.spec))
		fired := has(codes, "EVT-TOPO-NOT-SUBSCRIBE")
		good := fired == c.mustFail
		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-42s] %-30s rows=%d %s\n", c.name, verdictOf(codes), rows, mark)
		if !good {
			fail("expected a finding for %q, got %v", c.spec, codes)
		}
	}

	// The call-site detection itself, on the shapes that made a draft of
	// this gate wrong.
	lines := strings.Split(fixtureProducer, "\n")
	got := publishSites(lines)
	want := []int{producerSiteA, producerSiteB}
	if len(got) != len(want) {
		fail("the producer file has %d PublishAsync call site(s), expected %d", len(got), len(want))
	} else {
		for i, n := range got {
			if n != want[i] {
				fail("call site %d is on line %d, expected %d", i, n, want[i])
			}
		}
	}
	if decl := declarationLine(strings.Split(fixtureBus, "\n")); decl != busDecl {
		fail("the PublishAsync declaration is on line %d, expected %d", decl, busDecl)
	}
	if sites := publishSites(strings.Split(fixtureBus, "\n")); len(sites) != 0 {
		fail("the bus file has %d PublishAsync CALL site(s); the declaration is not a call", len(sites))
	}
	subSites := subscribeSites(strings.Split(fixtureSubscriber, "\n"))
	if len(subSites) != 1 || subSites[0] != subscriberSite {
		fail("the subscriber file has %v Subscribe call site(s), expected [%d]", subSites, subscriberSite)
	}
	// A helper whose NAME ends in PublishAsync is not a call site.
	helper := []string{"private Task CompactAndPublishAsync() => Task.CompletedTask;"}
	if sites := publishSites(helper); len(sites) != 0 {
		fail("a helper named CompactAndPublishAsync was read as %d call site(s)", len(sites))
	}

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}

// verdictOf renders the outcome of one fixture for the printed table.
func verdictOf(codes []string) string {
	if len(codes) == 0 {
		return "clean"
	}
	return strings.Join(codes, ",")
}
