/* Private POSIX supervisor. Git owns a separate process group whose leader is
 * deliberately left waitable until the inherited actors have actually stopped.
 * Control descriptors are private inherited pipe ends, never repository files.
 */
#define _DARWIN_C_SOURCE
#define _POSIX_C_SOURCE 200809L
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>
#ifdef __APPLE__
#include <libproc.h>
#include <sys/proc.h>
#else
#include <dirent.h>
#include <sys/prctl.h>
#endif

static volatile sig_atomic_t stop_requested;
static int events_fd;
static void request_stop(int signal_number) { (void)signal_number; stop_requested = 1; }
static int record(uint32_t kind, pid_t actor, uint32_t value)
{
    uint32_t message[4] = { htonl(0x47455031), htonl(kind), htonl((uint32_t)actor), htonl(value) };
    size_t position = 0;
    while (position < sizeof(message)) {
        ssize_t count = write(events_fd, (const char *)message + position, sizeof(message) - position);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return -1;
        position += (size_t)count;
    }
    return 0;
}
static int64_t milliseconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) return -1;
    return (int64_t)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}

/* No numeric PID is used to signal a non-child. The only group signaled is
 * reserved by the unreaped primary child. Linux subreaper children additionally
 * remain waitable by this single-threaded supervisor until it reaps them.
 */
static int live_members(pid_t group, int stopping, int force)
{
    int live = 0;
#ifdef __APPLE__
    int bytes = proc_listpids(PROC_PGRP_ONLY, (uint32_t)group, NULL, 0);
    if (bytes < 0) return -1;
    size_t capacity = (size_t)bytes + 64 * sizeof(pid_t);
    pid_t *members = malloc(capacity);
    if (members == NULL) return -1;
    bytes = proc_listpids(PROC_PGRP_ONLY, (uint32_t)group, members, (int)capacity);
    if (bytes < 0) { free(members); return -1; }
    if ((size_t)bytes == capacity) { free(members); errno = EAGAIN; return -1; }
    for (int index = 0; index < bytes / (int)sizeof(pid_t); index++) {
        if (members[index] == group) continue;
        struct proc_bsdinfo info;
        int count = proc_pidinfo(members[index], PROC_PIDTBSDINFO, 0, &info, sizeof(info));
        if (count == 0 && (errno == ESRCH || errno == ENOENT)) continue;
        if (count != (int)sizeof(info)) { free(members); if (errno == 0) errno = EIO; return -1; }
        if (info.pbi_pgid == (uint32_t)group && info.pbi_status != SZOMB) live++;
    }
    (void)stopping; (void)force;
    free(members);
#else
    int reaped = 0;
    DIR *processes = opendir("/proc");
    if (processes == NULL) return -1;
    struct dirent *entry;
    while ((entry = readdir(processes)) != NULL) {
        char *end; long candidate = strtol(entry->d_name, &end, 10);
        if (*entry->d_name == '\0' || *end != '\0' || candidate <= 1 || candidate == group || candidate == getpid()) continue;
        char path[128], text[4096];
        if (snprintf(path, sizeof(path), "/proc/%ld/stat", candidate) >= (int)sizeof(path)) continue;
        FILE *file = fopen(path, "r");
        if (file == NULL) { if (errno == ENOENT || errno == ESRCH || errno == EACCES) continue; closedir(processes); return -1; }
        size_t length = fread(text, 1, sizeof(text) - 1, file); fclose(file); text[length] = '\0';
        char *name_end = strrchr(text, ')'); char state; long parent, member_group;
        if (name_end == NULL || sscanf(name_end + 2, "%c %ld %ld", &state, &parent, &member_group) != 3) continue;
        int adopted = parent == getpid();
        if (member_group != group && !adopted) continue;
        if (state == 'Z' || state == 'X' || state == 'x') {
            if (adopted) { int status; if (waitpid((pid_t)candidate, &status, WNOHANG) == candidate) reaped = 1; }
            continue;
        }
        live++;
        if (stopping && adopted) {
            /* Our own unreaped child cannot be recycled while we signal it. */
            if (force) { (void)kill((pid_t)candidate, SIGSTOP); (void)kill((pid_t)candidate, SIGKILL); }
            else (void)kill((pid_t)candidate, SIGINT);
        }
    }
    closedir(processes);
    /* Reparenting can change an earlier candidate in this finite snapshot.
     * A pass which reaped a bridge must be followed by a fresh full pass. */
    if (live == 0 && reaped) return 1;
#endif
    return live;
}

static int descriptor(const char *text)
{
    char *end; long value = strtol(text, &end, 10);
    if (*text == '\0' || *end != '\0' || value < 3 || value > 0x7fffffff) return -1;
    return (int)value;
}

