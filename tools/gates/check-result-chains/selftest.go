package resultchains

// selftest.go -- the gate's own proof that it can fail.
//
// The python original planted the shapes in a throwaway tree and proved
// three things: the scanner finds them, the classifier tells them apart,
// and the rewrite produces the expected source. The port plants the same
// source and asserts the same three things.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// selfTestSource is the python original's SELF_TEST_SOURCE, byte for byte.
const selfTestSource = `using CSharpFunctionalExtensions;

namespace Probe;

internal static class Probe
{
    // A comment mentioning x.IsFailure must not be counted.
    internal static Result<int> Rewriteable(Result<int> x)
    {
        if (x.IsFailure)
            return x.ConvertFailure<int>();
        var v = x.Value;
        return Result.Success(v + 1);
    }

    internal static Result<int> Terminal(Result<int> x)
    {
        if (x.IsFailure)
            return x.ConvertFailure<int>();
        return Result.Success(0);
    }

    internal static Result<int> Mapped(Result<int> x)
    {
        if (x.IsFailure)
            return Result.Failure<int>($"boom: {x.Error}");
        var v = x.Value;
        return Result.Success(v);
    }

    internal static int SideEffect(Result<int> x)
    {
        if (x.IsFailure)
        {
            Log(x.Error);
            return 0;
        }
        return x.Value;
    }

    internal static object NotAResult(Result<int> x)
    {
        if (x.IsFailure)
            return new Thing(x.Error);
        return x.Value;
    }

    private static void Log(string message) { }
    private sealed class Thing { public Thing(string message) { } }
}
`

func selfTestRun() int {
	var failures []string

	tmp, err := os.MkdirTemp("", "harbor-resultchains-selftest")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	defer os.RemoveAll(tmp)

	src := filepath.Join(tmp, "src", "Probe")
	if err := os.MkdirAll(src, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	if err := os.WriteFile(filepath.Join(src, "Probe.cs"), []byte(selfTestSource), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}

	sites := collect([]string{"src"}, tmp)
	byKind := map[string]int{}
	for _, s := range sites {
		byKind[s.kind]++
	}

	expected := map[string]int{
		kindRewriteable:  1, // Rewriteable
		kindTerminalSame: 1, // Terminal
		kindChainMapped:  1, // Mapped
		kindSideEffect:   1, // SideEffect
		kindNonResult:    1, // NotAResult
	}
	for kind, want := range expected {
		got := byKind[kind]
		if got != want {
			failures = append(failures, fmt.Sprintf("%s: expected %d, got %d", kind, want, got))
		}
	}

	// The comment line must not produce a site.
	want := 0
	for _, count := range expected {
		want += count
	}
	if len(sites) != want {
		failures = append(failures, fmt.Sprintf("expected %d sites, got %d", want, len(sites)))
	}

	// The rewrite must produce the expected source.
	changed, _ := applySites(sites, tmp)
	if changed != 1 {
		failures = append(failures, fmt.Sprintf("expected 1 rewrite, got %d", changed))
	}
	rewritten, err := os.ReadFile(filepath.Join(src, "Probe.cs"))
	if err != nil {
		failures = append(failures, err.Error())
		return gate.Red
	}
	text := string(rewritten)
	rewrittenMethod := after(text, "Rewriteable(Result<int> x)")
	if !strings.Contains(rewrittenMethod, "return x.Bind(v => Result.Success(v + 1));") {
		failures = append(failures, "the rewrite did not produce the expected Bind call")
	}
	if strings.Contains(rewrittenMethod, "x.ConvertFailure<int>()") {
		failures = append(failures, "the rewrite left the old guard behind")
	}
	// The other methods must be untouched.
	for _, name := range []string{"Terminal", "Mapped", "SideEffect", "NotAResult"} {
		body := after(text, name+"(Result<int> x)")
		if !strings.Contains(body, "if (x.IsFailure)") {
			failures = append(failures, fmt.Sprintf("the rewrite touched the %s method", name))
		}
	}

	// After the rewrite the census must report no chain candidate there.
	afterSites := collect([]string{"src"}, tmp)
	afterKinds := map[string]int{}
	for _, s := range afterSites {
		afterKinds[s.kind]++
	}
	if afterKinds[kindRewriteable] != 0 {
		failures = append(failures, "the rewrite left a rewriteable site behind")
	}

	if len(failures) > 0 {
		for _, f := range failures {
			fmt.Fprintf(os.Stderr, "FAIL: %s\n", f)
		}
		return gate.Red
	}
	fmt.Print("self-test OK: the scanner finds every planted shape, tells them apart, " +
		"and the rewrite produces the expected source.\n")
	return gate.Green
}

// after is the text between the marker and the next closing brace, the way
// the python original sliced it.
func after(text string, marker string) string {
	parts := strings.SplitN(text, marker, 2)
	if len(parts) < 2 {
		return ""
	}
	rest := parts[1]
	if i := strings.Index(rest, "}"); i >= 0 {
		return rest[:i]
	}
	return rest
}
