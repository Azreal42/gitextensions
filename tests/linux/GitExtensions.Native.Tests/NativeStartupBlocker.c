#define _DARWIN_C_SOURCE
#include <fcntl.h>
#include <libproc.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

/* Loaded into the actual supervisor before main/HELLO. The marker is closed
 * and atomically published before the private release-file wait. */
__attribute__((constructor)) static void before_main(void)
{
    const char *marker = getenv("GITEXTENSIONS_NATIVE_STARTUP_MARKER");
    const char *temporary = getenv("GITEXTENSIONS_NATIVE_STARTUP_TEMPORARY");
    const char *release = getenv("GITEXTENSIONS_NATIVE_STARTUP_RELEASE");
    if (marker == NULL || temporary == NULL || release == NULL) return;
    char executable[4096];
    if (proc_pidpath(getpid(), executable, sizeof(executable)) <= 0) _exit(111);
    int file = open(temporary, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    if (file < 0) return;
    if (dprintf(file, "%ld\n%ld\n%s\n", (long)getpid(), (long)getppid(), executable) < 0
        || close(file) != 0 || rename(temporary, marker) != 0) _exit(112);
    struct timespec delay = { .tv_sec = 0, .tv_nsec = 10000000 };
    while (access(release, F_OK) != 0) (void)nanosleep(&delay, NULL);
}
