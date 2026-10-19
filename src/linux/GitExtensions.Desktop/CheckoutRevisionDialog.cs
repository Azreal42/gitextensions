using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>The compact original FormCheckoutRevision. The host owns the one existing script/native workflow.</summary>
public sealed class CheckoutRevisionDialog : RepositoryDialog
{
    private readonly CheckoutRevisionExecutor _execute;
    private readonly TextBox _revision = ReferenceDialogControls.Text("CheckoutRevision");
    private readonly CheckBox _force = new() { Name = "ForceCheckoutRevision", Height = 24, Padding = new Thickness(0) };
    private readonly TextBlock _count = new() { Name = "CheckoutRevisionCommitCount", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _choose, _checkout;
    private bool _bare;
    public string? SelectedRevision { get; private set; }
    public bool Force => _force.IsChecked == true;
    public CheckoutRevisionOutcome? Outcome { get; private set; }
    public GitResult? LastNativeResult { get; private set; }
    public bool Succeeded => Outcome?.Succeeded == true;

    public CheckoutRevisionDialog(GitRepository repository, CheckoutRevisionExecutor execute, string? revision = null)
        : base(repository, "Checkout revision")
    {
        ArgumentNullException.ThrowIfNull(execute); _execute = execute;
        Name = "CheckoutRevisionDialog"; Title = Text("$this", "Checkout revision");
        Width = 481; Height = 131; MinWidth = 460; MinHeight = 125; SizeToContent = SizeToContent.Height; CanResize = false;
        _choose = Action("ChooseCheckoutRevisionCommit", "Choose revision", ChooseAsync);
        _choose.Content = ResetDialogControls.Icon("SelectRevision"); _choose.Width = 25; _choose.Height = 24; _choose.Padding = new Thickness(2);
        ToolTip.SetTip(_choose, T("Choose revision"));
        Grid picker = new() { Name = "CheckoutCommitPicker", ColumnDefinitions = new("*,Auto,Auto"), Height = 26 };
        _revision.Margin = new Thickness(0, 2, 0, 0); _revision.MaxLength = 100;
        picker.Children.Add(_revision); Grid.SetColumn(_choose, 1); _choose.Margin = new Thickness(3, 0); picker.Children.Add(_choose);
        Grid.SetColumn(_count, 2); picker.Children.Add(_count);
        _force.Content = Caption("Force", "&Force (reset local changes)");
        Grid fields = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("32,Auto,*") };
        Label label = new() { Content = Caption("label2", "Checkout this &revision"), Target = _revision, Margin = new Thickness(3, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(label); Grid.SetColumn(picker, 1); fields.Children.Add(picker);
        Grid.SetColumn(_force, 1); Grid.SetRow(_force, 1); fields.Children.Add(_force);
        _checkout = Action("ExecuteCheckoutRevision", "Checkout", ExecuteAsync);
        _checkout.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { ResetDialogControls.Icon("checkout"), Caption("OkCheckout", "&Checkout") } };
        _checkout.IsDefault = true; _checkout.MinWidth = 84; _checkout.Height = 25; _checkout.Padding = new Thickness(6, 0);
        Border buttons = new() { Height = 41, Padding = new Thickness(12, 8), Child = new StackPanel
            { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _checkout } } };
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Root.Children.Add(new Border { Height = 90, Padding = new Thickness(12), Child = fields });
        Track(_revision, _force); CollapseEmptyStatus();
        _revision.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty) { SelectedRevision = null; _count.Text = ""; } };
        Opened += (_, _) => _revision.Focus(); SetRevision(revision);
    }
    private string Text(string item, string source) => Translations.Translate("FormCheckoutRevision", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormCheckoutRevision", item, "Text", source) };
    public void SetRevision(string? revision)
    {
        if (IsBusy) { throw new InvalidOperationException("Wait for the current checkout dialog operation to finish."); }
        _revision.Text = revision ?? ""; SelectedRevision = null; _count.Text = "";
    }
    public override Task InitializeAsync() => RunAsync("Checkout revision", async token =>
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        if (!_bare && !string.IsNullOrWhiteSpace(_revision.Text)) { await ResolveSelectionAsync(token); }
        Status.Text = _bare ? T("Open a worktree to check out a revision.") : "";
    });
    private async Task<string> ResolveSelectionAsync(CancellationToken token)
    {
        string text = (_revision.Text ?? "").Trim();
        if (text.Length == 0 || text.Length is 40 or 64 && text.All(c => c == '0'))
        { throw new InvalidOperationException(Text("_noRevisionSelectedMsgBox", "Select 1 revision to checkout.")); }
        string resolved = await Repository.ResolveRevisionAsync(text, token);
        if (!CheckoutRevisionRequest.IsActualCommit(resolved)) { throw new InvalidOperationException(Text("_noRevisionSelectedMsgBox", "Select 1 revision to checkout.")); }
        // CommitPickerSmallControl shows ObjectId.ToShortString() (eight characters), while keeping the actual commit.
        _revision.Text = resolved[..8]; SelectedRevision = resolved;
        ToolTip.SetTip(_revision, await ReferenceDialogControls.SummaryAsync(Repository, resolved, token));
        GitStatus status = await Repository.GetStatusAsync(token);
        if (status.Head is not null)
        {
            GitComparisonCommitCounts? count = await Repository.GetComparisonCommitCountsAsync(new(status.Head), new(resolved), token);
            _count.Text = count is null ? "" : count.BaseOnly == 0 && count.CompareOnly == 0 ? "=" : $"(+{count.BaseOnly}-{count.CompareOnly})";
        }
        return resolved;
    }
    private Task ChooseAsync() => RunAsync("Choose revision", async token =>
    {
        GitCommit? commit = await RevertRevisionPicker.ShowAsync(this, Repository, SelectedRevision, token);
        if (commit is null) { Status.Text = ""; return; }
        _revision.Text = commit.Hash; await ResolveSelectionAsync(token); Status.Text = "";
    });
    public async Task ExecuteAsync()
    {
        if (IsBusy || _bare || Succeeded) { return; }
        bool force = Force; Outcome = null; LastNativeResult = null;
        await RunAsync("Checkout revision", async token =>
        {
            string commit = await ResolveSelectionAsync(token);
            try
            {
                Outcome = await _execute(this, new(commit, force), token);
                LastNativeResult = Outcome.NativeResult;
                Status.Text = Outcome.BeforeVetoed ? T("Operation cancelled") : "";
            }
            finally { MarkRepositoryChanged(); }
        });
        if (LastException is GitCommandException native && native.Arguments.FirstOrDefault() == "switch") { LastNativeResult = native.Result; }
        if (Succeeded && IsVisible) { Close(true); }
    }
    protected override void UpdateActions()
    {
        if (_checkout is null || _choose is null) { return; }
        _checkout.IsEnabled = _choose.IsEnabled = !IsBusy && !_bare && !Succeeded;
        _force.IsEnabled = _revision.IsEnabled = !IsBusy && !_bare && !Succeeded;
    }
}
