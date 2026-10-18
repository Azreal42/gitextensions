/* Git Extensions Linux process launcher. SPDX-License-Identifier: GPL-3.0-or-later */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__GLIBC__) || !defined(__LP64__)
#error "The process launcher requires the 64-bit Linux glibc ABI for its declared RID."
#endif

#if defined(GE_ARCH_X64) && !defined(__x86_64__)
#error "The process launcher compiler does not target linux-x64."
#elif defined(GE_ARCH_ARM64) && !defined(__aarch64__)
#error "The process launcher compiler does not target linux-arm64."
#elif !defined(GE_ARCH_X64) && !defined(GE_ARCH_ARM64)
#error "An explicit supported target architecture is required."
#endif

static void uint32_be(unsigned char *target, uint32_t value)
{
    target[0] = (unsigned char)(value >> 24);
    target[1] = (unsigned char)(value >> 16);
    target[2] = (unsigned char)(value >> 8);
    target[3] = (unsigned char)value;
}

static int send_record(int control, const unsigned char *record)
{
    size_t offset = 0;
    while (offset < 16)
    {
        ssize_t sent = send(control, record + offset, 16 - offset, MSG_NOSIGNAL);
        if (sent < 0 && errno == EINTR) { continue; }
        if (sent <= 0) { return -1; }
        offset += (size_t)sent;
    }
    return 0;
}

static _Noreturn void launch_failed(int control, const char *witness, int original_errno, uint32_t stage)
{
    unsigned char record[16] = { 'G', 'E', 'X', '1' };
    uint32_be(record + 4, (uint32_t)getpid());
    uint32_be(record + 8, (uint32_t)original_errno);
    uint32_be(record + 12, stage);
    int file;
    do { file = open(witness, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600); } while (file < 0 && errno == EINTR);
    if (file >= 0)
    {
        (void)fchmod(file, 0600);
        size_t offset = 0;
        while (offset < sizeof(record))
        {
            ssize_t written = write(file, record + offset, sizeof(record) - offset);
            if (written < 0 && errno == EINTR) { continue; }
            if (written <= 0) { break; }
            offset += (size_t)written;
        }
        (void)close(file);
    }
    /* Authenticated control remains authoritative even if the filesystem cannot create the supplemental witness. */
    (void)send_record(control, record);
    (void)close(control);
    /* The managed parent reads the private errno record, never this exit status or Git's stderr. */
    _exit(127);
}

static int ge_group_member(gid_t group)
{
    if (getegid() == group) { return 1; }
    int count = getgroups(0, NULL);
    if (count < 0) { return -1; }
    if (count == 0) { return 0; }
    gid_t *groups = malloc((size_t)count * sizeof(gid_t));
    if (groups == NULL) { return -1; }
    int received = getgroups(count, groups);
    if (received < 0) { int error = errno; free(groups); errno = error; return -1; }
    int found = 0;
    for (int index = 0; index < received; index++) { if (groups[index] == group) { found = 1; break; } }
    free(groups);
    return found;
}

/* Process.Unix.IsExecutable uses mode/identity, not access(X_OK). ACLs are subsequently enforced by the real exec. */
static int executable_candidate(const char *path)
{
    struct stat status;
    if (stat(path, &status) != 0 || S_ISDIR(status.st_mode)) { return 0; }
    mode_t permissions = status.st_mode & (S_IXUSR | S_IXGRP | S_IXOTH);
    if (permissions == (S_IXUSR | S_IXGRP | S_IXOTH)) { return 1; }
    if (permissions == 0) { return 0; }
    uid_t user = geteuid();
    if (user == 0) { return 1; }
    if (user == status.st_uid) { return (permissions & S_IXUSR) != 0; }
    int group = (permissions & S_IXGRP) != 0, other = (permissions & S_IXOTH) != 0;
    if (group == other) { return group; }
    int member = ge_group_member(status.st_gid);
    return member < 0 ? -1 : member ? group : other;
}

