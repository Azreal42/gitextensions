#define _DARWIN_C_SOURCE
#ifndef __APPLE__
#define _GNU_SOURCE
#endif
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdint.h>
#include <string.h>
#include <time.h>
#include <sys/socket.h>
#include <sys/un.h>
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
int ge_wait_child(int process_id, int *code)
{
    siginfo_t state = { 0 };
    int result;
    do { result = waitid(P_PID, (id_t)process_id, &state, WEXITED | WNOWAIT); } while (result != 0 && errno == EINTR);
    if (result != 0) return -1;
    if (state.si_pid != process_id) { errno = EIO; return -1; }
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

/* Authenticate with native socket credentials, never a PID supplied in JSON. */
__attribute__((visibility("default")))
int ge_socket_peer_process(int descriptor)
{
#ifdef __APPLE__
    uid_t user; gid_t group; pid_t process;
    socklen_t size = sizeof(process);
    if (getpeereid(descriptor, &user, &group) != 0
        || getsockopt(descriptor, SOL_LOCAL, LOCAL_PEERPID, &process, &size) != 0) return -1;
    if (size != sizeof(process) || user != geteuid() || process <= 1) { errno = EACCES; return -1; }
    return process;
#else
    struct ucred peer;
    socklen_t size = sizeof(peer);
    if (getsockopt(descriptor, SOL_SOCKET, SO_PEERCRED, &peer, &size) != 0) return -1;
    if (size != sizeof(peer) || peer.uid != geteuid() || peer.pid <= 1) { errno = EACCES; return -1; }
    return peer.pid;
#endif
}

/* Linux can transfer its pidfd directly. Darwin confines kqueues to their
 * creator, so the receiver registers its own watch while this helper stays
 * connected and awaits ACK, before any Git process can be created. */
__attribute__((visibility("default")))
int ge_send_current_process_reference(int socket_descriptor)
{
#ifdef __APPLE__
    int no_sigpipe = 1;
    if (setsockopt(socket_descriptor, SOL_SOCKET, SO_NOSIGPIPE, &no_sigpipe, sizeof(no_sigpipe)) != 0) return -1;
    unsigned char marker = 'K';
    struct iovec data = { .iov_base = &marker, .iov_len = 1 };
    struct msghdr message = { .msg_iov = &data, .msg_iovlen = 1 };
#else
    int reference = ge_open_process_reference(getpid());
    if (reference < 0) return -1;
    unsigned char marker = 'P';
    struct iovec data = { .iov_base = &marker, .iov_len = 1 };
    union { struct cmsghdr alignment; unsigned char bytes[CMSG_SPACE(sizeof(int))]; } control;
    memset(&control, 0, sizeof(control));
    struct msghdr message = { .msg_iov = &data, .msg_iovlen = 1,
        .msg_control = control.bytes, .msg_controllen = sizeof(control.bytes) };
    struct cmsghdr *header = CMSG_FIRSTHDR(&message);
    header->cmsg_level = SOL_SOCKET; header->cmsg_type = SCM_RIGHTS; header->cmsg_len = CMSG_LEN(sizeof(reference));
    memcpy(CMSG_DATA(header), &reference, sizeof(reference));
#endif
    int flags = MSG_DONTWAIT;
#ifdef MSG_NOSIGNAL
    flags |= MSG_NOSIGNAL;
#endif
    ssize_t count;
    do { count = sendmsg(socket_descriptor, &message, flags); } while (count < 0 && errno == EINTR);
    int error = errno;
#ifndef __APPLE__
    close(reference);
#endif
    errno = count == 0 ? EPIPE : error;
    return count == 1 ? 0 : -1;
}

__attribute__((visibility("default")))
int ge_receive_process_reference(int socket_descriptor, int *process_id)
{
    int peer = ge_socket_peer_process(socket_descriptor);
    if (peer < 0) return -1;
    unsigned char marker = 0;
    struct iovec data = { .iov_base = &marker, .iov_len = 1 };
    union { struct cmsghdr alignment; unsigned char bytes[CMSG_SPACE(4 * sizeof(int))]; } control;
    memset(&control, 0, sizeof(control));
    struct msghdr message = { .msg_iov = &data, .msg_iovlen = 1,
        .msg_control = control.bytes, .msg_controllen = sizeof(control.bytes) };
    int flags = MSG_DONTWAIT;
#ifdef MSG_CMSG_CLOEXEC
    flags |= MSG_CMSG_CLOEXEC;
#endif
    ssize_t count;
    do { count = recvmsg(socket_descriptor, &message, flags); } while (count < 0 && errno == EINTR);
    if (count <= 0) { if (count == 0) errno = EPIPE; return -1; }
    int reference = -1, descriptors = 0;
    int invalid = count != 1 || (message.msg_flags & (MSG_CTRUNC | MSG_TRUNC));
    for (struct cmsghdr *header = CMSG_FIRSTHDR(&message); header != NULL; header = CMSG_NXTHDR(&message, header)) {
        if (header->cmsg_level != SOL_SOCKET || header->cmsg_type != SCM_RIGHTS || header->cmsg_len < CMSG_LEN(sizeof(int))) {
            invalid = 1; continue;
        }
        size_t size = header->cmsg_len - CMSG_LEN(0);
        if (size % sizeof(int) != 0) invalid = 1;
        for (size_t offset = 0; offset + sizeof(int) <= size; offset += sizeof(int)) {
            int descriptor;
            memcpy(&descriptor, CMSG_DATA(header) + offset, sizeof(descriptor));
            if (descriptors++ == 0) reference = descriptor;
            else close(descriptor);
        }
    }
#ifdef __APPLE__
    if (invalid || descriptors != 0 || marker != 'K') {
        if (reference >= 0) close(reference);
        errno = EPROTO; return -1;
    }
    reference = ge_open_process_reference(peer);
    if (reference < 0) return -1;
    /* LOCAL_PEERPID requires a still-connected peer. Recheck after EV_ADD so
     * an exit during registration cannot attach our lease to a recycled PID.
     * The helper retains this CLOEXEC control socket until the final result. */
    int live_peer = ge_socket_peer_process(socket_descriptor);
    if (live_peer != peer) {
        int error = live_peer < 0 ? errno : EPROTO; close(reference); errno = error; return -1;
    }
#else
    if (invalid || descriptors != 1 || marker != 'P') {
        if (reference >= 0) close(reference);
        errno = EPROTO; return -1;
    }
#endif
    if (fcntl(reference, F_SETFD, FD_CLOEXEC) != 0) {
        int error = errno; close(reference); errno = error; return -1;
    }
    *process_id = peer;
    return reference;
}

__attribute__((visibility("default")))
int ge_wait_process_reference(int descriptor)
{
#ifdef __APPLE__
    struct kevent event;
    int result;
    do { result = kevent(descriptor, NULL, 0, &event, 1, NULL); } while (result < 0 && errno == EINTR);
    if (result == 1 && event.filter == EVFILT_PROC && (event.fflags & NOTE_EXIT)) return 0;
#else
    struct pollfd event = { .fd = descriptor, .events = POLLIN };
    int result;
    do { result = poll(&event, 1, -1); } while (result < 0 && errno == EINTR);
    if (result == 1 && (event.revents & (POLLIN | POLLHUP))) return 0;
#endif
    if (result >= 0) errno = EIO;
    return -1;
}
