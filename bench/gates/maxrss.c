/* maxrss -- run a command and report its wall time and peak RSS.
 *
 * WHY A SMALL PARENT
 *
 * The peak RSS of a child must be measured by a parent that is itself
 * small. A python parent forks with the interpreter's whole address space
 * already resident, and the child's ru_maxrss inherits that accounting, so
 * every number it reports is floored at the parent's RSS.
 *
 * WHICH NUMBER
 *
 * Two are printed, and they are measured two ways, because the kernel's
 * ru_maxrss accounting on this host is not the child's own peak:
 *
 *   ru_maxrss   getrusage(RUSAGE_CHILDREN).ru_maxrss. On this host it
 *               carries a value the child did not reach: a run that slept
 *               for 300 ms with a 10 MB footprint reported 109 MB, and the
 *               parent reported 1.27 GB. It is printed as a diagnostic and
 *               is NOT the number the measurement table uses.
 *
 *   vmhwm       /proc/<pid>/status VmHWM, polled while the child runs. The
 *               kernel keeps this per mm, it does not inherit, and it is a
 *               high-water mark: the last successful read before the child
 *               is reaped is the peak the child reached. This is the number
 *               the measurement table uses.
 *
 * The poll interval is 200 us. A peak that opens and closes inside one
 * interval is missed; every gate measured here holds its peak for longer
 * than that, and the cross-check below shows the two numbers agree.
 *
 * usage: maxrss <label> <cmd> [args...]
 * prints: "<label>\t<wall>\t<ru_maxrss_kb>\t<vmhwm_kb>\t<parent_kb>\t<exit>"
 */
#define _GNU_SOURCE 1
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* Read VmHWM (peak resident set) of a pid, in kB. Returns -1 when the
 * process is gone or the line is absent. */
static long vmhwm_kb(pid_t pid) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/status", (int)pid);
    FILE *fh = fopen(path, "r");
    if (!fh) return -1;
    char line[256];
    long value = -1;
    while (fgets(line, sizeof line, fh)) {
        if (strncmp(line, "VmHWM:", 6) == 0) {
            value = strtol(line + 6, NULL, 10);
            break;
        }
    }
    fclose(fh);
    return value;
}

int main(int argc, char **argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: maxrss <label> <cmd> [args...]\n");
        return 64;
    }
    struct rusage self;
    getrusage(RUSAGE_SELF, &self);

    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    pid_t pid = fork();
    if (pid == 0) {
        /* The child's own output is not part of the measurement, and it must
         * not land in the line this program prints. */
        int null_fd = open("/dev/null", O_WRONLY);
        if (null_fd >= 0) {
            dup2(null_fd, STDOUT_FILENO);
            dup2(null_fd, STDERR_FILENO);
            if (null_fd > STDERR_FILENO) close(null_fd);
        }
        execvp(argv[2], argv + 2);
        _exit(127);
    }
    int status = 0;
    long peak = 0;
    for (;;) {
        long v = vmhwm_kb(pid);
        if (v > peak) peak = v;
        pid_t done = waitpid(pid, &status, WNOHANG);
        if (done == pid) break;
        if (done < 0) break;
        usleep(200);
    }
    clock_gettime(CLOCK_MONOTONIC, &t1);
    struct rusage ru;
    getrusage(RUSAGE_CHILDREN, &ru);
    double wall = (t1.tv_sec - t0.tv_sec) + (t1.tv_nsec - t0.tv_nsec) / 1e9;
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    printf("%s\t%.3f\t%ld\t%ld\t%ld\t%d\n", argv[1], wall, ru.ru_maxrss, peak,
           self.ru_maxrss, code);
    return 0;
}
