using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private readonly SemaphoreSlim _commandCacheAuthorityGate = new(1, 1);
    private readonly Dictionary<string, CommandCacheAuthority> _commandCacheAuthorities = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding CacheUtf8 = new(false, true);
    private sealed record CommandCacheAuthority(string EnvironmentIdentity, string Fingerprint, string NativeIdentity, string GitExecutablePath,
        IReadOnlyList<string> Files, IReadOnlyList<string> RecursiveDirectories, string? AttributesRoot,
        bool DifftasticVerified, bool DiffDependenciesVerified, bool BlameDependenciesVerified);

    private async Task<CommandCacheAuthority?> GetCommandCacheAuthorityAsync(GitExecutionEnvironment execution,
        IReadOnlyDictionary<string, string?>? overrides, GitCachedReadKind kind, IReadOnlyList<string> inputFiles, CancellationToken token)
    {
        Dictionary<string, string?> effective = new(execution.EnvironmentVariables, StringComparer.Ordinal);
        if (overrides is not null)
        { foreach ((string name, string? value) in overrides) { if (value is null) { effective.Remove(name); } else { effective[name] = value; } } }
        bool attributesRequired = kind is GitCachedReadKind.Diff or GitCachedReadKind.RangeDiff or GitCachedReadKind.Grep
            or GitCachedReadKind.Blame or GitCachedReadKind.Difftastic;
        // Raw object/tree/metadata/graph readers do not inspect the worktree's attribute namespace.
        string environmentIdentity = CacheHashText(CacheFrame(Path) + CacheFrame(execution.Executable) + CacheEnvironmentIdentity(effective)
            + CacheFrame(attributesRequired ? "attributes" : "raw") + string.Concat(inputFiles.Order(StringComparer.Ordinal).Select(CacheFrame)));
        await _commandCacheAuthorityGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_commandCacheAuthorities.TryGetValue(environmentIdentity, out CommandCacheAuthority? known)
                && await IsCommandCacheAuthorityCurrentAsync(known, token).ConfigureAwait(false))
            { return CacheKindVerified(known, kind) ? known : null; }
            _commandCacheAuthorities.Remove(environmentIdentity);
            CommandCacheAuthority? authority = await BuildCommandCacheAuthorityAsync(execution, effective, overrides, environmentIdentity, inputFiles, attributesRequired, token).ConfigureAwait(false);
            if (authority is null) { return null; }
            if (_commandCacheAuthorities.Count >= 8) { _commandCacheAuthorities.Clear(); }
            _commandCacheAuthorities.Add(environmentIdentity, authority);
            return CacheKindVerified(authority, kind) ? authority : null;
        }
        catch (Exception error) when (IsCommandCacheContextError(error)) { return null; }
        finally { _commandCacheAuthorityGate.Release(); }
    }

    private static bool CacheKindVerified(CommandCacheAuthority authority, GitCachedReadKind kind)
        => (kind != GitCachedReadKind.Difftastic || authority.DifftasticVerified)
            && (kind != GitCachedReadKind.Blame || authority.BlameDependenciesVerified)
            && (kind is not (GitCachedReadKind.Diff or GitCachedReadKind.RangeDiff or GitCachedReadKind.Difftastic)
                || authority.DiffDependenciesVerified);

    private static bool IsCommandCacheContextError(Exception error) => error is IOException or UnauthorizedAccessException
        or Win32Exception or GitCommandException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException;

    private async Task<bool> IsCommandCacheAuthorityCurrentAsync(CommandCacheAuthority authority, CancellationToken token)
    {
        try { return await SnapshotCommandCacheFilesAsync(authority, token).ConfigureAwait(false) == authority.Fingerprint; }
        catch (Exception error) when (IsCommandCacheContextError(error)) { return false; }
    }

    private Task<CommandCacheAuthority?> BuildCommandCacheAuthorityAsync(GitExecutionEnvironment execution,
        IReadOnlyDictionary<string, string?> effective, IReadOnlyDictionary<string, string?>? overrides,
        string environmentIdentity, IReadOnlyList<string> inputFiles, bool attributesRequired, CancellationToken token)
        // ConfigureAwait(false) does not move synchronously completed work off the caller's UI thread.
        // Task.Run guarantees that directory inspection and native-bootstrap preparation begin on a worker.
        => Task.Run(() => BuildCommandCacheAuthorityCoreAsync(execution, effective, overrides, environmentIdentity, inputFiles, attributesRequired, token), token);

    private async Task<CommandCacheAuthority?> BuildCommandCacheAuthorityCoreAsync(GitExecutionEnvironment execution,
        IReadOnlyDictionary<string, string?> effective, IReadOnlyDictionary<string, string?>? overrides,
        string environmentIdentity, IReadOnlyList<string> inputFiles, bool attributesRequired, CancellationToken token)
    {
        // A Git script/interpreter can read arbitrary external files without changing its argv/env/image.
        // Its byte hash is not an authority for those inputs. Preserve native fresh execution instead.
        string? git = ResolveCommandCacheExecutable(execution.Executable, effective, Path);
        if (git is null || !IsCommandCacheNativeGitExecutable(git)) { return null; }
        // Discover effective GIT_DIR/WORK_TREE/COMMON_DIR afresh. Info can describe an older binding.
        GitRunner discoveryRunner = new(GitExecutionEnvironment.Capture(execution.Options, effective));
        GitRepositoryInfo info = await DiscoverRepositoryAsync(Path, discoveryRunner, token).ConfigureAwait(false);
        HashSet<string> files = new(StringComparer.Ordinal);
        HashSet<string> recursive = new(StringComparer.Ordinal);
        void Add(string path) { if (path.IndexOfAny(['\0', '\n', '\r']) >= 0) { throw new NotSupportedException("Unverifiable cache dependency path."); } files.Add(System.IO.Path.GetFullPath(path, Path)); }
        foreach (string root in new[] { Path, info.RootPath, info.GitDirectory, info.CommonDirectory }) { Add(root); }
        foreach (string input in inputFiles) { Add(input); }
        foreach (string path in new[] { System.IO.Path.Combine(info.RootPath, ".git"), System.IO.Path.Combine(info.GitDirectory, "HEAD"),
            System.IO.Path.Combine(info.GitDirectory, "commondir"), System.IO.Path.Combine(info.GitDirectory, "gitdir"),
            System.IO.Path.Combine(info.CommonDirectory, "config"), System.IO.Path.Combine(info.GitDirectory, "config.worktree"),
            System.IO.Path.Combine(info.CommonDirectory, "packed-refs"), System.IO.Path.Combine(info.GitDirectory, "packed-refs") }) { Add(path); }
        // These overrides are read by Git from the process environment, not from config.
        // Git setup may chdir before opening these paths. Relative overrides are admitted only
        // when the command starts at its discovered native root; other cwd cases stay fresh.
        bool relativeGraphPathsVerified = System.IO.Path.TrimEndingDirectorySeparator(Path)
            == System.IO.Path.TrimEndingDirectorySeparator(info.RootPath);
        if (effective.TryGetValue("GIT_GRAFT_FILE", out string? graft))
        {
            if (string.IsNullOrEmpty(graft) || (!System.IO.Path.IsPathFullyQualified(graft) && !relativeGraphPathsVerified)) { return null; }
            Add(graft);
        }
        else
        {
            Add(System.IO.Path.Combine(info.CommonDirectory, "info", "grafts"));
            Add(System.IO.Path.Combine(info.GitDirectory, "info", "grafts"));
        }
        if (effective.TryGetValue("GIT_SHALLOW_FILE", out string? shallow))
        {
            // An explicitly empty native shallow path disables shallow handling.
            if (shallow is null) { return null; }
            if (shallow.Length > 0)
            {
                if (!System.IO.Path.IsPathFullyQualified(shallow) && !relativeGraphPathsVerified) { return null; }
                Add(shallow);
            }
        }
        else
        {
            Add(System.IO.Path.Combine(info.CommonDirectory, "shallow"));
            Add(System.IO.Path.Combine(info.GitDirectory, "shallow"));
        }
        string replaceBase = effective.TryGetValue("GIT_REPLACE_REF_BASE", out string? configuredReplaceBase)
            ? configuredReplaceBase ?? "" : "refs/replace/";
        string? replaceDependency = CommandCacheReplaceDependency(replaceBase);
        if (replaceDependency is null) { return null; }
        foreach (string directory in new[] { info.CommonDirectory, info.GitDirectory }.Distinct(StringComparer.Ordinal))
        {
            recursive.Add(System.IO.Path.Combine(directory, replaceDependency));
            recursive.Add(System.IO.Path.Combine(directory, "reftable"));
        }
        if (attributesRequired)
        {
            foreach (string path in new[] { System.IO.Path.Combine(info.CommonDirectory, "info", "attributes"),
                System.IO.Path.Combine(info.GitDirectory, "info", "attributes"), System.IO.Path.Combine(info.GitDirectory, "index"),
                System.IO.Path.Combine(info.CommonDirectory, "index"), System.IO.Path.Combine(info.RootPath, ".mailmap") }) { Add(path); }
        }
        // Shared index files can supply the attributes fallback for an otherwise immutable diff.
        if (attributesRequired)
        {
            foreach (string directory in new[] { info.GitDirectory, info.CommonDirectory }.Distinct(StringComparer.Ordinal))
            { foreach (string shared in Directory.EnumerateFiles(directory, "sharedindex.*")) { token.ThrowIfCancellationRequested(); Add(shared); } }
        }
        List<string> stores = [System.IO.Path.Combine(info.CommonDirectory, "objects")];
        if (effective.GetValueOrDefault("GIT_OBJECT_DIRECTORY") is { Length: > 0 } objectDirectory) { stores.Add(System.IO.Path.GetFullPath(objectDirectory, Path)); }
        if (effective.GetValueOrDefault("GIT_ALTERNATE_OBJECT_DIRECTORIES") is { Length: > 0 } alternates)
        {
            // Quoted separator/path syntax is Git's grammar. Do not approximate it with Split.
            if (alternates.Contains('\"') || alternates.Contains('\\')) { return null; }
            stores.AddRange(alternates.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => System.IO.Path.GetFullPath(p, Path)));
        }
        HashSet<string> visitedStores = new(StringComparer.Ordinal);
        for (int i = 0; i < stores.Count; i++)
        {
            string store = stores[i];
            if (!visitedStores.Add(store)) { continue; }
            if (visitedStores.Count > 128) { return null; }
            Add(store); string alternateFile = System.IO.Path.Combine(store, "info", "alternates"); Add(alternateFile);
            if (File.Exists(alternateFile))
            {
                foreach (string line in await File.ReadAllLinesAsync(alternateFile, token).ConfigureAwait(false))
                {
                    if (line.Length == 0) { continue; }
                    if (line.Contains('\"') || line.Contains('\\')) { return null; }
                    stores.Add(System.IO.Path.GetFullPath(line, store));
                }
            }
        }
        foreach (string candidate in CommandCacheExecutableCandidates(execution.Executable, effective, Path)) { Add(candidate); }
        // These native variables include missing default roots and respect disabled system/global files.
        // Unsupported older Git => uncached read, never a new error in the reader.
        string[] rootVariables = attributesRequired
            ? ["GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL", "GIT_ATTR_SYSTEM", "GIT_ATTR_GLOBAL"]
            : ["GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL"];
        foreach (string variable in rootVariables)
        {
            string? roots = await ReadCommandCacheVariableAsync(variable, effective, overrides, token).ConfigureAwait(false);
            if (roots is null) { return null; }
            foreach (string root in roots.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { Add(root.TrimEnd('\r')); }
        }
        // Bootstrap closure with Git's own parser, including inactive includeIf directives and missing targets.
        List<string> configFiles = files.Where(p => p.EndsWith("config", StringComparison.Ordinal) || p.EndsWith("config.worktree", StringComparison.Ordinal)).ToList();
        string? origins = await ReadCommandCacheProbeAsync(["config", "--null", "--list", "--show-origin", "--show-scope", "--includes"], overrides, token).ConfigureAwait(false);
        if (origins is null) { return null; }
        string[] fields = origins.Split('\0');
        if (fields.Length % 3 != 1 || fields[^1].Length != 0) { return null; }
        Dictionary<string, string> selected = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 2 < fields.Length; i += 3)
        {
            string origin = fields[i + 1];
            if (origin.StartsWith("file:", StringComparison.Ordinal)) { string source = System.IO.Path.GetFullPath(origin[5..], Path); Add(source); configFiles.Add(source); }
            int newline = fields[i + 2].IndexOf('\n');
            if (newline >= 0) { selected[fields[i + 2][..newline]] = fields[i + 2][(newline + 1)..]; }
        }
        string? directives = await ReadCommandCacheProbeAsync(["config", "--null", "--path", "--show-origin", "--includes", "--get-regexp", "^(include|includeif\\..*)\\.path$"], overrides, token, noMatch: true).ConfigureAwait(false);
        if (directives is null) { return null; }
        string[] includeFields = directives.Split('\0');
        if (includeFields.Length % 2 != 1 || includeFields[^1].Length != 0) { return null; }
        for (int i = 0; i + 1 < includeFields.Length; i += 2)
        {
            string origin = includeFields[i]; int newline = includeFields[i + 1].IndexOf('\n');
            if (newline < 0) { return null; }
            string relativeTo = origin.StartsWith("file:", StringComparison.Ordinal)
                ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(origin[5..], Path))! : Path;
            string target = System.IO.Path.GetFullPath(includeFields[i + 1][(newline + 1)..], relativeTo); Add(target); configFiles.Add(target);
        }
        // Roots with no settings and missing roots must still be parsed/tracked on the next generation.
        foreach (string variable in new[] { "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL" })
        {
            string? roots = await ReadCommandCacheVariableAsync(variable, effective, overrides, token).ConfigureAwait(false);
            if (roots is null) { return null; }
            configFiles.AddRange(roots.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => System.IO.Path.GetFullPath(p.TrimEnd('\r'), Path)));
        }
        HashSet<string> parsed = new(StringComparer.Ordinal);
        for (int i = 0; i < configFiles.Count; i++)
        {
            string source = configFiles[i]; Add(source);
            if (!parsed.Add(source) || !File.Exists(source)) { continue; }
            if (parsed.Count > 256) { return null; }
            string? included = await ReadCommandCacheProbeAsync(["config", "--no-includes", "--file", source, "--null", "--path", "--get-regexp", "^(include|includeif\\..*)\\.path$"], overrides, token, noMatch: true).ConfigureAwait(false);
            if (included is null) { return null; }
            foreach (string record in included.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int newline = record.IndexOf('\n'); if (newline < 0) { return null; }
                string target = System.IO.Path.GetFullPath(record[(newline + 1)..], System.IO.Path.GetDirectoryName(source)!);
                Add(target); configFiles.Add(target);
            }
        }
        // Native origins preserve ordering/conditions; snapshots alone never reimplement config semantics.
        // A symbolic HEAD's target can move without changing HEAD's own bytes.
        Queue<string> symbolic = new(); symbolic.Enqueue(System.IO.Path.Combine(info.GitDirectory, "HEAD"));
        HashSet<string> symbolicSeen = new(StringComparer.Ordinal);
        while (symbolic.TryDequeue(out string? reference))
        {
            Add(reference); if (!symbolicSeen.Add(reference)) { return null; }
            if (symbolicSeen.Count > 32) { return null; }
            if (!File.Exists(reference)) { continue; }
            string text = await File.ReadAllTextAsync(reference, token).ConfigureAwait(false);
            if (!text.StartsWith("ref: ", StringComparison.Ordinal)) { continue; }
            string target = text[5..].TrimEnd('\r', '\n');
            if (!target.StartsWith("refs/", StringComparison.Ordinal) || target.Split('/').Any(p => p is "" or "." or "..")) { return null; }
            symbolic.Enqueue(System.IO.Path.Combine(info.CommonDirectory, target));
            if (info.CommonDirectory != info.GitDirectory) { symbolic.Enqueue(System.IO.Path.Combine(info.GitDirectory, target)); }
        }
        // Arbitrary symbolic source expressions may use any loose/packed ref, not just HEAD.
        // Only this refs namespace is scanned, never logs/locks/the whole .git directory.
        if (attributesRequired && (selected.ContainsKey("mailmap.blob") || effective.ContainsKey("GIT_ATTR_SOURCE")))
        { recursive.Add(System.IO.Path.Combine(info.CommonDirectory, "refs")); recursive.Add(System.IO.Path.Combine(info.GitDirectory, "refs")); }
        if (attributesRequired && selected.ContainsKey("mailmap.file"))
        {
            string? mailmap = await ReadCommandCacheProbeAsync(["config", "--path", "--get", "mailmap.file"], overrides, token).ConfigureAwait(false);
            if (mailmap is null) { return null; } Add(mailmap.TrimEnd('\r', '\n'));
        }
        string nativeIdentity = CacheHashText(origins);
        bool diffVerified = !selected.TryGetValue("diff.submodule", out string? submodule) || submodule is "short" or "";
        // Blame may invoke textconv, unlike the explicit --no-textconv diff readers.
        // Without a closed driver executable/context, preserve its native execution and display uncached.
        bool blameVerified = !selected.Any(setting => setting.Key.StartsWith("diff.", StringComparison.OrdinalIgnoreCase)
            && setting.Key.EndsWith(".textconv", StringComparison.OrdinalIgnoreCase) && setting.Value.Length > 0);
        bool dftVerified = false;
        string tool = attributesRequired ? selected.GetValueOrDefault("difftool.difftastic.path", "difft") : "";
        if (attributesRequired && selected.TryGetValue("difftool.difftastic.cmd", out string? command))
        {
            try
            {
                string[] parts = GitGrepArguments.Split(command).ToArray();
                // A shell pipeline/interpreter/configuration program has further dependencies we cannot prove here.
                string first = parts.FirstOrDefault() ?? "";
                bool shell = new[] { "env", "sh", "bash", "dash", "zsh", "fish", "cmd", "powershell", "pwsh", "python", "python3", "perl", "ruby", "node" }
                    .Contains(System.IO.Path.GetFileName(first), StringComparer.Ordinal);
                if (first.Length > 0 && !shell && command.IndexOfAny(['\r', '\n']) < 0
                    && parts.All(IsCommandCacheDifftasticTokenVerifiable) && !first.Contains('$')) { tool = first; }
                else { tool = ""; }
            }
            catch (ArgumentException) { tool = ""; }
        }
        if (tool.Length > 0)
        {
            foreach (string candidate in CommandCacheExecutableCandidates(tool, effective, Path)) { Add(candidate); }
            dftVerified = ResolveCommandCacheExecutable(tool, effective, Path) is not null;
        }
        CommandCacheAuthority seed = new(environmentIdentity, "", nativeIdentity, git, files.Order(StringComparer.Ordinal).ToArray(),
            recursive.Order(StringComparer.Ordinal).ToArray(), attributesRequired && !info.IsBare ? info.RootPath : null,
            dftVerified, diffVerified, blameVerified);
        // A second native config read closes the discovery window without trusting a half-read include graph.
        string before = await SnapshotCommandCacheFilesAsync(seed, token).ConfigureAwait(false);
        string? check = await ReadCommandCacheProbeAsync(["config", "--null", "--list", "--show-origin", "--show-scope", "--includes"], overrides, token).ConfigureAwait(false);
        if (check is null || CacheHashText(check) != nativeIdentity) { return null; }
        string after = await SnapshotCommandCacheFilesAsync(seed, token).ConfigureAwait(false);
        return before == after ? seed with { Fingerprint = after } : null;
    }

    private static string? CommandCacheReplaceDependency(string replaceBase)
    {
        // Git uses a literal ref prefix, not an arbitrary filesystem directory or a shell pattern.
        if (!replaceBase.StartsWith("refs/", StringComparison.Ordinal)
            || replaceBase.Any(c => char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            || replaceBase.Contains("..", StringComparison.Ordinal) || replaceBase.Contains("@{", StringComparison.Ordinal)) { return null; }
        string trimmed = replaceBase.EndsWith('/') ? replaceBase[..^1] : replaceBase;
        if (trimmed.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.')
            || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))) { return null; }
        // Without a trailing slash the prefix can match sibling refs: close its parent namespace.
        return replaceBase.EndsWith('/') ? trimmed : System.IO.Path.GetDirectoryName(trimmed);
    }

    private static bool IsCommandCacheNativeGitExecutable(string path)
    {
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        if (identity?.IsRegular != true) { return false; }
        using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { return false; }
        bool native = IsCommandCacheNativeGitImage(path, location.Path, file);
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        return native && GitRulesIdentity.ReadPath(location.Path) == identity;
    }

    private static bool IsCommandCacheNativeGitImage(string path, string resolvedPath, Stream file)
    {
        // The qualified portable product currently runs on Linux. Other image formats stay fresh.
        if (!OperatingSystem.IsLinux() || IsInterpreter(path) || IsInterpreter(resolvedPath)) { return false; }
        Span<byte> header = stackalloc byte[20]; int length = 0;
        while (length < header.Length)
        {
            int count = file.Read(header[length..]);
            if (count == 0) { return false; }
            length += count;
        }
        file.Position = 0;
        if (!header[..4].SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' })
            || header[4] is not (1 or 2) || header[5] is not (1 or 2) || header[6] != 1) { return false; }
        int type = header[5] == 1 ? header[16] | header[17] << 8 : header[17] | header[16] << 8;
        return type is 2 or 3; // ET_EXEC or ET_DYN (PIE), never a shebang/text script or relocatable image.

        static bool IsInterpreter(string value)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
            return name is "sh" or "bash" or "dash" or "ash" or "zsh" or "ksh" or "fish" or "csh" or "tcsh"
                or "env" or "busybox" or "python" or "python2" or "python3" or "perl" or "ruby" or "node" or "nodejs"
                or "php" or "lua" or "tclsh" or "wish" or "pwsh" or "powershell" or "dotnet" or "mono" or "java";
        }
    }

    private static bool IsCommandCacheDifftasticTokenVerifiable(string token)
        // Only complete Git-supplied file placeholders are part of the captured comparison input.
        // Shell substitution, other variables and concatenated/modified expansions need unknown dependencies.
        => token is "$LOCAL" or "$REMOTE" or "${LOCAL}" or "${REMOTE}"
            || !token.Any(c => c is '$' or '`' or ';' or '|' or '&' or '<' or '>' or '(' or ')'
                or '*' or '?' or '[' or ']' or '~' or '#' or '{' or '}');

    private async Task<string?> ReadCommandCacheVariableAsync(string variable, IReadOnlyDictionary<string, string?> effective,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken token)
    {
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, "var", variable], token, environment: environment).ConfigureAwait(false);
        if (result.Success) { return CacheUtf8.GetString(result.StandardOutput); }
        bool Enabled(string name) => effective.GetValueOrDefault(name) is string value
            && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase));
        bool disabled = variable switch
        {
            "GIT_CONFIG_SYSTEM" => Enabled("GIT_CONFIG_NOSYSTEM") || effective.GetValueOrDefault("GIT_CONFIG_SYSTEM") == "",
            "GIT_CONFIG_GLOBAL" => effective.TryGetValue("GIT_CONFIG_GLOBAL", out string? global) && global == "",
            "GIT_ATTR_SYSTEM" => Enabled("GIT_ATTR_NOSYSTEM"),
            _ => false
        };
        // Git2.53 reports an explicitly disabled root as exit1 with no output or diagnostics.
        // A successful single LF is another native empty-root representation, handled above.
        return disabled && result.ExitCode == 1 && result.StandardOutput.Length == 0
            && result.StandardError.Length == 0 && result.StandardErrorBytes.Length == 0 ? "" : null;
    }

    private async Task<string?> ReadCommandCacheProbeAsync(IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken token, bool noMatch = false)
    {
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token, environment: environment).ConfigureAwait(false);
        if (!result.Success && !(noMatch && result.ExitCode == 1)) { return null; }
        return CacheUtf8.GetString(result.StandardOutput);
    }

    private static IEnumerable<string> CommandCacheExecutableCandidates(string executable,
        IReadOnlyDictionary<string, string?> environment, string workingDirectory)
        => executable.Contains(System.IO.Path.DirectorySeparatorChar)
            ? [System.IO.Path.GetFullPath(executable, workingDirectory)]
            : (environment.GetValueOrDefault("PATH") ?? "").Split(System.IO.Path.PathSeparator)
                .Select(directory => System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, executable), workingDirectory));

    private static string? ResolveCommandCacheExecutable(string executable, IReadOnlyDictionary<string, string?> environment, string workingDirectory)
    {
        foreach (string candidate in CommandCacheExecutableCandidates(executable, environment, workingDirectory))
        { if (File.Exists(candidate) && !Directory.Exists(candidate) && (OperatingSystem.IsWindows()
                || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
            { return System.IO.Path.GetFullPath(candidate); } }
        return null;
    }

    private static Task<string> SnapshotCommandCacheFilesAsync(CommandCacheAuthority authority, CancellationToken token)
        // Every validation path (including a fully synchronous cache hit) dispatches FS work explicitly.
        => Task.Run(() => SnapshotCommandCacheFilesCoreAsync(authority, token), token);

    private static async Task<string> SnapshotCommandCacheFilesCoreAsync(CommandCacheAuthority authority, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(CacheFrame(text)));
        Add(authority.EnvironmentIdentity); Add(authority.NativeIdentity);
        HashSet<string> paths = new(authority.Files, StringComparer.Ordinal);
        int inspectedEntries = 0;
        void InspectEntry()
        {
            token.ThrowIfCancellationRequested();
            if (++inspectedEntries > 100000) { throw new NotSupportedException("Cache dependency entries exceed their inspection budget."); }
        }
        foreach (string root in authority.RecursiveDirectories)
        {
            InspectEntry(); paths.Add(root);
            if (!Directory.Exists(root)) { continue; }
            Queue<string> pending = new(); pending.Enqueue(root); int count = 0;
            while (pending.TryDequeue(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100000) { throw new NotSupportedException("Cache dependency tree exceeds its inspection budget."); }
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    InspectEntry();
                    if (child.EndsWith(".lock", StringComparison.Ordinal)) { continue; }
                    paths.Add(child); GitRulesIdentity? identity = GitRulesIdentity.ReadPath(child);
                    if (identity?.IsDirectory == true) { pending.Enqueue(child); }
                    else if (identity?.IsLink == true) { throw new NotSupportedException("Unverifiable linked cache dependency tree."); }
                }
            }
        }
        if (authority.AttributesRoot is string worktree)
        {
            Queue<string> pending = new(); pending.Enqueue(worktree); int count = 0;
            while (pending.TryDequeue(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100000) { throw new NotSupportedException("Cache attribute tree exceeds its inspection budget."); }
                string attributes = System.IO.Path.Combine(directory, ".gitattributes");
                // The enumerated set detects creation/removal; empty new directories are not attribute changes.
                if (GitRulesIdentity.ReadPath(attributes) is not null) { paths.Add(attributes); }
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    InspectEntry();
                    if (System.IO.Path.GetFileName(child) == ".git") { continue; }
                    if (GitRulesIdentity.ReadPath(child)?.IsDirectory == true) { pending.Enqueue(child); }
                }
            }
        }
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); GitRulesLocation location = GitRulesFiles.Resolve(path); Add(path); Add(location.Path);
            foreach (GitRulesPathPart part in location.Parts) { Add(part.Path); Add(part.Identity?.ToString() ?? "missing"); Add(part.LinkTarget ?? "no-link"); }
            GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
            if (identity is null) { Add("missing"); continue; }
            if (identity.Value.IsDirectory) { Add("directory"); continue; }
            if (location.Path == "/dev/null" && identity.Value.Type == 0x2000) { Add("native-null-device"); continue; }
            if (!identity.Value.IsRegular) { throw new NotSupportedException("Unverifiable cache dependency file."); }
            Add(OperatingSystem.IsWindows() ? File.GetAttributes(location.Path).ToString() : File.GetUnixFileMode(location.Path).ToString());
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw new IOException("Cache dependency was replaced."); }
            // Bind classification to the same opened image whose bytes become this fingerprint.
            if (path == authority.GitExecutablePath && !IsCommandCacheNativeGitImage(path, location.Path, file))
            { throw new NotSupportedException("Git executable dependencies are not closed for command caching."); }
            byte[] digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false); hash.AppendData(digest);
            GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
            if (GitRulesIdentity.ReadPath(location.Path) != identity) { throw new IOException("Cache dependency was replaced."); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
