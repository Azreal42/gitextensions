using System.Globalization;
using GitCommands.Settings;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    // All readers share the canonical command, including its resolved object IDs and literal paths.
    private sealed record GitDiffReadCommand(IReadOnlyList<string> Arguments, int[]? AllowedExitCodes = null);

    public async Task<GitDiffDocument> GetDiffDocumentAsync(string? path = null, bool staged = false, string? revision = null,
        GitComparisonDiffOptions? options = null, GitDiffDisplayOptions? displayOptions = null, string? originalPath = null,
        CancellationToken cancellationToken = default)
        => await ReadDiffDocumentAsync(await BuildWorktreeDiffArgumentsAsync(path, staged, revision, options, originalPath, cancellationToken).ConfigureAwait(false),
            options ?? new(), displayOptions, readOnly: false, cancellationToken).ConfigureAwait(false);

    public async Task<GitDiffDocument> GetRevisionFileDiffDocumentAsync(GitRevisionChanges revisions, GitRevisionFile file,
        GitComparisonDiffOptions? options = null, GitDiffDisplayOptions? displayOptions = null, CancellationToken cancellationToken = default)
        => await ReadDiffDocumentAsync(await BuildRevisionFileDiffArgumentsAsync(revisions, file, options ?? new(), cancellationToken).ConfigureAwait(false),
            options ?? new(), displayOptions, readOnly: false, cancellationToken).ConfigureAwait(false);

    public async Task<GitDiffDocument> GetComparisonFileDiffDocumentAsync(GitComparison comparison, GitRevisionFile file,
        GitComparisonDiffOptions? options = null, GitDiffDisplayOptions? displayOptions = null, CancellationToken cancellationToken = default)
        => await ReadDiffDocumentAsync(await BuildComparisonFileDiffArgumentsAsync(comparison, file, options, cancellationToken).ConfigureAwait(false),
            options ?? new(), displayOptions, readOnly: false, cancellationToken).ConfigureAwait(false);

    public async Task<GitDiffDocument> GetCombinedFileDiffDocumentAsync(GitCombinedDiff diff, GitRevisionFile file,
        GitCombinedDiffOptions? options = null, GitDiffDisplayOptions? displayOptions = null, CancellationToken cancellationToken = default)
        => await ReadDiffDocumentAsync(await BuildCombinedFileDiffArgumentsAsync(diff, file, (options ?? new()) with { UseGitColoring = false }, cancellationToken).ConfigureAwait(false),
            options?.DiffOptions ?? new(), displayOptions, readOnly: true, cancellationToken).ConfigureAwait(false);

    public async Task<GitDiffDocument> GetRangeDiffDocumentAsync(GitRangeDiffComparison comparison, GitRangeDiffOptions? options = null,
        GitDiffDisplayOptions? displayOptions = null, CancellationToken cancellationToken = default)
        => await ReadDiffDocumentAsync(await BuildRangeDiffArgumentsAsync(comparison, (options ?? new()) with { UseGitColoring = false }, cancellationToken).ConfigureAwait(false),
            new(options?.IgnoreWhitespace ?? IgnoreWhitespaceKind.None, options?.ContextLines ?? 3, options?.ShowEntireFile ?? false, options?.TreatAllFilesAsText ?? false),
            displayOptions, readOnly: true, cancellationToken, forceGitColoring: true).ConfigureAwait(false);

    /// <summary>Match FileViewer.IsDifftasticEnabled: the user's effective difftool command enables the mode; no configuration is written.</summary>
    public async Task<bool> IsDifftasticAvailableAsync(CancellationToken cancellationToken = default)
        => (await GetConfigAsync("^difftool\\.difftastic\\.cmd$", cancellationToken: cancellationToken).ConfigureAwait(false)).Any(item => !string.IsNullOrEmpty(item.Value));

    private async Task<string> ExecuteDiffReadAsync(GitDiffReadCommand command, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null)
        => command.Arguments.Count == 0 ? "" : (await ExecuteAsync(command.Arguments, token,
            allowedExitCodes: command.AllowedExitCodes, environment: environment).ConfigureAwait(false)).StandardOutput;

    private async Task<GitDiffDocument> ReadDiffDocumentAsync(GitDiffReadCommand command, GitComparisonDiffOptions diffOptions,
        GitDiffDisplayOptions? displayOptions, bool readOnly, CancellationToken token, bool forceGitColoring = false)
    {
        displayOptions ??= new();
        if (!Enum.IsDefined(displayOptions.Appearance)) { throw new ArgumentOutOfRangeException(nameof(displayOptions)); }
        token.ThrowIfCancellationRequested();
        string patch = await ExecuteDiffReadAsync(command, token).ConfigureAwait(false);
        if (command.Arguments.Count == 0) { return new(patch, patch, GitDiffAppearance.Patch, !readOnly) { UseGitColoring = forceGitColoring || displayOptions.UseGitColoring }; }
        GitDiffAppearance appearance = readOnly ? GitDiffAppearance.Patch : displayOptions.Appearance;
        if (appearance == GitDiffAppearance.Difftastic && await IsDifftasticAvailableAsync(token).ConfigureAwait(false))
        {
            GitDiffReadCommand external = await BuildDifftasticCommandAsync(command, token).ConfigureAwait(false);
            try
            {
                IReadOnlyDictionary<string, string?> environment = GetDifftasticEnvironment(diffOptions, displayOptions);
                int width = int.TryParse(environment["DFT_WIDTH"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int configuredWidth) && configuredWidth > 0
                    ? configuredWidth : GetDifftasticWidth(displayOptions);
                string display = await ExecuteDiffReadAsync(external, token, environment).ConfigureAwait(false);
                return new(patch, display, GitDiffAppearance.Difftastic, false) { DifftasticWidth = width, UseGitColoring = true };
            }
            catch (GitCommandException exception) when (IsMissingDifftasticExecutable(exception.Result.StandardError))
            {
                // A configured but missing executable is unavailable; genuine tool failures and cancellation remain visible.
            }
        }
        if (appearance == GitDiffAppearance.Difftastic) { appearance = GitDiffAppearance.Patch; }
        bool color = forceGitColoring || displayOptions.UseGitColoring || appearance == GitDiffAppearance.GitWordDiff;
        if (!color) { return new(patch, patch, appearance, !readOnly && appearance == GitDiffAppearance.Patch); }
        List<string> arguments = [.. command.Arguments];
        int terminator = arguments.IndexOf("--");
        if (terminator < 0) { terminator = arguments.Count; }
        int noColor = arguments.IndexOf("--no-color");
        if (noColor >= 0 && noColor < terminator) { arguments[noColor] = "--color=always"; }
        else { arguments.Insert(terminator++, "--color=always"); }
        if (appearance == GitDiffAppearance.GitWordDiff) { arguments.Insert(terminator, "--word-diff=color"); }
        string gitCommand = arguments[GetDiffCommandIndex(arguments)];
        IReadOnlyList<string> configuration = await GetDiffDisplayConfigurationAsync(gitCommand, displayOptions.ReverseGitColoring, token).ConfigureAwait(false);
        string colored = await ExecuteDiffReadAsync(new([.. configuration, .. arguments], command.AllowedExitCodes), token).ConfigureAwait(false);
        return new(patch, colored, appearance, !readOnly && appearance == GitDiffAppearance.Patch && GitAnsiColorParser.StripAnsi(colored) == patch) { UseGitColoring = true };
    }

    private async Task<IReadOnlyList<string>> GetDiffDisplayConfigurationAsync(string command, bool reverse, CancellationToken token)
    {
        IReadOnlyList<KeyValuePair<string, string>> effective = await GetConfigAsync("^(color\\.|diff\\.|core\\.safecrlf$)", cancellationToken: token).ConfigureAwait(false);
        HashSet<string> configured = new(effective.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);
        List<string> arguments = [];
        // Upstream GitCommandConfiguration.Default, restricted to absent settings so custom values remain effective.
        if (command is "diff" or "range-diff") { Default("color.ui", "never"); }
        if (command == "diff")
        {
            Default("diff.submodule", "short"); Default("diff.noprefix", "false"); Default("diff.mnemonicprefix", "false");
            Default("diff.ignoresubmodules", "none"); Default("core.safecrlf", "false");
        }
        Default("diff.colormovedws", "no"); Default("diff.wordregex", "[a-z0-9_]+|."); Default("diff.colormoved", "dimmed-zebra");
        string attribute = reverse ? " reverse" : "";
        Default("color.diff.old", "red" + attribute); Default("color.diff.new", "green" + attribute);
        if (reverse)
        {
            bool bright = await GetRangeDiffGitVersionAsync(token).ConfigureAwait(false) >= new Version(2, 26);
            Default("color.diff.oldmoved", bright ? "black brightmagenta" : "reverse bold magenta");
            Default("color.diff.newmoved", bright ? "black brightblue" : "reverse bold blue");
            Default("color.diff.oldmovedalternative", bright ? "black brightcyan" : "reverse bold cyan");
            Default("color.diff.newmovedalternative", bright ? "black brightyellow" : "reverse bold yellow");
        }
        Default("color.diff.oldmoveddimmed", "magenta dim" + attribute); Default("color.diff.newmoveddimmed", "blue dim" + attribute);
        Default("color.diff.oldmovedalternativedimmed", "cyan dim" + attribute); Default("color.diff.newmovedalternativedimmed", "yellow dim" + attribute);
        if (command == "range-diff")
        {
            Default("color.diff.oldbold", "brightred" + attribute); Default("color.diff.newbold", "brightgreen" + attribute);
            Default("color.diff.olddimmed", "red dim" + attribute); Default("color.diff.newdimmed", "green dim" + attribute);
        }
        return arguments;
        void Default(string key, string value) { if (!configured.Contains(key)) { arguments.AddRange(["-c", key + "=" + value]); } }
    }

    private async Task<GitDiffReadCommand> BuildDifftasticCommandAsync(GitDiffReadCommand canonical, CancellationToken token)
    {
        List<string> arguments = [.. canonical.Arguments];
        int command = GetDiffCommandIndex(arguments);
        int terminator = arguments.IndexOf("--");
        bool root = arguments.Take(terminator < 0 ? arguments.Count : terminator).Contains("--root");
        arguments[command] = "difftool";
        arguments.Remove("--no-ext-diff");
        if (root)
        {
            arguments.Remove("--root"); arguments.Remove("-r"); arguments.Remove("--no-commit-id"); arguments.Remove("--patch");
            string emptyTree = ReadOutputLine((await ExecuteAsync(["hash-object", "-t", "tree", "--stdin"], token, standardInput: "").ConfigureAwait(false)).StandardOutput);
            arguments.Insert(arguments.IndexOf("--") - 1, emptyTree);
        }
        arguments.InsertRange(command + 1, ["--no-prompt", "--tool=difftastic", "--trust-exit-code", "--find-renames", "--find-copies"]);
        return new(arguments, canonical.AllowedExitCodes);
    }

    private static IReadOnlyDictionary<string, string?> GetDifftasticEnvironment(GitComparisonDiffOptions diff, GitDiffDisplayOptions display)
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        Add("DFT_COLOR", "always"); Add("DFT_BACKGROUND", "light"); Add("DFT_SYNTAX_HIGHLIGHT", display.SyntaxHighlighting ? "on" : "off");
        Add("DFT_CONTEXT", (diff.ShowEntireFile ? 9000 : diff.ContextLines).ToString(CultureInfo.InvariantCulture));
        Add("DFT_STRIP_CR", diff.IgnoreWhitespace == IgnoreWhitespaceKind.None ? "off" : "on");
        Add("DFT_WIDTH", GetDifftasticWidth(display).ToString(CultureInfo.InvariantCulture));
        return environment;
        void Add(string key, string value) { environment.Add(key, Environment.GetEnvironmentVariable(key) ?? value); }
    }

    private static int GetDiffCommandIndex(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count && arguments[index] != "--"; index++)
        {
            if (arguments[index] is "diff" or "diff-tree" or "range-diff") { return index; }
        }
        throw new InvalidOperationException("A diff read command must contain a Git diff operation before its literal paths.");
    }

    private static int GetDifftasticWidth(GitDiffDisplayOptions display) => Math.Clamp(display.ViewerWidth / 7, 88, 200) / 2 * 2;

    private static bool IsMissingDifftasticExecutable(string error)
        => error.Split('\n').Any(line =>
            // Git's difftool shell diagnoses an unavailable configured program. A tool's own
            // "not found" domain error must not silently replace its output with a patch.
            (line.Contains("git-mergetool--lib:", StringComparison.Ordinal) || line.Contains("git-difftool--helper:", StringComparison.Ordinal))
                && (line.TrimEnd('\r').EndsWith(": not found", StringComparison.Ordinal)
                    || line.Contains("No such file or directory", StringComparison.Ordinal))
            || line.Contains("cannot run", StringComparison.Ordinal) && line.Contains("No such file or directory", StringComparison.Ordinal));
}
