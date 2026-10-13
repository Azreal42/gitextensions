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
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly bool _userAbleToChangeRevision, _couldBeOrphan;
    private readonly string _initialRevision;
    private bool _unborn;
    private bool _bare;
    private bool _hasTargetCommit;
    public string? CreatedBranch { get; private set; }

    public CreateBranchDialog(GitRepository repository, string revision = "HEAD", string? name = null, bool autoNormalize = true, string replacement = "_",
        bool checkoutAfterCreation = true, bool userAbleToChangeRevision = true, bool couldBeOrphan = true) : base(repository, "Create branch")
    {
        _autoNormalize = autoNormalize;
        _replacement = replacement;
        _initialRevision = revision; _userAbleToChangeRevision = userAbleToChangeRevision; _couldBeOrphan = couldBeOrphan;
        if (autoNormalize) { GitBranchNaming.Normalize("", replacement); }
        Width = 600;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        _name.Text = name;
        _revision.Text = revision;
        _revision.IsReadOnly = !userAbleToChangeRevision;
        _checkout.IsChecked = checkoutAfterCreation;
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
        _orphan.IsCheckedChanged += (_, _) => { if (_couldBeOrphan && _orphan.IsChecked == true) { _checkout.IsChecked = true; } else { _clear.IsChecked = false; } UpdateActions(); };
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        _name.LostFocus += (_, _) => NormalizeName();
        UpdateActions();
        Opened += (_, _) => _name.Focus();
    }

    public override Task InitializeAsync() => RunAsync("Create branch", async token =>
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        _unborn = (await Repository.GetStatusAsync(token)).IsUnborn;
        // An unborn HEAD can still have a real dangling commit selected for recovery.
        _hasTargetCommit = !_unborn;
        if (_unborn && ReadOptions().Revision != "HEAD")
        { await Repository.ResolveRevisionAsync(ReadOptions().Revision, token); _hasTargetCommit = true; }
        if (_bare) { _checkout.IsChecked = false; _orphan.IsChecked = false; }
        else if (_unborn && !_hasTargetCommit && _couldBeOrphan) { _orphan.IsChecked = true; }
        _summary.Text = _unborn && !_hasTargetCommit && _couldBeOrphan ? T("Creating orphan branch (repository has no commits)") : await ReferenceDialogControls.SummaryAsync(Repository, ReadOptions().Revision, token);
        Status.Text = T("Enter branch name.");
    });

    public GitCreateBranchOptions ReadOptions() => new((_name.Text ?? "").Trim(), _userAbleToChangeRevision ? _revision.Text ?? "HEAD" : _initialRevision,
        _checkout.IsChecked == true, _couldBeOrphan && _orphan.IsChecked == true, _couldBeOrphan && _clear.IsChecked == true);

    private void NormalizeName()
    {
        int caret = _name.CaretIndex;
        _name.Text = GitBranchNaming.NormalizeForInput(_name.Text, _autoNormalize, _replacement);
        _name.CaretIndex = Math.Min(caret, _name.Text.Length);
    }

    private async Task ChooseAsync()
    {
        if (!_userAbleToChangeRevision || IsBusy) { return; }
        GitCommit? selected = await ExportRevisionPicker.ShowAsync(this, Repository);
        if (selected is null) { return; }
        _revision.Text = selected.Hash;
        await RunAsync("Revision", async token => _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, selected.Hash, token));
    }

    private async Task CreateAsync()
    {
        if (IsBusy || !IsVisible) { return; }
        _create.Focus();
        NormalizeName();
        GitCreateBranchOptions options = ReadOptions();
        if (options.ClearWorkingTree && !await Dialogs.ConfirmAsync(this, T("Clear working directory and index"), T("Remove tracked files and the index while creating the orphan branch? Untracked files are preserved."))) { return; }
        if (!IsVisible) { return; }
        await RunAsync("Create branch", async token =>
        {
            if (!_unborn || _hasTargetCommit) { _summary.Text = await ReferenceDialogControls.SummaryAsync(Repository, options.Revision, token); }
            try { await Repository.CreateBranchAsync(options, token); CreatedBranch = options.Name; }
            finally { MarkRepositoryChanged(); }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        _create.IsEnabled = !IsBusy && !string.IsNullOrWhiteSpace(_name.Text) && !(_bare && _unborn && !_hasTargetCommit);
        _revision.IsEnabled = _choose.IsEnabled = !IsBusy && (!_unborn || _hasTargetCommit) && _userAbleToChangeRevision;
        _orphan.IsEnabled = !IsBusy && !_bare && (!_unborn || _hasTargetCommit) && _couldBeOrphan;
        _checkout.IsEnabled = !IsBusy && !_bare && (!_couldBeOrphan || _orphan.IsChecked != true);
        _clear.IsEnabled = !IsBusy && !_bare && _orphan.IsChecked == true && _couldBeOrphan;
    }
}
