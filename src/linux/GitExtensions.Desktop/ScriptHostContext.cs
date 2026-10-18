using GitExtensions.Linux.Scripts;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

internal sealed record ScriptHostSelection(bool HasRevisionGrid = false, GitCommit? Selected = null,
    IReadOnlyList<GitCommit>? SelectedCommits = null, FileMenuSelection? Files = null,
    int? LineNumber = null, int? ColumnNumber = null);
internal sealed record CapturedScriptContext(ScriptContext Context, ScriptReferenceMetadata References);

/// <summary>Capture UI selection before the first read; materialize repository facts before the first prompt.</summary>
internal static class ScriptHostContext
{
    internal static async Task<CapturedScriptContext> CaptureAsync(GitRepository repository, ScriptHostSelection selection, CancellationToken token)
    {
        using IDisposable operation = repository.BeginOperation();
        GitExecutionEnvironment environment = repository.ExecutionEnvironment;
        GitStatus status = await repository.GetStatusAsync(token);
        IReadOnlyList<GitReference> references = (await repository.GetReferencesAsync(token))
            .Where(reference => reference.IsBranch || reference.IsRemote || reference.IsTag).ToArray();
        IReadOnlyList<GitRemote> remotes = await repository.GetRemotesAsync(token);
        IReadOnlyList<KeyValuePair<string, string>> configuration = await repository.GetConfigAsync("^branch\\..*\\.remote$", cancellationToken: token);
        Dictionary<string, string> branchRemotes = new(StringComparer.Ordinal);
        foreach (var entry in configuration)
        { branchRemotes[entry.Key[7..^7]] = entry.Value; }
        GitCommit? selected = selection.Selected;
        string? selectedHash = selected?.Hash;
        Dictionary<string, ScriptRevision> details = new(StringComparer.Ordinal);
        if (selected?.Sha1Id is { IsArtificial: true })
        {
            var identity = await repository.GetConfigAsync("^user\\.(name|email)$", cancellationToken: token);
            string name = identity.LastOrDefault(entry => entry.Key == "user.name").Value ?? "";
            details[selected.Hash] = new(selected.Hash, selected.Subject, Author: name, Committer: name,
                AuthorDate: DateTime.MaxValue, CommitDate: DateTime.MaxValue);
        }
        foreach (string hash in new[] { selectedHash, status.Head }.OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(hash => !details.ContainsKey(hash)))
        {
            GitCommitDetails commit = await repository.GetCommitDetailsAsync(hash, includeNotes: false, cancellationToken: token);
            details[hash] = new(commit.Hash, commit.Subject, commit.Message, commit.Author.Name, commit.Committer.Name,
                ScriptRevision.FromUnixTimeSeconds(commit.Author.Date.ToUnixTimeSeconds()), ScriptRevision.FromUnixTimeSeconds(commit.Committer.Date.ToUnixTimeSeconds()));
        }
        ScriptRevision? current = status.Head is string head ? details.GetValueOrDefault(head) : null;
        ScriptRevision? selectedRevision = selectedHash is null ? null : details.GetValueOrDefault(selectedHash);
        GitReference[] selectedReferences = references.Where(reference => (reference.RevisionHash ?? reference.Hash) == selectedHash).ToArray();
        GitReference[] currentReferences = references.Where(reference => (reference.RevisionHash ?? reference.Hash) == status.Head).ToArray();
        string RemoteOf(GitReference reference) => remotes.Where(remote => reference.ShortName.StartsWith(remote.Name + "/", StringComparison.Ordinal))
            .OrderByDescending(remote => remote.Name.Length).FirstOrDefault()?.Name ?? "";
        ScriptReference Convert(GitReference reference) => new(reference.ShortName,
            reference.IsBranch ? ScriptReferenceKind.LocalBranch : reference.IsTag ? ScriptReferenceKind.Tag : ScriptReferenceKind.RemoteBranch,
            reference.IsRemote ? RemoteOf(reference) : "");
        string branch = status.IsDetached ? "" : status.Branch ?? "";
        ScriptContext context = new()
        {
            WorkingDir = Path.EndsInDirectorySeparator(repository.Path) ? repository.Path : repository.Path + Path.DirectorySeparatorChar,
            RepoName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)), HasRevisionGrid = selection.HasRevisionGrid,
            SelectedRevision = selectedRevision,
            SelectedRevisions = (selection.SelectedCommits ?? []).Select(commit => details.GetValueOrDefault(commit.Hash)
                ?? new ScriptRevision(commit.Hash, commit.Subject, commit.Body, commit.AuthorName, AuthorDate: ScriptRevision.FromUnixTimeSeconds(commit.AuthoredAt.ToUnixTimeSeconds()))).ToArray(),
            SelectedReferences = selectedReferences.Select(Convert).ToArray(), CurrentRevision = current,
            CurrentReferences = currentReferences.Select(Convert).ToArray(), CurrentBranch = branch,
            CurrentRemote = branchRemotes.GetValueOrDefault(branch) ?? "", BranchRemotes = branchRemotes,
            RemoteUrls = remotes.ToDictionary(remote => remote.Name, remote => remote.FetchUrls.FirstOrDefault() ?? "", StringComparer.Ordinal),
            SelectedFolder = selection.Files?.Folder, SelectedRelativePaths = selection.Files?.Paths ?? [],
            LineNumber = selection.LineNumber, ColumnNumber = selection.ColumnNumber,
            GitExecutable = environment.Executable, ExecutionEnvironment = environment,
            ApplicationExecutable = Environment.ProcessPath ?? "gitextensions"
        };
        return new(context.Snapshot(), new(selectedReferences, currentReferences));
    }
}
