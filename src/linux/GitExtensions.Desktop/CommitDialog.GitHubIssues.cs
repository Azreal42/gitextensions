using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private sealed record GitHubIssueMenuEntry(CommitMessageTemplate Template, bool Notice = false);
    private GitHubHostingPreferencesStore _githubIssueSettings = null!;
    private IReadOnlyList<GitHubIssueMenuEntry> _githubIssueTemplates = [];
    private CancellationTokenSource? _githubIssueLifetime;
    private Task _githubIssueWork = Task.CompletedTask;
    private bool _githubIssueStarted, _githubIssueClosing;
    internal Task GitHubIssueCompletion => _githubIssueWork;
    internal Func<GitHubHostingSettings, GitHubHostingCredentials, IGitHubAssignedIssuesClient> GitHubIssueClientFactory { get; set; }
        = (settings, credentials) => new GitHubHostingClient(settings, credentials.ToDraft());

    private void InitializeGitHubIssues(string profileRoot)
    {
        _githubIssueSettings = new(profileRoot);
        // The original PreCommit surrounds the window opening, not the individual git commit write.
        Opened += (_, _) =>
        {
            if (_githubIssueStarted || _githubIssueClosing || _lifetime.IsCancellationRequested) { return; }
            _githubIssueStarted = true;
            _githubIssueLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _githubIssueWork = ReadGitHubIssuesAsync(_githubIssueLifetime.Token);
        };
        Closed += (_, _) =>
        {
            _githubIssueClosing = true;
            CancellationTokenSource? lifetime = _githubIssueLifetime; _githubIssueLifetime = null;
            lifetime?.Cancel(); _githubIssueTemplates = []; lifetime?.Dispose();
        };
    }

    private async Task ReadGitHubIssuesAsync(CancellationToken token)
    {
        IReadOnlyList<GitHubIssueMenuEntry> entries;
        try
        {
            IReadOnlyDictionary<string, string?> values = _githubIssueSettings.Load();
            GitHubIssueHelperSettings helper = GitHubIssueHelperSettings.FromPluginSettings(values);
            if (!helper.Enabled) { return; }
            GitHubHostingSettings settings = GitHubHostingSettings.FromPluginSettings(values);
            GitHubHostingCredentials credentials = settings.CredentialsFromPluginSettings(values);
            if (credentials.Type == GitUIPluginInterfaces.BuildServerIntegration.BuildServerCredentialsType.Guest)
            {
                entries = [IssueNotice("_noTokenError", "No GitHub personal access token (PAT) defined")];
            }
            else
            {
                using IDisposable environment = _repository.BeginOperation();
                IReadOnlyList<GitRemote> remotes = await _repository.GetRemotesAsync(token);
                GitHubRepositoryId[] repositories = remotes.Select(remote => settings.TryParseRemote(remote.FetchUrls.FirstOrDefault() ?? "", out GitHubRepositoryId? identity) ? identity : null)
                    .OfType<GitHubRepositoryId>().ToArray();
                token.ThrowIfCancellationRequested(); if (repositories.Length == 0) { return; }
                using IGitHubAssignedIssuesClient client = GitHubIssueClientFactory(settings, credentials);
                GitHubHostingPage<GitHubAssignedIssue> page = await client.GetAssignedIssuesAsync(token);
                token.ThrowIfCancellationRequested();
                List<GitHubIssueMenuEntry> list = GitHubIssueCommitTemplates.Create(page.Items, repositories, helper.MaxCount)
                    .Select(template => new GitHubIssueMenuEntry(new(template.Name, template.Text))).ToList();
                if (page.Items.Count == 0) { list.Add(IssueNotice("_noAssignedIssues", "No assigned GitHub issues found")); }
                if (page.Truncated) { list.Add(IssueNotice("_assignedIssuesTruncated", "More assigned GitHub issues may be available; the response limit was reached.")); }
                entries = list.AsReadOnly();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (GitHubHostingException error)
        { entries = [new(new(GitHubIssueText("_assignedIssuesError", "Could not load assigned GitHub issues") + ": " + error.Message, ""), true)]; }
        catch (Exception)
        { entries = [IssueNotice("_assignedIssuesError", "Could not load assigned GitHub issues. Check GitHub settings and the network connection.")]; }
        if (token.IsCancellationRequested || _githubIssueClosing || _closeWhenIdle || _lifetime.IsCancellationRequested || !IsVisible) { return; }
        // A menu already open keeps its snapshot and selection. The next opening reads this private list.
        _githubIssueTemplates = entries;
    }

    private GitHubIssueMenuEntry IssueNotice(string item, string text) => new(new(GitHubIssueText(item, text), ""), true);
    private string GitHubIssueText(string item, string text) => _translations.Translate("GitHub3Plugin", item, "Text", text);
    private Task BeginGitHubIssueClose()
    { _githubIssueClosing = true; _githubIssueLifetime?.Cancel(); return _githubIssueWork; }
}
