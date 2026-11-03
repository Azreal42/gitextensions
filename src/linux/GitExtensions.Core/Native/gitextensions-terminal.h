/* The supervisor owns the PTY and two bounded relay queues. No process or
 * shell outside the reserved lifetime is needed to allocate the terminal. */
#include <sys/ioctl.h>
#include <termios.h>
#ifdef __APPLE__
#include <util.h>
#else
#include <pty.h>
#include <utmp.h>
#endif

#define TERMINAL_BUFFER_SIZE 65536
struct terminal_buffer { unsigned char bytes[TERMINAL_BUFFER_SIZE]; size_t offset, count; };
struct terminal_stream { int descriptor, owned, original_flags; };
struct terminal_io {
    int master, slave, input_eof, master_eof, sent_eof, raw;
    struct terminal_stream input, output;
    struct termios saved;
    struct winsize size;
    struct terminal_buffer incoming, outgoing;
};
static void terminal_initialize(struct terminal_io *io)
{
    memset(io, 0, sizeof(*io));
    io->master = io->slave = io->input.descriptor = io->output.descriptor = -1;
}
static int terminal_stream_open(struct terminal_stream *stream, int descriptor, int direction)
{
    if (isatty(descriptor)) {
        char path[4096];
        int error = ttyname_r(descriptor, path, sizeof(path));
        if (error != 0) { errno = error; return -1; }
        /* A new open description avoids changing the caller's O_NONBLOCK. */
        stream->descriptor = open(path, direction | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
        if (stream->descriptor < 0) return -1;
        stream->owned = 1;
    } else {
        /* Stream mode receives only the factory's private pipe ends. */
        stream->descriptor = descriptor;
        stream->original_flags = fcntl(descriptor, F_GETFL);
        if (stream->original_flags < 0 || fcntl(descriptor, F_SETFL, stream->original_flags | O_NONBLOCK) != 0) return -1;
    }
    return 0;
}
static int terminal_prepare(struct terminal_io *io)
{
    io->size.ws_row = 24; io->size.ws_col = 80;
    int tty = isatty(STDIN_FILENO);
    if (tty && (tcgetattr(STDIN_FILENO, &io->saved) != 0 || ioctl(STDIN_FILENO, TIOCGWINSZ, &io->size) != 0)) return -1;
    if (openpty(&io->master, &io->slave, NULL, tty ? &io->saved : NULL, &io->size) != 0) return -1;
    if (fcntl(io->master, F_SETFD, FD_CLOEXEC) != 0 || fcntl(io->slave, F_SETFD, FD_CLOEXEC) != 0
        || fcntl(io->master, F_SETFL, O_NONBLOCK) != 0) return -1;
    if (terminal_stream_open(&io->input, STDIN_FILENO, O_RDONLY) != 0
        || terminal_stream_open(&io->output, STDOUT_FILENO, O_WRONLY) != 0) return -1;
    if (tty) {
        struct termios raw = io->saved; cfmakeraw(&raw);
        if (tcsetattr(STDIN_FILENO, TCSANOW, &raw) != 0) return -1;
        io->raw = 1;
    }
    return 0;
}
static void terminal_close(struct terminal_io *io)
{
    int original = errno;
    if (io->raw) {
        int restored;
        do { restored = tcsetattr(STDIN_FILENO, TCSANOW, &io->saved); } while (restored != 0 && errno == EINTR);
    }
    struct terminal_stream *streams[2] = { &io->input, &io->output };
    for (int index = 0; index < 2; index++) {
        struct terminal_stream *stream = streams[index];
        if (stream->descriptor < 0) continue;
        if (stream->owned) close(stream->descriptor);
        else (void)fcntl(stream->descriptor, F_SETFL, stream->original_flags);
    }
    if (io->master >= 0) close(io->master);
    if (io->slave >= 0) close(io->slave);
    errno = original;
}
static size_t terminal_space(struct terminal_buffer *buffer)
{
    if (buffer->offset != 0 && buffer->offset + buffer->count == TERMINAL_BUFFER_SIZE) {
        memmove(buffer->bytes, buffer->bytes + buffer->offset, buffer->count); buffer->offset = 0;
    }
    return TERMINAL_BUFFER_SIZE - buffer->offset - buffer->count;
}
static int terminal_read(int descriptor, struct terminal_buffer *buffer, int master, int *eof)
{
    size_t space = terminal_space(buffer);
    if (space == 0) return 0;
    ssize_t count = read(descriptor, buffer->bytes + buffer->offset + buffer->count, space);
    if (count > 0) { buffer->count += (size_t)count; return 0; }
    /* Linux reports EIO after the final PTY slave closes; Darwin reports EOF. */
    if (count == 0 || (count < 0 && master && errno == EIO)) { *eof = 1; return 0; }
    return errno == EINTR || errno == EAGAIN ? 0 : -1;
}
static int terminal_write(int descriptor, struct terminal_buffer *buffer)
{
    if (buffer->count == 0) return 0;
    ssize_t count = write(descriptor, buffer->bytes + buffer->offset, buffer->count);
    if (count > 0) {
        buffer->offset += (size_t)count; buffer->count -= (size_t)count;
        if (buffer->count == 0) buffer->offset = 0;
        return 0;
    }
    if (count == 0) { errno = EIO; return -1; }
    return count < 0 && (errno == EINTR || errno == EAGAIN) ? 0 : -1;
}
static void terminal_poll(struct terminal_io *io, struct pollfd *descriptors, int stopping)
{
    short events = (terminal_space(&io->outgoing) != 0 || stopping ? POLLIN : 0)
        | (io->incoming.count != 0 && !stopping ? POLLOUT : 0);
    descriptors[0] = (struct pollfd){ .fd = io->master_eof || events == 0 ? -1 : io->master, .events = events };
    descriptors[1] = (struct pollfd){ .fd = io->input_eof || stopping || io->master_eof || terminal_space(&io->incoming) == 0 ? -1 : io->input.descriptor,
        .events = POLLIN };
    descriptors[2] = (struct pollfd){ .fd = io->outgoing.count == 0 || stopping ? -1 : io->output.descriptor, .events = POLLOUT };
}
static int terminal_cycle(struct terminal_io *io, struct pollfd *descriptors, int stopping)
{
    if (stopping) { io->incoming.offset = io->incoming.count = io->outgoing.offset = io->outgoing.count = 0; }
    if (descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) {
        if (terminal_read(io->master, &io->outgoing, 1, &io->master_eof) != 0) return -1;
        if (stopping) io->outgoing.offset = io->outgoing.count = 0;
    }
    if (!stopping) {
        if (descriptors[1].revents & (POLLIN | POLLHUP | POLLERR)) {
            if (terminal_read(io->input.descriptor, &io->incoming, 0, &io->input_eof) != 0) return -1;
        }
        if (io->input_eof && !io->sent_eof && terminal_space(&io->incoming) != 0) {
            io->incoming.bytes[io->incoming.offset + io->incoming.count++] = io->raw ? io->saved.c_cc[VEOF] : 4;
            io->sent_eof = 1;
        }
        if (!io->master_eof && (descriptors[0].revents & POLLOUT) && terminal_write(io->master, &io->incoming) != 0) return -1;
        if ((descriptors[2].revents & (POLLOUT | POLLHUP | POLLERR)) && terminal_write(io->output.descriptor, &io->outgoing) != 0) return -1;
    }
    if (io->raw) {
        struct winsize size;
        if (ioctl(STDIN_FILENO, TIOCGWINSZ, &size) != 0) return -1;
        if (memcmp(&size, &io->size, sizeof(size)) != 0) {
            if (ioctl(io->master, TIOCSWINSZ, &size) != 0) return -1;
            io->size = size;
        }
    }
    return 0;
}

/* PATH belongs to the captured native environment. execv never falls back to
 * interpreting a file as a shell program, including an ENOEXEC candidate. */
static void terminal_exec(char *const arguments[])
{
    if (strchr(arguments[0], '/') != NULL) { execv(arguments[0], arguments); return; }
    const char *path = getenv("PATH");
    if (path == NULL) path = "/usr/bin:/bin";
    int denied = 0;
    while (1) {
        const char *end = strchr(path, ':'); size_t length = end == NULL ? strlen(path) : (size_t)(end - path);
        size_t size = length + strlen(arguments[0]) + 2;
        char *candidate = malloc(size);
        if (candidate == NULL) { errno = ENOMEM; return; }
        if (length == 0) (void)snprintf(candidate, size, "%s", arguments[0]);
        else (void)snprintf(candidate, size, "%.*s/%s", (int)length, path, arguments[0]);
        execv(candidate, arguments); int error = errno; free(candidate);
        if (error == EACCES) denied = 1;
        else if (error != ENOENT && error != ENOTDIR) { errno = error; return; }
        if (end == NULL) { errno = denied ? EACCES : ENOENT; return; }
        path = end + 1;
    }
}
