#define _DARWIN_C_SOURCE
#ifndef __APPLE__
#define _GNU_SOURCE
#endif
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdint.h>
#include <time.h>
#include <sys/wait.h>
#include <unistd.h>
#ifdef __APPLE__
#include <errno.h>
#include <libproc.h>
#include <sys/proc.h>
#include <sys/event.h>
#else
#include <poll.h>
#include <sys/syscall.h>
#endif

/* Fixed ABI for managed callers; Darwin arm64 fcntl is variadic. */
__attribute__((visibility("default")))
int ge_duplicate_cloexec(int descriptor, int minimum)
{
    return fcntl(descriptor, F_DUPFD_CLOEXEC, minimum);
}

/* Keep opaque Darwin and concrete glibc spawn structs in native code. Only
 * these five explicitly mapped descriptors cross the process boundary. */
__attribute__((visibility("default")))
int ge_spawn_supervisor(const char *executable, const char *directory,
                       char *const arguments[], char *const environment[],
                       const int descriptors[5], int *process_id)
{
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
    int error = posix_spawn_file_actions_init(&actions);
    if (error != 0) return error;
    error = posix_spawnattr_init(&attributes);
    if (error != 0) { posix_spawn_file_actions_destroy(&actions); return error; }
    short flags = POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGMASK;
#ifdef __APPLE__
    flags |= POSIX_SPAWN_CLOEXEC_DEFAULT;
#endif
    sigset_t empty;
    sigemptyset(&empty);
    error = posix_spawnattr_setsigmask(&attributes, &empty);
    if (error == 0) error = posix_spawnattr_setflags(&attributes, flags);
    for (int index = 0; index < 5 && error == 0; index++) {
        if (descriptors[index] < 5) { error = EINVAL; break; }
        error = posix_spawn_file_actions_adddup2(&actions, descriptors[index], index);
    }
    for (int index = 0; index < 5 && error == 0; index++)
        error = posix_spawn_file_actions_addclose(&actions, descriptors[index]);
#ifdef __APPLE__
    /* Retain the macOS 15 entry point when compiling with a newer SDK. */
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
#endif
    if (error == 0) error = posix_spawn_file_actions_addchdir_np(&actions, directory);
#ifdef __APPLE__
#pragma clang diagnostic pop
#endif
#ifndef __APPLE__
    if (error == 0) error = posix_spawn_file_actions_addclosefrom_np(&actions, 5);
#endif
    if (error == 0) error = posix_spawn(process_id, executable, &actions, &attributes, arguments, environment);
    posix_spawnattr_destroy(&attributes);
    posix_spawn_file_actions_destroy(&actions);
    return error;
}

/* Observation deliberately leaves the unmanaged direct child unreaped. Its
 * PID remains reserved through pre-ACK cancellation and final I/O cleanup. */
__attribute__((visibility("default")))
int ge_observe_child(int process_id, int *code)
{
    siginfo_t state = { 0 };
    if (waitid(P_PID, (id_t)process_id, &state, WEXITED | WNOHANG | WNOWAIT) != 0) return -1;
    if (state.si_pid == 0) return 0;
    *code = state.si_code == CLD_EXITED ? state.si_status : 128 + state.si_status;
    return 1;
}

__attribute__((visibility("default")))
int ge_reap_child(int process_id)
{
    pid_t result;
    do { result = waitpid(process_id, NULL, 0); } while (result < 0 && errno == EINTR);
    return result == process_id ? 0 : -1;
}

/* The managed owner calls this only before ACK, with its unreaped own PID.
 * No Git code can have run and no unrelated recycled PID can be signaled. */
__attribute__((visibility("default")))
int ge_kill_reserved_child(int process_id)
{
    int result;
    do { result = kill(process_id, SIGKILL); } while (result != 0 && errno == EINTR);
    return result == 0 || errno == ESRCH ? 0 : -1;
}

/* Stable native observations used by the process contract tests. An exit
 * event is independent of whether a supervisor has reaped its zombie yet. */
__attribute__((visibility("default")))
int ge_open_process_reference(int process_id)
{
#ifdef __APPLE__
    int descriptor = kqueue();
    if (descriptor < 0) return -1;
    struct kevent event;
    EV_SET(&event, (uintptr_t)process_id, EVFILT_PROC, EV_ADD | EV_ENABLE | EV_CLEAR, NOTE_EXIT, 0, NULL);
    if (fcntl(descriptor, F_SETFD, FD_CLOEXEC) != 0 || kevent(descriptor, &event, 1, NULL, 0, NULL) != 0) {
        int error = errno; close(descriptor); errno = error; return -1;
    }
    return descriptor;
#else
    return (int)syscall(SYS_pidfd_open, process_id, 0);
#endif
}

__attribute__((visibility("default")))
int ge_process_reference_is_running(int descriptor)
{
#ifdef __APPLE__
    struct kevent event;
    struct timespec immediate = { 0 };
    int result = kevent(descriptor, NULL, 0, &event, 1, &immediate);
    if (result < 0) return -1;
    if (result == 0) return 1;
    if (event.filter == EVFILT_PROC && (event.fflags & NOTE_EXIT)) return 0;
    errno = EIO; return -1;
#else
    struct pollfd event = { .fd = descriptor, .events = POLLIN };
    int result = poll(&event, 1, 0);
    if (result < 0) return -1;
    if (event.revents & (POLLIN | POLLHUP)) return 0;
    if (event.revents == 0) return 1;
    errno = EIO; return -1;
#endif
}

__attribute__((visibility("default")))
int ge_stop_process_reference(int descriptor)
{
#ifdef __APPLE__
    (void)descriptor; errno = ENOTSUP; return -1;
#else
    int result;
    do { result = (int)syscall(SYS_pidfd_send_signal, descriptor, SIGKILL, NULL, 0); } while (result != 0 && errno == EINTR);
    return result == 0 || errno == ESRCH ? 0 : -1;
#endif
}
