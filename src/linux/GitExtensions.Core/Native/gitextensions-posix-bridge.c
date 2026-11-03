#define _DARWIN_C_SOURCE
#include <fcntl.h>
#ifdef __APPLE__
#include <errno.h>
#include <libproc.h>
#include <sys/proc.h>
#endif

/* Fixed ABI for managed callers; Darwin arm64 fcntl is variadic. */
__attribute__((visibility("default")))
int ge_duplicate_cloexec(int descriptor, int minimum)
{
    return fcntl(descriptor, F_DUPFD_CLOEXEC, minimum);
}

#ifdef __APPLE__
/* Read-only observation for native process contracts. A reserved zombie has
 * stopped running even though its supervisor intentionally has not reaped it. */
__attribute__((visibility("default")))
int ge_process_is_running(int pid)
{
    struct proc_bsdinfo info;
    errno = 0;
    int bytes = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info));
    if (bytes == 0 && (errno == ESRCH || errno == ENOENT)) return 0;
    if (bytes != (int)sizeof(info)) { if (errno == 0) errno = EIO; return -1; }
    return info.pbi_status != SZOMB;
}
#endif
