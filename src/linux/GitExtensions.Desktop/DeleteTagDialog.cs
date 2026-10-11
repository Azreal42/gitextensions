using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

public sealed class DeleteTagDialog : RepositoryDialog
{
    private readonly ComboBox _tags = ReferenceDialogControls.Choice("DeleteTagSelection");
    private readonly CheckBox _deleteRemote = new() { Name = "DeleteRemoteTag" };
    private readonly ComboBox _remote = ReferenceDialogControls.Choice("DeleteTagRemote");
    private readonly Button _delete;
    private readonly string? _initial;

    public DeleteTagDialog(GitRepository repository, string? tag = null) : base(repository, "Delete tag")
    {
        _initial = tag;
        Width = 560;
        MinWidth = 470;
        SizeToContent = SizeToContent.Height;
        _deleteRemote.Content = T("Delete tag also from the following remote(s):");
        ReferenceDialogControls.Compact(_deleteRemote);
        ReferenceDialogControls.ReferenceTemplate(_tags);
        _delete = Action("DeleteTag", "Delete", DeleteAsync);
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelDeleteTag", "Cancel", () => { Close(); return Task.CompletedTask; }), _delete);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid fields = ReferenceDialogControls.Fields();
        ReferenceDialogControls.Field(fields, T("Select tag"), _tags);
        ReferenceDialogControls.Field(fields, "", _deleteRemote);
        ReferenceDialogControls.Field(fields, T("Remote"), _remote);
        Root.Children.Add(new StackPanel { Margin = new Thickness(10), Spacing = 8, Children =
        { fields, new TextBlock { Text = T("This will delete the selected tag from the (local) repository.") } } });
        Track(_tags, _deleteRemote, _remote);
        _tags.SelectionChanged += (_, _) => UpdateActions();
        _deleteRemote.IsCheckedChanged += (_, _) => UpdateActions();
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Delete tag", async token =>
    {
        GitReference[] tags = (await Repository.GetReferencesAsync(token)).Where(reference => reference.IsTag).ToArray();
        _tags.ItemsSource = tags;
        _tags.SelectedItem = tags.FirstOrDefault(tag => tag.ShortName == _initial || tag.Name == _initial) ?? tags.FirstOrDefault();
        _remote.ItemsSource = (await Repository.GetRemotesAsync(token)).Select(remote => remote.Name).ToArray();
        _remote.SelectedIndex = _remote.Items.Count > 0 ? 0 : -1;
        Status.Text = T("Select tag");
    });

    private async Task DeleteAsync()
    {
        if (_tags.SelectedItem is not GitReference selected) { return; }
        string? remote = _deleteRemote.IsChecked == true ? _remote.SelectedItem as string : null;
        if (remote is not null && !await Dialogs.ConfirmAsync(this, T("Delete remote tag"), selected.ShortName + " → " + remote)) { return; }
        await RunAsync("Delete tag", async token =>
        {
            try { await Repository.DeleteTagsAsync([selected.ShortName], remote, token); }
            finally { MarkRepositoryChanged(); }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        _remote.IsEnabled = !IsBusy && _deleteRemote.IsChecked == true;
        _deleteRemote.IsEnabled = !IsBusy && _remote.Items.Count > 0;
        _delete.IsEnabled = !IsBusy && _tags.SelectedItem is not null && (_deleteRemote.IsChecked != true || _remote.SelectedItem is not null);
    }
}
