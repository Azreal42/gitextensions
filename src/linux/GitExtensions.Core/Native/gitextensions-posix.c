/* Private POSIX supervisor. Git owns a separate process group whose leader is
 * deliberately left waitable until the inherited actors have actually stopped.
 * Control descriptors are private inherited pipe ends, never repository files.
 */
#define _DARWIN_C_SOURCE
#ifndef __APPLE__
#define _DEFAULT_SOURCE
#endif
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
#include <sys/syscall.h>
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

#ifndef __APPLE__
struct owned_actor { pid_t pid; int descriptor; int64_t interrupted; int forced; };
static struct owned_actor *actors;
static size_t actor_count, actor_capacity;
static int discovery_started;
static int actor_alive(size_t index)
{
    if (actors[index].descriptor < 0) return 0;
    struct pollfd descriptor = { .fd = actors[index].descriptor, .events = POLLIN };
    int result;
    do { result = poll(&descriptor, 1, 0); } while (result < 0 && errno == EINTR);
    if (result < 0) return -1;
    if (descriptor.revents & (POLLIN | POLLHUP)) return 0;
    if (descriptor.revents != 0) { errno = EIO; return -1; }
    return 1;
}
static int actor_index(pid_t pid)
{
    for (size_t index = 0; index < actor_count; index++) {
        int alive = actor_alive(index);
        if (alive < 0) return -2;
        if (!alive && actors[index].descriptor >= 0) {
            close(actors[index].descriptor); actors[index].descriptor = -1;
        }
        if (alive && actors[index].pid == pid) return (int)index;
    }
    return -1;
}
static int process_state(pid_t pid, pid_t *parent, pid_t *session, char *state)
{
    char path[128], text[4096];
    if (snprintf(path, sizeof(path), "/proc/%ld/stat", (long)pid) >= (int)sizeof(path)) { errno = EOVERFLOW; return -1; }
    FILE *file = fopen(path, "r");
    if (file == NULL) return errno == ENOENT || errno == ESRCH ? 0 : -1;
    errno = 0;
    size_t length = fread(text, 1, sizeof(text) - 1, file);
    int failed = ferror(file), original_error = errno;
    fclose(file); text[length] = '\0';
    if (failed || length == 0) {
        errno = original_error != 0 ? original_error : EIO;
        return errno == ENOENT || errno == ESRCH ? 0 : -1;
    }
    char *end = strrchr(text, ')'); long ppid, group, sid;
    if (end == NULL || sscanf(end + 2, "%c %ld %ld %ld", state, &ppid, &group, &sid) != 4) { errno = EIO; return -1; }
    *parent = (pid_t)ppid; *session = (pid_t)sid; return 1;
}
static int capture_actor(pid_t pid, pid_t session, int direct_child)
{
    int existing = actor_index(pid);
    if (existing >= 0) return 0;
    if (existing == -2) return -1;
    pid_t parent, sid; char state;
    int found = process_state(pid, &parent, &sid, &state);
    if (found < 0 && errno == EACCES && !direct_child) return 0;
    if (found <= 0) return found;
    int anchor = actor_index(parent);
    if (anchor == -2) return -1;
    if (!direct_child && parent != getpid() && sid != session && anchor < 0) return 0;
    int descriptor = (int)syscall(SYS_pidfd_open, pid, 0);
    if (descriptor < 0) return errno == ESRCH ? 0 : -1;
    found = process_state(pid, &parent, &sid, &state);
    struct pollfd ready = { .fd = descriptor, .events = POLLIN };
    int polled;
    do { polled = poll(&ready, 1, 0); } while (polled < 0 && errno == EINTR);
    int owned = direct_child || parent == getpid() || sid == session;
    if (!owned && anchor >= 0 && parent == actors[anchor].pid) {
        int alive = actor_alive((size_t)anchor);
        if (alive < 0) { close(descriptor); return -1; }
        owned = alive;
    }
    if (found <= 0 || polled != 0 || !owned) {
        int error = errno; close(descriptor); errno = error;
        return found < 0 || polled < 0 ? -1 : 0;
    }
    if (actor_count == actor_capacity) {
        size_t capacity = actor_capacity == 0 ? 8 : actor_capacity * 2;
        struct owned_actor *grown = realloc(actors, capacity * sizeof(*actors));
        if (grown == NULL) { close(descriptor); return -1; }
        actors = grown; actor_capacity = capacity;
    }
    actors[actor_count++] = (struct owned_actor){ .pid = pid, .descriptor = descriptor, .interrupted = -1, .forced = 0 };
    return 0;
}
static int signal_actor(size_t index, int number)
{
    int result;
    do { result = (int)syscall(SYS_pidfd_send_signal, actors[index].descriptor, number, NULL, 0); }
    while (result != 0 && errno == EINTR);
    return result == 0 || errno == ESRCH ? 0 : -1;
}
static int advance_stops(void)
{
    for (size_t index = 0; index < actor_count; index++) {
        int alive = actor_alive(index);
        if (alive < 0) return -1;
        if (!alive) continue;
        if (actors[index].interrupted < 0) {
            if (signal_actor(index, SIGINT) != 0) return -1;
            actors[index].interrupted = milliseconds();
        }
        if (!actors[index].forced && milliseconds() - actors[index].interrupted >= 500) {
            if (signal_actor(index, SIGSTOP) != 0 || signal_actor(index, SIGKILL) != 0) return -1;
            actors[index].forced = 1;
        }
    }
    return 0;
}
#endif

