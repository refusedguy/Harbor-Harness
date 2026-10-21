// Package measurebuildcpu is the compiled port of tools/measure-build-cpu.py.
//
// The gate runs a build command while measuring what it costs: the wall
// time, the CPU utilisation sampled from /proc/stat, the CPU accounted per
// process, MSBuild's own task aggregates and the project timeline.
//
// It is a MEASUREMENT tool: it does not gate, it does not touch the build,
// and it works on any command.
//
// The text this prints is the text the python original printed, and the files
// it writes are the files the python original wrote.
package measurebuildcpu

import (
	"bufio"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// arrowRe is "  Harbor.Abstractions -> /home/runner/work/.../Harbor.Abstractions.dll"
// — printed when the project is BUILT, i.e. a completion marker.
var arrowRe = regexp.MustCompile(`^\s*(?P<name>\S+)\s+->\s+(?P<out>\S+\.(?:dll|exe))\s*$`)

// isBuildingRe is the project START marker. Normal verbosity only.
var isBuildingRe = regexp.MustCompile(`^\s*\d+>Project\s+"[^"]*"\s+\(\d+\)\s+is building\s+"(?P<proj>[^"]+)"`)

// doneRe is '  2>Done Building Project "/…/x.csproj" (default targets).'.
var doneRe = regexp.MustCompile(`^\s*\d+>Done Building Project\s+"(?P<proj>[^"]+)"`)

// perfRowRe is a Performance Summary row: "    434717 ms  Csc     112 calls".
var perfRowRe = regexp.MustCompile(`^\s*(?P<ms>\d+)\s+ms\s+(?P<task>\S+)\s+(?P<calls>\d+)\s+calls\s*$`)

// Run is the gate entry point.
func Run(args []string) int {
	label := ""
	outDir := ""
	interval := 1.0
	selfTest := false
	var command []string
	seenCommand := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		if arg == "--" {
			command = append(command, args[i+1:]...)
			break
		}
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--label":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--label needs a name")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			label = value
		case "--out-dir":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--out-dir needs a path")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			outDir = value
		case "--interval":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--interval needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, err := strconv.ParseFloat(value, 64)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--interval needs a number")
				return gate.Broken
			}
			interval = parsed
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates measure-build-cpu --label NAME --out-dir DIR [--interval SECONDS] -- COMMAND...")
			return gate.Green
		default:
			if strings.HasPrefix(arg, "-") && arg != "-" {
				fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
				return gate.Broken
			}
			if !seenCommand {
				command = []string{arg}
				seenCommand = true
				continue
			}
			command = append(command, arg)
		}
	}

	if selfTest {
		return selfTestRun()
	}
	if label == "" || outDir == "" {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: --label and --out-dir are required")
		return gate.Broken
	}
	if len(command) == 0 {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: no command given")
		return gate.Broken
	}

	if err := os.MkdirAll(outDir, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot create the output directory:", err)
		return gate.Broken
	}
	logPath := filepath.Join(outDir, label+".log")
	jsonPath := filepath.Join(outDir, label+".json")

	ncpu := runtime.NumCPU()
	ncpuStat := statCPUCount()
	if ncpuStat == 0 {
		ncpuStat = ncpu
	}

	sampler := newSampler(interval, ncpuStat)
	sampler.start()

	started := time.Now()
	log, err := os.Create(logPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot create the log:", err)
		return gate.Broken
	}
	reader, writer, err := os.Pipe()
	if err != nil {
		log.Close()
		fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot create the pipe:", err)
		return gate.Broken
	}
	process := exec.Command(command[0], command[1:]...)
	process.Stdout = writer
	process.Stderr = writer
	var copier sync.WaitGroup
	copier.Add(1)
	go func() {
		defer copier.Done()
		buffered := bufio.NewReader(reader)
		for {
			line, readErr := buffered.ReadString('\n')
			if len(line) > 0 {
				fmt.Fprintf(log, "%.3f | %s", time.Since(started).Seconds(), line)
			}
			if readErr != nil {
				return
			}
		}
	}()
	runErr := process.Run()
	writer.Close()
	copier.Wait()
	reader.Close()
	exitCode := 0
	if runErr != nil {
		if exit, ok := runErr.(*exec.ExitError); ok {
			exitCode = exit.ExitCode()
		} else {
			log.Close()
			sampler.stop()
			fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot run the command:", runErr)
			return gate.Broken
		}
	}
	wall := time.Since(started).Seconds()
	log.Close()
	sampler.stop()

	result := gatekit.NewJSONObject()
	result.Set("label", label)
	result.Set("command", command)
	result.Set("exit_code", exitCode)
	result.Set("wall_seconds", round(wall, 3))
	result.Set("ncpu", ncpu)
	result.Set("ncpu_stat", ncpuStat)
	result.Set("cpu_model", cpuModel())
	result.Set("cpu", summarizeCPU(sampler.samples, ncpuStat))
	var cpuSamples [][]float64
	for _, sample := range sampler.samples {
		cpuSamples = append(cpuSamples, []float64{round(sample[0], 3), sample[1]})
	}
	result.Set("cpu_samples", cpuSamples)
	result.Set("process_cpu_seconds", processCPUTotals(sampler.processSamples))

	// The measurement above is the point of the run: write it BEFORE the
	// analysis, so a failure in the analysis cannot lose the numbers.
	if err := os.WriteFile(jsonPath, []byte(gatekit.WriteJSON(result, 0)), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot write the measurement:", err)
		return gate.Broken
	}

	events, tasks, lastStamp := parseLog(logPath)
	result.Set("last_output_seconds", round(lastStamp, 3))
	result.Set("tasks", tasks)
	result.Set("projects_in_flight", inFlightProfile(events, wall))
	result.Set("timeline", events)

	if err := os.WriteFile(jsonPath, []byte(gatekit.WriteJSON(result, 0)), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, "measure-build-cpu: cannot write the measurement:", err)
		return gate.Broken
	}

	csc := gatekit.JSONObjectChild(tasks, "Csc")
	cscCalls := "None"
	cscMS := "None"
	if csc != nil {
		cscCalls = gatekit.JSONObjectText(csc, "calls")
		cscMS = gatekit.JSONObjectText(csc, "total_ms")
	}
	cpuSummary, _ := result.Get("cpu").(*gatekit.JSONObject)
	fmt.Printf("[measure-build-cpu] label=%s exit=%d wall=%.1fs avg_cores=%s max_cores=%s at4=%s csc_calls=%s csc_ms=%s -> %s\n",
		label, exitCode, wall,
		jsonText(cpuSummary, "avg_busy_cores"),
		jsonText(cpuSummary, "max_busy_cores"),
		jsonText(cpuSummary, "fraction_at_or_above_3_5_cores"),
		cscCalls, cscMS, jsonPath)

	if exitCode != 0 {
		return gate.Red
	}
	return gate.Green
}

