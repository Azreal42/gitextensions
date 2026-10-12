using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original FormSubmodules Remove workflow; it retains module files and repositories.</summary>
public sealed class SubmoduleRemovalDialog : RepositoryDialog
{
    private readonly ListBox _modules = new() { Name = "RemovalSubmodules", SelectionMode = SelectionMode.Single };
    private readonly TextBox _name = Detail("RemovalSubmoduleName");
    private readonly TextBox _remote = Detail("RemovalSubmoduleRemotePath");
    private readonly TextBox _path = Detail("RemovalSubmoduleLocalPath");
    private readonly TextBox _commit = Detail("RemovalSubmoduleCommit");
    private readonly TextBox _branch = Detail("RemovalSubmoduleBranch");
    private readonly TextBox _state = Detail("RemovalSubmoduleStatus");
    private readonly Button _remove;
    private readonly GitSubmodule? _initialSelection;
    private bool _confirming;
    private readonly Dictionary<string, (GitSubmodule Module, string? Branch)> _details = new(StringComparer.Ordinal);

    public SubmoduleRemovalDialog(GitRepository repository, GitSubmodule? selected = null) : base(repository, "Submodules")
    {
        _initialSelection = selected;
        Name = "SubmoduleRemoval";
        Title = Original("$this", "Submodules");
        Width = 782; Height = 372; MinWidth = 650; MinHeight = 320;
        _remove = Action("RemoveSubmodule", Original("RemoveSubmodule", "Remove"), RemoveAsync);
        _remove.Height = 25; _remove.MinWidth = 90;
        Button close = new() { Name = "CloseSubmoduleRemoval", Content = T("Close"), IsCancel = true, Height = 25, MinWidth = 75 };
        close.Click += (_, _) => Close();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(10) };
        buttons.Children.Add(_remove); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Grid body = new() { ColumnDefinitions = new("222,*"), ColumnSpacing = 10, Margin = new Thickness(10, 10, 10, 0) };
        DockPanel left = new();
        Grid header = new() { ColumnDefinitions = new("*,90"), Margin = new Thickness(6, 0, 6, 3) };
        header.Children.Add(new TextBlock { Name = "RemovalNameHeader", Text = Translations.Translate("FormSubmodules", "nameDataGridViewTextBoxColumn", "HeaderText", "Name") });
        TextBlock statusHeader = new() { Name = "RemovalStatusHeader", Text = Translations.Translate("FormSubmodules", "Status", "HeaderText", "Status") }; Grid.SetColumn(statusHeader, 1); header.Children.Add(statusHeader);
        DockPanel.SetDock(header, Dock.Top); left.Children.Add(header); left.Children.Add(_modules); body.Children.Add(left);
        _modules.ItemTemplate = new FuncDataTemplate<GitSubmoduleRemovalSelection>((item, _) =>
        {
            Grid row = new() { ColumnDefinitions = new("*,90") };
            row.Children.Add(new TextBlock { Text = item?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            TextBlock state = new() { Text = item is null ? "" : OriginalStatus(item), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(state, 1); row.Children.Add(state); return row;
        });
        Grid details = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,25,25,25,25,25,25"), RowSpacing = 7 };
        TextBlock caption = new() { Text = Original("groupBox1", "Details"), FontWeight = FontWeight.Bold };
        Grid.SetColumnSpan(caption, 2); details.Children.Add(caption);
        (string Key, string Label, TextBox Input)[] fields = [("label1", "Name", _name), ("label2", "Remote path", _remote), ("label3", "Local path", _path), ("label4", "Commit", _commit), ("label5", "Branch", _branch), ("label6", "Status", _state)];
        for (int i = 0; i < fields.Length; i++)
        {
            TextBlock label = new() { Text = Original(fields[i].Key, fields[i].Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(label, i + 1); details.Children.Add(label);
            Grid.SetRow(fields[i].Input, i + 1); Grid.SetColumn(fields[i].Input, 1); details.Children.Add(fields[i].Input);
        }
        Grid.SetColumn(details, 1); body.Children.Add(details); Root.Children.Add(body);
        _modules.SelectionChanged += (_, _) => { ShowSelection(); UpdateActions(); };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Submodules", async token =>
    {
        IReadOnlyList<GitSubmoduleRemovalSelection> entries = await Repository.GetSubmoduleRemovalSelectionsAsync(token);
        await ReadDetailsAsync(entries, token);
        _modules.ItemsSource = entries;
        // Never substitute another module when a requested selection vanished.
        _modules.SelectedItem = _initialSelection is null ? entries.FirstOrDefault() : entries.SingleOrDefault(item =>
            item.Name == _initialSelection.Name && item.Path == _initialSelection.Path && item.IndexHead == _initialSelection.ExpectedHead);
        ShowSelection();
        Status.Text = T("Submodules");
    });

    private async Task RemoveAsync()
    {
        if (IsBusy || _confirming || _modules.SelectedItem is not GitSubmoduleRemovalSelection selected) { return; }
        _confirming = true; UpdateActions();
        try
        {
            SubmoduleRemovalConfirmation confirmation = new(Translations, selected) { RequestedThemeVariant = ActualThemeVariant };
            if (!await confirmation.ShowDialog<bool>(this) || !IsVisible) { return; }
            await RunAsync(Original("RemoveSubmodule", "Remove"), async token =>
            {
                await Repository.RemoveSubmoduleAsync(selected, token);
                MarkRepositoryChanged();
                // Query indexed registrations, so the intentionally retained last .gitmodules file does not resurrect the row.
                IReadOnlyList<GitSubmoduleRemovalSelection> entries = await Repository.GetSubmoduleRemovalSelectionsAsync(token);
                await ReadDetailsAsync(entries, token);
                _modules.ItemsSource = entries;
                _modules.SelectedItem = null; ShowSelection();
                Status.Text = T("Submodules");
            });
        }
        finally { _confirming = false; UpdateActions(); }
    }

    protected override void UpdateActions() => _remove.IsEnabled = !IsBusy && !_confirming && _modules.SelectedItem is GitSubmoduleRemovalSelection;
    private string Original(string key, string source) => Translations.Translate("FormSubmodules", key, "Text", source);
    private static TextBox Detail(string name) => new() { Name = name, IsReadOnly = true, Height = 25 };
    private void ShowSelection()
    {
        GitSubmoduleRemovalSelection? item = _modules.SelectedItem as GitSubmoduleRemovalSelection;
        _name.Text = item?.Name; _remote.Text = item?.Url; _path.Text = item?.Path;
        _commit.Text = item is null ? null : _details.TryGetValue(item.Path, out var details) ? details.Module.Head ?? item.IndexHead : item.IndexHead;
        _branch.Text = item is not null && _details.TryGetValue(item.Path, out var branch) ? branch.Branch : null;
        _state.Text = item is null ? "" : OriginalStatus(item);
    }

    private string OriginalStatus(GitSubmoduleRemovalSelection item)
    {
        // These are literal dynamic GitSubmoduleInfo.Status values in Windows, without XLF entries.
        if (!item.IsInitialized) { return "Not initialized"; }
        return _details.TryGetValue(item.Path, out var details) && details.Module.Head == item.IndexHead ? "Up-to-date" : "Modified";
    }

    private async Task ReadDetailsAsync(IReadOnlyList<GitSubmoduleRemovalSelection> entries, CancellationToken token)
    {
        _details.Clear();
        IReadOnlyList<GitSubmodule> modules = await Repository.GetSubmodulesAsync(token);
        foreach (GitSubmoduleRemovalSelection item in entries)
        {
            GitSubmodule? module = modules.SingleOrDefault(module => module.Name == item.Name && module.Path == item.Path && module.ExpectedHead == item.IndexHead);
            if (module is null) { continue; }
            string? branch = null;
            if (module.IsInitialized && module.Head is not null)
            {
                GitResult result = await new GitRunner().RunAsync(System.IO.Path.Combine(Repository.Path, module.Path),
                    ["--no-optional-locks", "describe", "--all", "--always", module.Head], token);
                if (result.Success)
                {
                    branch = result.StandardOutput.TrimEnd('\r', '\n');
                    // git submodule status (and the Windows Branch column) omits the tags/ qualifier.
                    if (branch.StartsWith("tags/", StringComparison.Ordinal)) { branch = branch[5..]; }
                }
            }
            _details.Add(item.Path, (module, branch));
        }
    }
}

internal sealed class SubmoduleRemovalConfirmation : Window
{
    internal SubmoduleRemovalConfirmation(TranslationCatalog translations, GitSubmoduleRemovalSelection selection)
    {
        Name = "ConfirmSubmoduleRemoval";
        Title = translations.Translate("FormSubmodules", "_removeSelectedSubmoduleCaption", "Text", "Remove");
        Width = 520; CanResize = false; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel content = new() { Margin = new Thickness(12), Spacing = 8 };
        content.Children.Add(new TextBlock { Name = "SubmoduleRemovalQuestion", TextWrapping = TextWrapping.Wrap,
            Text = translations.Translate("FormSubmodules", "_removeSelectedSubmodule", "Text", "Are you sure you want remove the selected submodule?") });
        content.Children.Add(new TextBox { Name = "ConfirmedSubmodulePath", Text = selection.Path, IsReadOnly = true });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button yes = new() { Name = "ConfirmRemoveSubmodule", Content = translations.Translate("Yes"), MinWidth = 75, Height = 25, IsDefault = true };
        Button no = new() { Name = "KeepSubmodule", Content = translations.Translate("No"), MinWidth = 75, Height = 25, IsCancel = true };
        yes.Click += (_, _) => Close(true); no.Click += (_, _) => Close(false);
        buttons.Children.Add(yes); buttons.Children.Add(no); content.Children.Add(buttons); Content = content;
    }
}