static char *combine(const char *directory, const char *name)
{
    char *result = NULL;
    size_t length = strlen(directory);
    const char *separator = length == 0 || directory[length - 1] == '/' ? "" : "/";
    if (asprintf(&result, "%s%s%s", directory, separator, name) < 0) { return NULL; }
    return result;
}

static char *resolve_path(const char *parent_directory, const char *parent_path, const char *program)
{
    char *path = strdup(parent_path);
    if (path == NULL) { return NULL; }
    char *save = NULL;
    for (char *directory = strtok_r(path, ":", &save); directory != NULL; directory = strtok_r(NULL, ":", &save))
    {
        char *candidate = combine(directory, program);
        if (candidate == NULL) { int error = errno; free(path); errno = error; return NULL; }
        char *parent_candidate = candidate[0] == '/' ? strdup(candidate) : combine(parent_directory, candidate);
        if (parent_candidate == NULL) { int error = errno; free(candidate); free(path); errno = error; return NULL; }
        int selected = executable_candidate(parent_candidate), error = errno;
        free(parent_candidate);
        if (selected < 0) { free(candidate); free(path); errno = error; return NULL; }
        if (selected)
        {
            free(path);
            /* Keep the selected pathname exactly: execv interprets a relative PATH candidate after child chdir.
               Absolutizing it would also change the kernel's shebang argument and the script's real $0. */
            return candidate;
        }
        free(candidate);
    }
    free(path); errno = ENOENT; return NULL;
}

int main(int argc, char **argv)
{
    /* control, witness, direct absolute target (or empty), parent cwd/PATH, original argv0, literal arguments */
    if (argc < 7 || (argv[3][0] != '\0' && argv[3][0] != '/') || argv[4][0] != '/') { _exit(125); }
    int control = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (control < 0) { _exit(125); }
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    if (strlen(argv[1]) >= sizeof(address.sun_path)) { _exit(125); }
    memcpy(address.sun_path, argv[1], strlen(argv[1]) + 1);
    int connected;
    do { connected = connect(control, (const struct sockaddr *)&address, sizeof(address)); } while (connected < 0 && errno == EINTR);
    if (connected < 0) { _exit(125); }
    unsigned char hello[16] = { 'G', 'E', 'X', '1' };
    uint32_be(hello + 4, (uint32_t)getpid());
    if (send_record(control, hello) < 0) { _exit(125); }
    unsigned char acknowledgement;
    ssize_t received;
    do { received = recv(control, &acknowledgement, 1, 0); } while (received < 0 && errno == EINTR);
    if (received != 1 || acknowledgement != 0xA5) { _exit(125); }
    if (setsid() < 0) { int error = errno; launch_failed(control, argv[2], error, 1); }
    char *target = argv[3][0] != '\0' ? argv[3] : resolve_path(argv[4], argv[5], argv[6]);
    if (target == NULL) { int error = errno; launch_failed(control, argv[2], error, 2); }
    /* Git itself can prepend its exec-path to PATH. Native editor/index-filter children must
       retain this actual selection while argv0 and the kernel's shebang semantics stay intact. */
    const char *git = getenv("GITEXTENSIONS_GIT_EXECUTABLE");
    if (git != NULL && strcmp(git, argv[6]) == 0)
    {
        char *directory = target[0] == '/' ? NULL : getcwd(NULL, 0);
        char *selected = target[0] == '/' ? strdup(target) : directory == NULL ? NULL : combine(directory, target);
        free(directory);
        if (selected == NULL || setenv("GITEXTENSIONS_GIT_EXECUTABLE", selected, 1) < 0)
        { int error = errno; free(selected); launch_failed(control, argv[2], error, 2); }
        free(selected);
    }
    execv(target, argv + 6);
    int error = errno;
    launch_failed(control, argv[2], error, 2);
}
