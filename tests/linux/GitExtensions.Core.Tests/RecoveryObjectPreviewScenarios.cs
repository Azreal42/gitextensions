using System.Collections.Concurrent;
using System.Text;
using GitExtensions.Linux;

internal static class RecoveryObjectPreviewScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("recovery previews read removed blobs and trees freshly while generic previews retain native cache hits", MissingAfterGenericHitAsync)
    ];

    private static async Task MissingAfterGenericHitAsync()
    {
        GitEnvironmentOptions previousOptions = GitEnvironment.Options;
        GitCommandLogCapturePolicy previousCapture = GitCommandLog.CapturePolicy;
        string? previousAskpass = GitRunner.AskpassFallbackPath;
        string[] names = ["GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_NAMESPACE",
            "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "WAYLAND_DISPLAY"];
        Dictionary<string, string?> previousEnvironment = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        GitEnvironment.Configure(new() { Executable = "/usr/bin/git" });
        GitCommandLog.ConfigureCapture(new(false)); GitRunner.AskpassFallbackPath = null;
        foreach (string name in names) { Environment.SetEnvironmentVariable(name, null); }
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        GitCommandCache.Shared.Clear();
        try
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
            GitRepository repository = fixture.Repository;
            await repository.SetConfigAsync("i18n.filesEncoding", "utf-8");
            GitRunner native = new("/usr/bin/git");
            byte[] blobBytes = Encoding.UTF8.GetBytes("recovery preview λ " + Guid.NewGuid().ToString("N") + "\n");
            string blob = await WriteObjectAsync(native, repository.Path, "blob", blobBytes);
            byte[] treeBytes = Encoding.UTF8.GetBytes("100644 blob " + blob + "\trecovery λ.txt\n");
            GitBinaryResult treeResult = await native.RunBinaryAsync(repository.Path, ["mktree"], standardInput: treeBytes);
            Check(treeResult.Success, treeResult.StandardError);
            string tree = Encoding.ASCII.GetString(treeResult.StandardOutput).Trim();
            GitObjectData treeData = await repository.ReadObjectAsync(tree);
            GitRepositoryInfo info = await repository.GetInfoAsync();
            long firstId = GitCommandLog.Snapshot().LastOrDefault()?.Id ?? 0;
            ConcurrentDictionary<long, GitCommandLogEntry> commands = new();
            void ObserveNativeCommands()
            {
                foreach (GitCommandLogEntry entry in GitCommandLog.Snapshot())
                {
                    if (entry.Id > firstId && entry.ProcessId is > 0 && entry.Directory == repository.Path && entry.Command == "git cat-file")
                    { commands[entry.Id] = entry; }
                }
            }
            int Calls() => commands.Count;
            GitCommandLog.Changed += ObserveNativeCommands;
            try
            {
                foreach ((string oid, string type, byte[] bytes) in new[] { (blob, "blob", blobBytes), (tree, "tree", treeData.Bytes) })
                {
                    int previewReads = type == "tree" ? 2 : 1;
                    int calls = Calls();
                    GitObjectPreview first = await repository.GetObjectPreviewAsync(oid);
                    CheckReadable(first, oid, bytes);
                    Check(Calls() == calls + previewReads, "The initial generic " + type + " preview did not execute its actual native reads.");
                    calls = Calls();
                    GitObjectPreview hit = await repository.GetObjectPreviewAsync(oid);
                    CheckReadable(hit, oid, bytes);
                    Check(hit.Text == first.Text && Calls() == calls, "The repeated generic " + type + " preview lost its positive cache hit.");
                    foreach (int iteration in new[] { 0, 1 })
                    {
                        calls = Calls();
                        GitObjectPreview current = await repository.GetRecoveryObjectPreviewAsync(oid);
                        CheckReadable(current, oid, bytes);
                        Check(current.Text == first.Text && Calls() == calls + previewReads,
                            "Recovery " + type + " preview reused cached binary or pretty content on iteration " + iteration + ".");
                    }

                    string loose = Path.Combine(info.CommonDirectory, "objects", oid[..2], oid[2..]);
                    Check(File.Exists(loose), "The regression requires its actual loose " + type + " object before deletion.");
                    File.Delete(loose);
                    Check(!File.Exists(loose), "The loose " + type + " object was not physically removed.");
                    GitBinaryResult missing = await native.RunBinaryAsync(repository.Path, ["--no-replace-objects", "cat-file", "--batch"],
                        standardInput: Encoding.ASCII.GetBytes(oid + "\n"));
                    Check(missing.ExitCode == 0 && Encoding.ASCII.GetString(missing.StandardOutput) == oid + " missing\n",
                        "The independent native witness did not report the removed " + type + " object as missing.");
                    // Do not clear the generic cache between its positive hit, deletion, missing reads and repair.
                    foreach (GitRepository reader in new[] { repository, new GitRepository(repository.Path, new GitRunner("/usr/bin/git")) })
                    {
                        calls = Calls();
                        GitObjectPreview absent = await reader.GetRecoveryObjectPreviewAsync(oid);
                        Check(absent.Hash == oid && absent.ReadError is not null && absent.ReadError.Contains(oid, StringComparison.Ordinal)
                            && absent.Object is null && absent.Text is null && absent.DiffDocument is null,
                            "Recovery returned cached " + type + " content after native Git confirmed its removal.");
                        Check(Calls() == calls + 1, "Missing Recovery " + type + " preview did not execute a fresh native batch read.");
                    }
                    Check(await WriteObjectAsync(native, repository.Path, type, bytes) == oid, "Repair changed the original " + type + " identity.");
                    calls = Calls();
                    GitObjectPreview repaired = await repository.GetRecoveryObjectPreviewAsync(oid);
                    CheckReadable(repaired, oid, bytes);
                    Check(repaired.Text == first.Text && Calls() == calls + previewReads,
                        "Recovery did not read the repaired " + type + " object without clearing the generic cache.");
                }
            }
            finally { GitCommandLog.Changed -= ObserveNativeCommands; }
        }
        finally
        {
            GitCommandCache.Shared.Clear(); GitEnvironment.Configure(previousOptions);
            GitCommandLog.ConfigureCapture(previousCapture); GitRunner.AskpassFallbackPath = previousAskpass;
            foreach ((string name, string? value) in previousEnvironment) { Environment.SetEnvironmentVariable(name, value); }
        }
    }

    private static async Task<string> WriteObjectAsync(GitRunner native, string path, string type, byte[] bytes)
    {
        GitBinaryResult result = await native.RunBinaryAsync(path, ["hash-object", "-t", type, "-w", "--stdin"], standardInput: bytes);
        Check(result.Success, result.StandardError);
        return Encoding.ASCII.GetString(result.StandardOutput).Trim();
    }
    private static void CheckReadable(GitObjectPreview preview, string oid, byte[] bytes)
        => Check(preview.Hash == oid && preview.ReadError is null && preview.Object is not null && preview.Object.Bytes.SequenceEqual(bytes)
            && preview.Text is not null && preview.DiffDocument is null, "The readable preview changed the actual object identity, bytes or presentation.");
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
}
