using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed class CreateBranchDialog : RepositoryDialog
{
    private readonly TextBox _name = ReferenceDialogControls.Text("CreateBranchName");
    private readonly TextBox _revision = ReferenceDialogControls.Text("CreateBranchRevision");
    private readonly TextBlock _summary = new() { Name = "CreateBranchSummary", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _checkout = new() { Name = "CheckoutAfterCreate", IsChecked = true };
    private readonly CheckBox _orphan = new() { Name = "CreateOrphanBranch" };
    private readonly CheckBox _clear = new() { Name = "ClearOrphanWorkingTree" };
    private readonly Button _choose;
    private readonly Button _create;
    private bool _unborn;
    private bool _bare;
    public string? CreatedBranch { get; private set; }

    public CreateBranchDialog(GitRepository repository, string revision = "HEAD", string? name = null) : base(repository, "Create branch")
    {
        Width = 600;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        _name.Text = name;
        _revision.Text = revision;
        _checkout.Content = T("Checkout after create");
        _orphan.Content = T("Create orphan");
        _clear.Content = T("Clear working directory and index");
        ReferenceDialogControls.Compact(_checkout, _orphan, _clear);
        _choose = Action("ChooseBranchRevision", "Choose…", ChooseAsync);
        _create = Action("CreateBranch", "Create branch", CreateAsync);
        _create.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelCreateBranch", "Cancel", () => { Close(); return Task.CompletedTask; }), _create);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid fields = ReferenceDialogControls.Fields();
        ReferenceDialogControls.Field(fields, T("Branch name"), _name);
        ReferenceDialogControls.Field(fields, T("Create branch at this revision"), _revision, _choose);
        ReferenceDialogControls.Field(fields, "", _checkout);
        StackPanel body = new() { Margin = new Thickness(10), Spacing = 8, Children =
        {
            fields, ClassicChrome.Border(_summary, new Thickness(1), new Thickness(8)),
            ExportDialogControls.Group(T("Orphan"), new StackPanel { Spacing = 4, Children = { _orphan, _clear } })
        } };
        Root.Children.Add(body);
        Track(_name, _revision, _checkout, _orphan, _clear);
        _orphan.IsCheckedChanged += (_, _) => { if (_orphan.IsChecked == true) { _checkout.IsChecked = true; } else { _clear.IsChecked = false; } UpdateActions(); };
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        UpdateActions();
        Opened += (_, _) => _name.Focus();
    }

    public override Task InitializeAsync() => RunAsync("Create branch", async token =>
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        _unborn = (await Repository.GetStatusAsync(token)).IsUnborn;
        if (_bare) { _checkout.IsChecked = false; _orphan.IsChecked = false; }
        else if (_unborn) { _orphan.IsChecked = true; }
        _summary.Text = _unborn ? T("Creating orphan branch (repository has no commits)") : await ReferenceDialogControls.SummaryAsync(Repository, _revision.Text ?? "HEAD", token);
        Status.Text = T("Enter branch name.");
    });

    public GitCreateBranchOptions ReadOptions() => new((_name.Text ?? "").Trim(), _revision.Text ?? "HEAD", _checkout.IsChecked == true, _orphan.IsChecked == true, _clear.IsChecked == true);

    private async Task ChooseAsync()
    {
        GitCommit? selected = await ExportRevisionPicker.ShowAsync(this, Repository);
        if (selected is null) { return; }
        _revision.Text = selected.Hash;
        await RunAsync("Revision", async token => _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, selected.Hash, token));
    }

    private async Task CreateAsync()
    {
        GitCreateBranchOptions options = ReadOptions();
        if (options.ClearWorkingTree && !await Dialogs.ConfirmAsync(this, T("Clear working directory and index"), T("Remove tracked files and the index while creating the orphan branch? Untracked files are preserved."))) { return; }
        await RunAsync("Create branch", async token =>
        {
            if (!_unborn) { _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, options.Revision, token); }
            try { await Repository.CreateBranchAsync(options, token); CreatedBranch = options.Name; }
            finally { MarkRepositoryChanged(); }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        _create.IsEnabled = !IsBusy && !string.IsNullOrWhiteSpace(_name.Text) && !(_bare && _unborn);
        _revision.IsEnabled = _choose.IsEnabled = !IsBusy && !_unborn;
        _orphan.IsEnabled = !IsBusy && !_bare && !_unborn;
        _checkout.IsEnabled = !IsBusy && !_bare && _orphan.IsChecked != true;
        _clear.IsEnabled = !IsBusy && !_bare && _orphan.IsChecked == true;
    }
}
