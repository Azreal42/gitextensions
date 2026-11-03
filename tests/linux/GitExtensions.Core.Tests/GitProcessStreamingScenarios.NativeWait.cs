using System.Globalization;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    private sealed record NativeWaitWitness(string Library, string Report)
    {
        internal void Apply(Dictionary<string, string?> environment)
        {
            environment["LD_PRELOAD"] = Library;
            environment["GITEXTENSIONS_PRIVATE_WAIT_REPORT"] = Report;
            environment["GITEXTENSIONS_PRIVATE_WAIT_PARENT"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        }
        internal int StatusFor(int owner, int child)
        {
            int[][] records = File.ReadAllLines(Report).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray())
                .Where(values => values.Length == 3 && values[0] == owner && values[1] == child).ToArray();
            Check(records.Length == 1, "The independent native interceptor must observe one actual kernel wait by this supervisor for this exact child.");
            return records[0][2];
        }
    }

    private static async Task<NativeWaitWitness> CreateNativeWaitWitnessAsync(RepositoryFixture fixture)
    {
        string source = Path.Combine(fixture.Root, "native-wait-witness.c"), library = Path.Combine(fixture.Root, "native-wait-witness.so");
        File.WriteAllText(source, """
            #define _GNU_SOURCE
            #include <dlfcn.h>
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <sys/types.h>
            #include <sys/wait.h>
            #include <unistd.h>
            pid_t waitpid(pid_t pid, int *status, int options)
            {
                pid_t (*next)(pid_t,int *,int) = (pid_t (*)(pid_t,int *,int))dlsym(RTLD_NEXT, "waitpid");
                if (next == NULL) { errno = ENOSYS; return -1; }
                int actual = 0;
                pid_t result = next(pid, &actual, options);
                int original_error = errno;
                if (status != NULL && result > 0) *status = actual;
                const char *report = getenv("GITEXTENSIONS_PRIVATE_WAIT_REPORT");
                const char *parent = getenv("GITEXTENSIONS_PRIVATE_WAIT_PARENT");
                if (result > 0 && report != NULL && parent != NULL && (pid_t)strtol(parent, NULL, 10) == getppid()) {
                    int file = open(report, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0600);
                    if (file >= 0) {
                        char record[128]; int length = snprintf(record, sizeof(record), "%ld %ld %d\n", (long)getpid(), (long)result, actual);
                        if (length > 0 && length < (int)sizeof(record)) {
                            ssize_t written;
                            do { written = write(file, record, (size_t)length); } while (written < 0 && errno == EINTR);
                        }
                        close(file);
                    }
                }
                errno = original_error;
                return result;
            }
            """ + "\n");
        GitResult compiled = await DirectLaunch("/usr/bin/cc", fixture.Root,
            ["-std=c11", "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", source, "-ldl", "-o", library]);
        Check(compiled.Success, "The independent native wait interceptor did not compile: " + compiled.StandardError);
        return new(library, Path.Combine(fixture.Root, "native-wait-witness.log"));
    }
}
