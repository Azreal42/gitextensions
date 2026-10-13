using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal sealed class RevertRevisionPicker : RepositoryDialog
{
    private readonly ExportRevisionList _revisions = new("RevertChooseRevision", false);
    private readonly TextBox _search = new() { Name = "RevertRevisionSearch", Height = 24 };
    private readonly string? _initial;
    private readonly CancellationToken _token;
    private RevertRevisionPicker(GitRepository repository, string? initial, CancellationToken token) : base(repository, "Choose revision")
    {
        _initial = initial; _token = token; Name = "RevertRevisionPicker"; Width = 780; Height = 420; MinWidth = 620; MinHeight = 300;
        Button select = Action("ChooseRevertCommit", "OK", () => { if (_revisions.Rows.SelectedItem is GitCommit commit) { Close(commit); } return Task.CompletedTask; }); select.IsDefault = true;
        Button cancel = new() { Name = "CancelRevertChoice", Content = T("Cancel"), IsCancel = true }; cancel.Click += (_, _) => Close(null);
        StackPanel buttons = ExportDialogControls.Buttons(select, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Grid search = ExportDialogControls.Fields(); ExportDialogControls.Field(search, T("Message"), _search, Action("SearchRevertRevision", "Search", InitializeAsync));
        search.Margin = new Thickness(10, 6); DockPanel.SetDock(search, Dock.Top); Root.Children.Add(search);
        _revisions.Margin = new Thickness(10, 0, 10, 6); Root.Children.Add(_revisions); Track(_search, _revisions);
    }
    public override Task InitializeAsync() => RunAsync("Choose revision", async token =>
    {
        IReadOnlyList<GitCommit> commits = await Repository.GetLogAsync(new GitLogQuery(Count: 500, Message: string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text), token);
        _revisions.SetData(commits, await Repository.GetReferencesAsync(token), (await Repository.GetStatusAsync(token)).Head);
        _revisions.Rows.SelectedItem = commits.FirstOrDefault(commit => commit.Hash == _initial) ?? commits.FirstOrDefault(); Status.Text = T("Choose revision");
    }, cancellationToken: _token);
    internal static async Task<GitCommit?> ShowAsync(Window owner, GitRepository repository, string? initial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RevertRevisionPicker picker = new(repository, initial, token) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (picker.IsVisible) { picker.Close(null); } }));
        await picker.InitializeAsync(); token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return null; }
        if (picker.LastError is not null) { throw picker.LastException ?? new InvalidOperationException(picker.LastError); }
        GitCommit? selected = await picker.ShowDialog<GitCommit?>(owner); token.ThrowIfCancellationRequested(); return selected;
    }
}
