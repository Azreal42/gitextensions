using System.Globalization;
using System.Runtime.ExceptionServices;

namespace GitExtensions.Linux.Desktop;

public sealed partial class ForkCloneDialog
{
    internal GitCloneOptions ReadCloneOptions()
    {
        decimal depth = _depth.Value ?? 0;
        if (depth < 0 || depth > 999 || decimal.Truncate(depth) != depth) { throw new ArgumentException(T("Clone depth must be an integer between 0 and 999.")); }
        return new(RecurseSubmodules: false, Depth: depth == 0 ? null : decimal.ToInt32(depth), SingleBranch: null);
    }

    internal static string CloneDestination(string? destination, string? subdirectory)
    {
        string directory = AbsoluteDirectory(destination); string child = subdirectory ?? "";
        if (Path.IsPathRooted(child) || child.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        { throw new ArgumentException("Subdirectory must remain inside the destination folder."); }
        return Path.GetFullPath(Path.Combine(directory, child));
    }

    private string CloneUrl(GitHubHostedRepository repository)
    {
        string url = _protocol.SelectedIndex == 0 ? repository.SshUrl : repository.CloneUrl;
        if (!_client.Settings.TryParseRemote(url, out GitHubRepositoryId? identity)
            || !identity.Owner.Equals(repository.Owner, StringComparison.OrdinalIgnoreCase) || !identity.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException(T("The clone URL does not match the selected GitHub repository and host.")); }
        return url;
    }
    private static bool SameRepository(GitHubRepositoryId left, GitHubRepositoryId right)
        => left.Owner.Equals(right.Owner, StringComparison.OrdinalIgnoreCase) && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase);

    private Task CloneAsync() => RunOwned("Clone", async token =>
    {
        if (_selected is not { } selected) { return; }
        using IDisposable execution = GitEnvironment.BeginOperation();
        GitRunner runner = GitEnvironment.Current.CreateRunner();
        LastProcess = null; CloneWarning = null;
        string directory = AbsoluteDirectory(_destination.Text), path = CloneDestination(_destination.Text, _subdirectory.Text);
        string source = CloneUrl(selected); GitCloneOptions options = ReadCloneOptions();
        string remoteName = (_upstream.Text ?? "").Trim(); string? parentUrl = null;
        if (remoteName.Length > 0)
        {
            if (remoteName.StartsWith('-') || remoteName.Any(char.IsControl)) { throw new ArgumentException(T("Enter a valid upstream remote name.")); }
            if (_parent is null) { await LoadParentAsync(selected, token); }
            if (_parent is null || _selected?.Parent is not { } parent || !SameRepository(parent, _parent.Identity)) { throw new InvalidOperationException(T("The parent repository could not be loaded. Clear the upstream name to clone without adding it.")); }
            parentUrl = CloneUrl(_parent);
        }
        EnsureCurrent(token);
        string description = "git clone -v --progress" + (options.Depth is int depth ? " --depth=" + depth.ToString(CultureInfo.InvariantCulture) : "") + " — " + source;
        LastProcess = await GitProcessDialog.ShowAsync(this, directory, description, true,
            (ct, output) => GitRepository.CloneCommandAsync(source, path, options, ct, output, runner), token);
        if (LastProcess.WasCancelled || LastProcess.OwnerCloseRequested || LastProcess.WorkflowStopRequested) { throw new OperationCanceledException(token); }
        EnsureCurrent(token);
        if (LastProcess.CompletionError is Exception completionError) { ExceptionDispatchInfo.Capture(completionError).Throw(); }
        if (LastProcess.Error is Exception error) { ExceptionDispatchInfo.Capture(error).Throw(); }
        if (LastProcess.Result?.Success != true) { throw new InvalidOperationException(T("No native Git result was returned.")); }
        // Discovery and upstream configuration are never reported as clone transfer output.
        GitRepository cloned = await GitRepository.OpenAsync(path, token, runner); EnsureCurrent(token);
        if (parentUrl is not null)
        {
            try { await cloned.AddRemoteAsync(remoteName, parentUrl, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure) when (failure is GitCommandException or ArgumentException or IOException or UnauthorizedAccessException)
            { CloneWarning = T("The repository was cloned, but its upstream remote could not be added.") + "\n" + failure.Message; }
        }
        EnsureCurrent(token);
        ClonePreferencesStore preferences = new(); preferences.Save(preferences.Load() with { Destination = directory });
        RepositoryHistoryState history = LoadHistory(); new CloneSourcesStore().Visit(source, history.HistorySize);
        try { new RepositoryHistoryStore().Save(history.Visit(cloned.Path)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        Complete(cloned);
    });

    private static RepositoryHistoryState LoadHistory()
    {
        try { return new RepositoryHistoryStore().Load(new PreferencesStore().Load().RecentRepositories); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(); }
    }
}