int main(int argc, char **argv)
{
    if (argc < 5 || strcmp(argv[3], "--") != 0) return 2;
    int control_fd = descriptor(argv[1]); events_fd = descriptor(argv[2]);
    if (control_fd < 0 || events_fd < 0 || control_fd == events_fd) return 2;
    if (fcntl(control_fd, F_SETFD, FD_CLOEXEC) != 0 || fcntl(events_fd, F_SETFD, FD_CLOEXEC) != 0) return 2;
    if (getsid(0) != getpid() && setsid() < 0) { (void)record(4, getpid(), (uint32_t)errno); return 1; }
    signal(SIGPIPE, SIG_IGN); signal(SIGCHLD, SIG_DFL);
    struct sigaction action; memset(&action, 0, sizeof(action)); action.sa_handler = request_stop;
    sigemptyset(&action.sa_mask);
    sigaction(SIGINT, &action, NULL); sigaction(SIGTERM, &action, NULL); sigaction(SIGHUP, &action, NULL);
#ifndef __APPLE__
    if (prctl(PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0) != 0) { (void)record(4, getpid(), (uint32_t)errno); return 1; }
#endif
    if (record(1, getpid(), 0) != 0) return 1;
    char acknowledgement = 0;
    ssize_t count;
    do { count = read(control_fd, &acknowledgement, 1); } while (count < 0 && errno == EINTR && !stop_requested);
    if (count != 1 || acknowledgement != 'A' || stop_requested) return 1;
    int exec_error[2];
    if (pipe(exec_error) != 0) { (void)record(4, getpid(), (uint32_t)errno); return 1; }
    if (fcntl(exec_error[1], F_SETFD, FD_CLOEXEC) != 0) { (void)record(4, getpid(), (uint32_t)errno); return 1; }
    pid_t child = fork();
    if (child < 0) { (void)record(4, getpid(), (uint32_t)errno); return 1; }
    if (child == 0) {
        close(exec_error[0]); close(control_fd); close(events_fd);
        for (int number = 1; number < NSIG; number++) signal(number, SIG_DFL);
        sigset_t empty; sigemptyset(&empty); sigprocmask(SIG_SETMASK, &empty, NULL);
        if (setpgid(0, 0) == 0) execv(argv[4], &argv[4]);
        int error = errno; ssize_t sent;
        do { sent = write(exec_error[1], &error, sizeof(error)); } while (sent < 0 && errno == EINTR);
        _exit(127);
    }
    close(exec_error[1]);
    if (setpgid(child, child) != 0 && errno != EACCES && errno != ESRCH) stop_requested = 1;
    if (fcntl(exec_error[0], F_SETFL, O_NONBLOCK) != 0) stop_requested = 1;
    int native_error = 0, exec_done = 0; size_t exec_bytes = 0;
    int exited = 0, failure = 0; int64_t stopping_at = -1;
    while (1) {
        struct pollfd descriptors[2] = {
            { .fd = control_fd, .events = POLLIN | POLLHUP },
            { .fd = exec_done ? -1 : exec_error[0], .events = POLLIN | POLLHUP }
        };
        int polled = poll(descriptors, 2, 10);
        if (polled < 0 && errno != EINTR) { stop_requested = 1; failure = errno; }
        if (polled > 0 && (descriptors[0].revents & (POLLIN | POLLHUP | POLLERR))) {
            char command; ssize_t received = read(control_fd, &command, 1);
            if (received <= 0 || command == 'C') stop_requested = 1;
            else { stop_requested = 1; failure = EPROTO; }
        }
        if (!exec_done && polled > 0 && (descriptors[1].revents & (POLLIN | POLLHUP | POLLERR))) {
            ssize_t received = read(exec_error[0], (char *)&native_error + exec_bytes, sizeof(native_error) - exec_bytes);
            if (received > 0) {
                exec_bytes += (size_t)received;
                if (exec_bytes == sizeof(native_error)) {
                    (void)record(4, child, (uint32_t)native_error); stop_requested = 1; exec_done = 1;
                }
            } else if (received == 0) {
                exec_done = 1;
                if (exec_bytes != 0) { (void)record(4, child, EIO); stop_requested = 1; }
                else if (record(2, child, 0) != 0) stop_requested = 1;
            } else if (errno != EINTR && errno != EAGAIN) { failure = errno; stop_requested = 1; exec_done = 1; }
        }
        if (stop_requested && stopping_at < 0) stopping_at = milliseconds();
        int force = stop_requested && (stopping_at < 0 || milliseconds() - stopping_at >= 500);
        if (stop_requested) {
            if (force) { (void)kill(-child, SIGSTOP); (void)kill(-child, SIGKILL); }
            else (void)kill(-child, SIGINT);
        }
        siginfo_t state; memset(&state, 0, sizeof(state));
        if (waitid(P_PID, (id_t)child, &state, WEXITED | WNOHANG | WNOWAIT) != 0) {
            if (errno == EINTR) continue;
            failure = errno; stop_requested = 1; break;
        }
        exited = state.si_pid == child;
        int members = live_members(child, stop_requested, force);
        if (members < 0) { if (errno == EAGAIN) continue; failure = errno; stop_requested = 1; continue; }
        if (exited && members == 0) break;
    }
    close(exec_error[0]);
    /* Only now may the group leader's PID be released. */
    int status;
    do { count = waitpid(child, &status, 0); } while (count < 0 && errno == EINTR);
    if (count != child) { (void)record(4, child, (uint32_t)errno); return 1; }
    if (failure != 0) { (void)record(4, child, (uint32_t)failure); return 1; }
    int code = stop_requested ? 130 : (WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status));
    (void)record(3, child, (uint32_t)code);
    return code;
}
