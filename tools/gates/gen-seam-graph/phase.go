package genseamgraph

// phase.go -- the timing trace.
//
// The scanner walks the whole tree, and when it is slow the question is
// always "which phase". The trace is off unless HARBOR_PHASE_TIMING is set,
// because a gate that prints timings is a gate whose output changes.

import (
	"fmt"
	"os"
	"time"
)

var lastPhase = time.Now()

// phase prints the elapsed time of a phase when HARBOR_PHASE_TIMING is set.
func phase(name string, now time.Time) {
	if os.Getenv("HARBOR_PHASE_TIMING") == "" {
		lastPhase = now
		return
	}
	fmt.Fprintf(os.Stderr, "%-12s %v\n", name, now.Sub(lastPhase))
	lastPhase = now
}
