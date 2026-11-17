#define _DARWIN_C_SOURCE
#include <errno.h>
#include <poll.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/types.h>
#include <unistd.h>

struct fixture_state { atomic_int forked; atomic_int parent_polling; };
static struct fixture_state *state;
static pid_t owner;

__attribute__((constructor))
static void initialize(void)
{
    if (getenv("GITEXTENSIONS_GROUP_FIXTURE_READY") == NULL) return;
    state = mmap(NULL, sizeof(*state), PROT_READ | PROT_WRITE, MAP_ANON | MAP_SHARED, -1, 0);
    if (state == MAP_FAILED) _exit(125);
    atomic_init(&state->forked, 0);
    atomic_init(&state->parent_polling, 0);
    owner = getpid();
}

static pid_t fixture_fork(void)
{
    pid_t child = fork();
    if (state != NULL && child > 0 && getpid() == owner) atomic_store(&state->forked, 1);
    return child;
}

static int fixture_poll(struct pollfd *descriptors, nfds_t count, int timeout)
{
    if (state != NULL && getpid() == owner && atomic_load(&state->forked)) {
        /* The parent's post-fork setup is complete. Release the child's group
         * call only now, without depending on scheduler timing or a sleep. */
        atomic_store(&state->parent_polling, 1);
    }
    return poll(descriptors, count, timeout);
}

static int fixture_setpgid(pid_t process, pid_t group)
{
    if (state != NULL && getpid() != owner && process == 0 && group == 0) {
        while (!atomic_load(&state->parent_polling)) usleep(1000);
        const char *ready = getenv("GITEXTENSIONS_GROUP_FIXTURE_READY");
        const char *temporary = getenv("GITEXTENSIONS_GROUP_FIXTURE_TEMPORARY");
        FILE *output = fopen(temporary, "w");
        if (output == NULL) _exit(125);
        fprintf(output, "%ld %ld", (long)getpid(), (long)getpgrp());
        if (fclose(output) != 0 || rename(temporary, ready) != 0) _exit(125);
        const char *release = getenv("GITEXTENSIONS_GROUP_FIXTURE_RELEASE");
        if (release != NULL) while (access(release, F_OK) != 0) usleep(1000);
        /* Inject the observed EPERM on a redundant child group call, making
         * the competing-creator path deterministic. Initial creation succeeds. */
        if (getpgrp() == getpid()) { errno = EPERM; return -1; }
        /* The fixture instruments the supervisor, not the payload. In
         * particular, platform executables may reject injected libraries. */
        unsetenv("DYLD_INSERT_LIBRARIES");
    }
    return setpgid(process, group);
}

#define INTERPOSE(replacement, original) \
    __attribute__((used)) static const struct { const void *new_function; const void *old_function; } \
    interpose_##original __attribute__((section("__DATA,__interpose"))) = { (const void *)&replacement, (const void *)&original }

INTERPOSE(fixture_fork, fork);
INTERPOSE(fixture_poll, poll);
INTERPOSE(fixture_setpgid, setpgid);
