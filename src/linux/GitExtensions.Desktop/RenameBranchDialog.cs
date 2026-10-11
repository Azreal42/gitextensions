using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

public sealed class RenameBranchDialog : RepositoryDialog
{
    private readonly string _oldName;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly TextBox _name = ReferenceDialogControls.Text("RenameBranchName");
    private readonly Button _rename;
    public string? RenamedBranch { get; private set; }

    public RenameBranchDialog(GitRepository repository, string branch, bool autoNormalize = true, string replacement = "_") : base(repository, "Rename branch")
    {
        _oldName = branch;
        _autoNormalize = autoNormalize;
        _replacement = replacement;
        if (autoNormalize) { GitBranchNaming.Normalize("", replacement); }
        Width = 500; MinWidth = 400; SizeToContent = SizeToContent.Height;
        _name.Text = branch;
        _rename = Action("RenameBranch", "Rename", RenameAsync);
        _rename.IsDefault = true;
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6) };
        TextBlock label = new() { Text = T("New name"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        Grid.SetColumn(_name, 1); row.Children.Add(_name);
        Grid.SetColumn(_rename, 2); _rename.Margin = new Thickness(6, 0, 0, 0); row.Children.Add(_rename);
        Root.Children.Add(row);
        Track(_name);
        _name.LostFocus += (_, _) => NormalizeName();
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Rename branch", async token =>
    {
        GitStatus status = await Repository.GetStatusAsync(token);
        if (!(await Repository.GetReferencesAsync(token)).Any(reference => reference.IsBranch && reference.ShortName == _oldName) && !(status.IsUnborn && status.Branch == _oldName))
        { throw new ArgumentException("Select an existing local branch.", nameof(_oldName)); }
        Status.Text = T("Rename branch") + ": " + _oldName;
    });

    private void NormalizeName()
    {
        int caret = _name.CaretIndex;
        _name.Text = GitBranchNaming.NormalizeForInput(_name.Text, _autoNormalize, _replacement);
        _name.CaretIndex = Math.Min(caret, _name.Text.Length);
    }

    private Task RenameAsync()
    {
        _rename.Focus();
        NormalizeName();
        string name = _name.Text ?? "";
        if (name == _oldName) { Close(); return Task.CompletedTask; }
        if (string.IsNullOrWhiteSpace(name)) { _name.Focus(); return Task.CompletedTask; }
        return RunAsync("Rename branch", async token =>
        {
            await Repository.RenameBranchAsync(_oldName, name, token);
            RenamedBranch = name;
            MarkRepositoryChanged();
            Close();
        });
    }

    protected override void UpdateActions() => _rename.IsEnabled = !IsBusy && !string.IsNullOrWhiteSpace(_name.Text);
}