/* The unreaped primary reserves the private group/session identity. Linux
 * additionally owns adopted children as a single-threaded child subreaper;
 * pidfds protect every individual signal, including actors which escape it. */
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
    (void)stopping; (void)force; free(members);
#else
    (void)force;
    if (stopping) {
        DIR *processes = opendir("/proc");
        if (processes == NULL) return -1;
        struct dirent *entry;
        while ((entry = readdir(processes)) != NULL) {
            char *end; long candidate = strtol(entry->d_name, &end, 10);
            if (*entry->d_name == '\0' || *end != '\0' || candidate <= 1 || candidate == getpid()) continue;
            if (capture_actor((pid_t)candidate, group, 0) != 0 || (discovery_started && advance_stops() != 0)) { closedir(processes); return -1; }
        }
        closedir(processes);
        discovery_started = 1;
        if (advance_stops() != 0) return -1;
    }
    char path[128];
    if (snprintf(path, sizeof(path), "/proc/self/task/%ld/children", (long)getpid()) >= (int)sizeof(path)) { errno = EOVERFLOW; return -1; }
    FILE *children = fopen(path, "r");
    if (children == NULL) return -1;
    long candidate; int changed = 0, scanned;
    while ((scanned = fscanf(children, "%ld", &candidate)) == 1) {
        if (candidate == group) continue; // The primary must remain unreaped.
        siginfo_t state = { 0 };
        if (waitid(P_PID, (id_t)candidate, &state, WEXITED | WNOHANG | WNOWAIT) != 0) {
            if (errno == EINTR) { live++; continue; }
            int error = errno; fclose(children); errno = error; return -1;
        }
        if (state.si_pid == candidate) {
            int result;
            do { result = waitpid((pid_t)candidate, NULL, WNOHANG); } while (result < 0 && errno == EINTR);
            if (result != candidate) { int error = errno; fclose(children); errno = error; return -1; }
            changed = 1;
        } else {
            live++;
            if (stopping && capture_actor((pid_t)candidate, group, 1) != 0) { int error = errno; fclose(children); errno = error; return -1; }
        }
    }
    int error = ferror(children) || scanned != EOF; fclose(children);
    if (error) { errno = EIO; return -1; }
    if (stopping && advance_stops() != 0) return -1;
    // With a dead reserved primary and no other direct child, a subreaper has
    // no live descendant frontier. Reaping a bridge requires a fresh snapshot.
    if (live == 0 && changed) live = 1;
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
    int child_session = argc > 5 && strcmp(argv[3], "--child-sets-session") == 0;
    int executable = child_session ? 5 : 4;
    if (argc <= executable || strcmp(argv[executable - 1], "--") != 0) return 2;
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
        if (child_session || setpgid(0, 0) == 0) execv(argv[executable], &argv[executable]);
        int error = errno; ssize_t sent;
        do { sent = write(exec_error[1], &error, sizeof(error)); } while (sent < 0 && errno == EINTR);
        _exit(127);
    }
    close(exec_error[1]);
    if (!child_session && setpgid(child, child) != 0 && errno != EACCES && errno != ESRCH) stop_requested = 1;
    /* Only Git and its descendants retain the actual standard streams. EOF is
     * observable independently of the supervisor's mandatory process drain. */
    close(STDIN_FILENO); close(STDOUT_FILENO); close(STDERR_FILENO);
    if (fcntl(exec_error[0], F_SETFL, O_NONBLOCK) != 0) stop_requested = 1;
    int native_error = 0, exec_done = 0; size_t exec_bytes = 0;
    int exited = 0, failure = 0; int64_t stopping_at = -1;
#ifdef __APPLE__
    int group_interrupted = 0, primary_interrupted = 0;
#else
    if (capture_actor(child, child, 1) != 0) { failure = errno; stop_requested = 1; }
#endif
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
#ifdef __APPLE__
        if (stop_requested) {
            if (force) {
                (void)kill(child, SIGSTOP); (void)kill(child, SIGKILL);
                (void)kill(-child, SIGSTOP); (void)kill(-child, SIGKILL);
            } else if (!group_interrupted) {
                if (kill(-child, SIGINT) == 0) group_interrupted = 1;
                else if (errno == ESRCH && !primary_interrupted) { (void)kill(child, SIGINT); primary_interrupted = 1; }
                else if (errno != ESRCH) failure = errno;
            }
        }
#endif
        siginfo_t state; memset(&state, 0, sizeof(state));
        if (waitid(P_PID, (id_t)child, &state, WEXITED | WNOHANG | WNOWAIT) != 0) {
            if (errno == EINTR) continue;
            failure = errno; stop_requested = 1; break;
        }
        if (!exited && state.si_pid == child) {
            exited = 1;
            int primary_code = state.si_code == CLD_EXITED ? state.si_status : 128 + state.si_status;
            if (record(5, child, (uint32_t)primary_code) != 0) stop_requested = 1;
        }
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
