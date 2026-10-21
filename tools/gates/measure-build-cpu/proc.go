package measurebuildcpu

// proc.go -- the /proc readers of the sampler.
//
// The numbers this gate exists for come out of the kernel's own counters:
// the busy-core series of /proc/stat, the per-process CPU time of
// /proc/<pid>/stat, the CPU count of /proc/stat and the model name of
// /proc/cpuinfo.

import (
	"os"
	"strconv"
	"strings"
)

// readCPUTimes returns (total jiffies, busy jiffies) from /proc/stat.
//
// Busy excludes idle AND iowait: a process blocked on I/O is not using a
// core, and counting iowait as busy would overstate how parallel the build
// was — which is exactly the number this gate exists to measure.
func readCPUTimes() (int64, int64) {
	data, err := os.ReadFile("/proc/stat")
	if err != nil {
		return 0, 0
	}
	line := string(data)
	if idx := strings.IndexByte(line, '\n'); idx >= 0 {
		line = line[:idx]
	}
	fields := strings.Fields(line)
	if len(fields) < 2 {
		return 0, 0
	}
	var total int64
	for _, field := range fields[1:] {
		value, err := strconv.ParseInt(field, 10, 64)
		if err != nil {
			continue
		}
		total += value
	}
	values := fields[1:]
	if len(values) < 3 {
		return total, total
	}
	idle, err := strconv.ParseInt(values[3], 10, 64)
	if err != nil {
		idle = 0
	}
	iowait := int64(0)
	if len(values) > 4 {
		if parsed, err := strconv.ParseInt(values[4], 10, 64); err == nil {
			iowait = parsed
		}
	}
	return total, total - idle - iowait
}

// cpuModel is the model name of the machine.
func cpuModel() string {
	data, err := os.ReadFile("/proc/cpuinfo")
	if err != nil {
		return "unknown"
	}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(strings.ToLower(line), "model name") {
			if idx := strings.IndexByte(line, ':'); idx >= 0 {
				return strings.TrimSpace(line[idx+1:])
			}
		}
	}
	return "unknown"
}

// statCPUCount is how many CPUs /proc/stat accounts for.
//
// This is NOT necessarily the number of schedulable CPUs: a sandbox can
// expose four schedulable CPUs while /proc/stat only carries the CPUs of
// the cgroup the job runs in. The number of busy CORES is derived from the
// jiffy deltas of /proc/stat, so the divisor has to be the one /proc/stat
// uses — otherwise "the machine is 99 % busy" and "one core is busy" look
// identical, which is the whole question this gate exists to answer.
func statCPUCount() int {
	data, err := os.ReadFile("/proc/stat")
	if err != nil {
		return 0
	}
	count := 0
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(line, "cpu") && !strings.HasPrefix(line, "cpu ") {
			count++
		}
	}
	return count
}

// readProcessCPU is the total CPU jiffies per process name (comm).
//
// The machine-level number says HOW MUCH of the CPU was used; this says
// WHO used it — the compiler server, the MSBuild nodes, the architecture
// test process. Without it, "the build used three cores" cannot be told
// apart from "the build used one core and something else used two".
func readProcessCPU() map[string]int64 {
	totals := map[string]int64{}
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return totals
	}
	for _, entry := range entries {
		name := entry.Name()
		if name == "" || name[0] < '0' || name[0] > '9' {
			continue
		}
		data, err := os.ReadFile("/proc/" + name + "/stat")
		if err != nil {
			continue
		}
		text := string(data)
		openParen := strings.IndexByte(text, '(')
		closeParen := strings.LastIndexByte(text, ')')
		if openParen < 0 || closeParen < openParen {
			continue
		}
		comm := text[openParen+1 : closeParen]
		fields := strings.Fields(text[closeParen+2:])
		if len(fields) < 13 {
			continue
		}
		utime, err := strconv.ParseInt(fields[11], 10, 64)
		if err != nil {
			continue
		}
		stime, err := strconv.ParseInt(fields[12], 10, 64)
		if err != nil {
			continue
		}
		totals[comm] += utime + stime
	}
	return totals
}