// jsonText is a value of the summary, the way python printed it.
func jsonText(object *gatekit.JSONObject, key string) string {
	if object == nil {
		return "None"
	}
	switch typed := object.Get(key).(type) {
	case nil:
		return "None"
	case string:
		return typed
	case float64:
		return gatekit.PyFloat(typed)
	case int:
		return strconv.Itoa(typed)
	}
	return "None"
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// round is the python round(): the value is rounded to n decimals, and the
// result is a float.
//
// The rounding is the one of the exact binary value, with a tie going to
// the even digit, which is what python's round() does and what Go's
// strconv does.
func round(value float64, decimals int) float64 {
	text := strconv.FormatFloat(value, 'f', decimals, 64)
	parsed, err := strconv.ParseFloat(text, 64)
	if err != nil {
		return value
	}
	return parsed
}

// percentile is the python percentile of the gate.
func percentile(values []float64, fraction float64) float64 {
	if len(values) == 0 {
		return 0
	}
	ordered := append([]float64{}, values...)
	sort.Float64s(ordered)
	index := int(round(fraction*float64(len(ordered)-1), 0))
	if index < 0 {
		index = 0
	}
	if index > len(ordered)-1 {
		index = len(ordered) - 1
	}
	return ordered[index]
}

// summarizeCPU is the summary of the busy-core series.
func summarizeCPU(samples [][]float64, ncpu int) *gatekit.JSONObject {
	out := gatekit.NewJSONObject()
	if len(samples) == 0 {
		out.Set("samples", 0)
		return out
	}
	cores := make([]float64, 0, len(samples))
	for _, sample := range samples {
		cores = append(cores, sample[1])
	}
	total := 0.0
	peak := cores[0]
	at35, below15 := 0.0, 0.0
	for _, value := range cores {
		total += value
		if value > peak {
			peak = value
		}
		if value >= 3.5 {
			at35++
		}
		if value < 1.5 {
			below15++
		}
	}
	count := float64(len(cores))
	out.Set("samples", len(cores))
	out.Set("avg_busy_cores", round(total/count, 3))
	out.Set("p50_busy_cores", round(percentile(cores, 0.50), 3))
	out.Set("p95_busy_cores", round(percentile(cores, 0.95), 3))
	out.Set("max_busy_cores", round(peak, 3))
	out.Set("fraction_at_or_above_3_5_cores", round(at35/count, 4))
	out.Set("fraction_below_1_5_cores", round(below15/count, 4))
	out.Set("ncpu", ncpu)
	return out
}

// processSample is one sample of the per-process CPU time.
type processSample struct {
	t          float64
	cpuSeconds map[string]float64
}

// processCPUTotals sums the per-process CPU seconds over the whole run.
func processCPUTotals(samples []processSample) *gatekit.JSONObject {
	totals := map[string]float64{}
	for _, sample := range samples {
		for name, seconds := range sample.cpuSeconds {
			totals[name] += seconds
		}
	}
	names := make([]string, 0, len(totals))
	for name := range totals {
		names = append(names, name)
	}
	sort.SliceStable(names, func(i, j int) bool { return totals[names[i]] > totals[names[j]] })
	out := gatekit.NewJSONObject()
	for _, name := range names {
		out.Set(name, round(totals[name], 1))
	}
	return out
}

// projectKey normalises a project path or name to a comparable key.
func projectKey(pathOrName string) string {
	base := strings.ReplaceAll(pathOrName, "\\", "/")
	base = strings.TrimRight(base, "/")
	if idx := strings.LastIndexByte(base, '/'); idx >= 0 {
		base = base[idx+1:]
	}
	base = strings.TrimSuffix(base, ".csproj")
	return base
}

// parseLog recovers the timeline, the task aggregates and the last output
// stamp.
func parseLog(logPath string) ([]*gatekit.JSONObject, *gatekit.JSONObject, float64) {
	var events []*gatekit.JSONObject
	tasks := gatekit.NewJSONObject()
	lastStamp := 0.0

	fh, err := os.Open(logPath)
	if err != nil {
		return events, tasks, lastStamp
	}
	defer fh.Close()

	scanner := bufio.NewScanner(fh)
	scanner.Buffer(make([]byte, 0, 1<<16), 1<<24)
	for scanner.Scan() {
		line := scanner.Text()
		head, rest, _ := strings.Cut(line, "|")
		stamp, err := strconv.ParseFloat(strings.TrimSpace(head), 64)
		if err != nil {
			continue
		}
		if stamp > lastStamp {
			lastStamp = stamp
		}
		if match := arrowRe.FindStringSubmatch(rest); match != nil {
			event := gatekit.NewJSONObject()
			event.Set("t", stamp)
			event.Set("kind", "done")
			event.Set("project", projectKey(match[1]))
			event.Set("output", match[2])
			events = append(events, event)
			continue
		}
		if match := isBuildingRe.FindStringSubmatch(rest); match != nil {
			event := gatekit.NewJSONObject()
			event.Set("t", stamp)
			event.Set("kind", "start")
			event.Set("project", projectKey(match[1]))
			events = append(events, event)
			continue
		}
		if match := doneRe.FindStringSubmatch(rest); match != nil {
			event := gatekit.NewJSONObject()
			event.Set("t", stamp)
			event.Set("kind", "done")
			event.Set("project", projectKey(match[1]))
			events = append(events, event)
			continue
		}
		if match := perfRowRe.FindStringSubmatch(rest); match != nil {
			ms, err := strconv.Atoi(match[1])
			if err != nil {
				continue
			}
			calls, err := strconv.Atoi(match[3])
			if err != nil {
				continue
			}
			entry := gatekit.NewJSONObject()
			entry.Set("total_ms", ms)
			entry.Set("calls", calls)
			tasks.Set(match[2], entry)
		}
	}
	return events, tasks, lastStamp
}

// inFlightProfile is how many projects were in flight over time, and how
// often.
func inFlightProfile(events []*gatekit.JSONObject, wall float64) *gatekit.JSONObject {
	type stamp struct {
		t       float64
		project string
	}
	var starts, dones []stamp
	for _, event := range events {
		t, _ := event.Get("t").(float64)
		kind, _ := event.Get("kind").(string)
		project, _ := event.Get("project").(string)
		switch kind {
		case "start":
			starts = append(starts, stamp{t, project})
		case "done":
			dones = append(dones, stamp{t, project})
		}
	}
	sort.SliceStable(starts, func(i, j int) bool {
		if starts[i].t != starts[j].t {
			return starts[i].t < starts[j].t
		}
		return starts[i].project < starts[j].project
	})
	sort.SliceStable(dones, func(i, j int) bool {
		if dones[i].t != dones[j].t {
			return dones[i].t < dones[j].t
		}
		return dones[i].project < dones[j].project
	})

	profile := gatekit.NewJSONObject()
	profile.Set("available", false)
	profile.Set("projects_started", len(starts))
	profile.Set("projects_finished", len(dones))
	if len(starts) == 0 {
		return profile
	}

	grid := 0.0
	step := 1.0
	var counts []int
	startIndex, doneIndex := 0, 0
	open := map[string]bool{}
	for grid <= wall {
		for startIndex < len(starts) && starts[startIndex].t <= grid {
			open[starts[startIndex].project] = true
			startIndex++
		}
		for doneIndex < len(dones) && dones[doneIndex].t <= grid {
			delete(open, dones[doneIndex].project)
			doneIndex++
		}
		counts = append(counts, len(open))
		grid += step
	}
	if len(counts) == 0 {
		return profile
	}

	total := 0
	maximum := 0
	for _, count := range counts {
		total += count
		if count > maximum {
			maximum = count
		}
	}
	profile.Set("available", true)
	profile.Set("grid_seconds", step)
	profile.Set("avg_in_flight", round(float64(total)/float64(len(counts)), 3))
	profile.Set("max_in_flight", maximum)
	for _, threshold := range []int{1, 2, 3, 4, 6, 8, 12, 16, 24} {
		atOrAbove := 0
		for _, count := range counts {
			if count >= threshold {
				atOrAbove++
			}
		}
		profile.Set(fmt.Sprintf("fraction_at_or_above_%d", threshold),
			round(float64(atOrAbove)/float64(len(counts)), 4))
	}
	return profile
}

// sampler samples /proc/stat on a fixed interval.
type sampler struct {
	interval       time.Duration
	ncpu           float64
	mutex          sync.Mutex
	samples        [][]float64
	processSamples []processSample
	stopChannel    chan struct{}
	stopped        sync.WaitGroup
	prevTotal      int64
	prevBusy       int64
	prevProcess    map[string]int64
	started        time.Time
}

func newSampler(interval float64, ncpu int) *sampler {
	total, busy := readCPUTimes()
	return &sampler{
		interval:    time.Duration(interval * float64(time.Second)),
		ncpu:        float64(ncpu),
		stopChannel: make(chan struct{}),
		prevTotal:   total,
		prevBusy:    busy,
		prevProcess: readProcessCPU(),
	}
}

func (s *sampler) start() {
	s.started = time.Now()
	s.stopped.Add(1)
	go func() {
		defer s.stopped.Done()
		ticker := time.NewTicker(s.interval)
		defer ticker.Stop()
		previousTime := time.Now()
		previousProcess := s.prevProcess
		for {
			select {
			case <-s.stopChannel:
				return
			case now := <-ticker.C:
				total, busy := readCPUTimes()
				deltaTotal := total - s.prevTotal
				deltaBusy := busy - s.prevBusy
				elapsed := now.Sub(previousTime).Seconds()
				_ = elapsed
				cores := 0.0
				if deltaTotal > 0 {
					cores = (float64(deltaBusy) / float64(deltaTotal)) * s.ncpu
				}
				currentProcess := readProcessCPU()
				breakdown := map[string]float64{}
				for name, jiffies := range currentProcess {
					previous, ok := previousProcess[name]
					if !ok {
						previous = jiffies
					}
					delta := jiffies - previous
					if delta > 0 {
						breakdown[name] = round(float64(delta)/100.0, 2)
					}
				}
				s.mutex.Lock()
				s.samples = append(s.samples, []float64{round(now.Sub(s.started).Seconds(), 3), round(cores, 3)})
				s.processSamples = append(s.processSamples, processSample{
					t:          round(now.Sub(s.started).Seconds(), 3),
					cpuSeconds: breakdown,
				})
				s.mutex.Unlock()
				s.prevTotal, s.prevBusy = total, busy
				previousProcess = currentProcess
				previousTime = now
			}
		}
	}()
}

func (s *sampler) stop() {
	close(s.stopChannel)
	s.stopped.Wait()
}
