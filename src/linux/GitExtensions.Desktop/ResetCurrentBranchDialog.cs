using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>The five reset types and hard-reset warning from FormResetCurrentBranch.</summary>
public sealed class ResetCurrentBranchDialog : RepositoryDialog
{
    private readonly string _initialRevision;
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmoduleUpdate;
    private readonly TextBlock _branch = new() { Name = "ResetCurrentBranchInfo", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = ResetDialogControls.Summary("ResetCurrentRevisionSummary");
    private readonly Dictionary<GitResetMode, RadioButton> _modes = [];
    private readonly StackPanel _body;
    private readonly Button _reset;
    private string? _revision;
    private bool _bare, _confirming, _postProcessing;
    public bool Applied { get; private set; }
    public bool SubmodulesUpdated { get; private set; }
    public string? Revision => _revision;
    public GitResetMode Mode => _modes.FirstOrDefault(pair => pair.Value.IsChecked == true).Key;

    public ResetCurrentBranchDialog(GitRepository repository, string revision, GitResetMode mode = GitResetMode.Soft,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdate = null, CancellationToken cancellationToken = default) : base(repository, "Reset branch")
    {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        _initialRevision = revision; _afterSubmoduleUpdate = afterSubmoduleUpdate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Name = "ResetCurrentBranchDialog"; Title = Text("$this", "Reset current branch");
        Width = 510; MinWidth = 495; SizeToContent = SizeToContent.Height; CanResize = false;
        StackPanel choices = new() { Spacing = 6, Margin = new Thickness(3) };
        Add(GitResetMode.Soft, "Soft", "&Soft: leave working directory and index untouched", Color.FromRgb(128, 255, 128), 36);
        Add(GitResetMode.Mixed, "Mixed", "Mi&xed: leave working directory untouched, reset index", Color.FromRgb(255, 255, 128), 36);
        Add(GitResetMode.Keep, "Keep", "&Keep: update working directory to the commit \n(abort if there are local changes), reset index", Color.FromRgb(255, 255, 128), 48);
        Add(GitResetMode.Merge, "Merge", "&Merge: update working directory to the commit and keep local changes \n(abort if there are conflicts), reset index", Color.FromRgb(255, 255, 128), 48);
        Add(GitResetMode.Hard, "Hard", "&Hard: reset working directory and index\n(discard ALL local changes, even uncommitted changes)", Color.FromRgb(255, 128, 128), 48);
        _modes[mode].IsChecked = true;
        _reset = Action("ExecuteResetCurrentBranch", "OK", ExecuteAsync); _reset.Content = Caption("Ok", "&OK"); _reset.IsDefault = true; _reset.MinWidth = 91; _reset.Height = 25;
        Button cancel = new() { Name = "CancelResetCurrentBranch", Content = Caption("Cancel", "&Cancel"), IsCancel = true, MinWidth = 91, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = ExportDialogControls.Buttons(_reset, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        _body = new() { Margin = new Thickness(8), Spacing = 5, Children = { _branch, _summary, ExportDialogControls.Group(Text("gbResetType", "Reset type"), choices) } };
        Root.Children.Add(_body); Track([.. _modes.Values]);
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
        Opened += (_, _) => { if (!IsBusy) { _modes[mode].Focus(); } };
        UpdateActions();

        void Add(GitResetMode value, string item, string source, Color color, double height)
        {
            RadioButton radio = new() { Name = "ResetMode" + item, GroupName = "ResetCurrentType", MinHeight = height,
                Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new AccessText { Text = Translations.TranslateAccessKeys("FormResetCurrentBranch", item, "Text", source), TextWrapping = TextWrapping.Wrap } };
            radio.ActualThemeVariantChanged += (_, _) => ResetDialogControls.UpdateModeColor(radio, color);
            radio.AttachedToVisualTree += (_, _) => ResetDialogControls.UpdateModeColor(radio, color);
            radio.IsCheckedChanged += (_, _) => UpdateActions(); ResetDialogControls.UpdateModeColor(radio, color);
            _modes.Add(value, radio); choices.Children.Add(radio);
        }
    }
    private string Text(string item, string source) => Translations.Translate("FormResetCurrentBranch", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormResetCurrentBranch", item, "Text", source) };
    public override Task InitializeAsync() => RunAsync("Reset branch", async token =>
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        GitStatus status = await Repository.GetStatusAsync(token);
        _revision = await Repository.ResolveRevisionAsync(_initialRevision, token);
        GitCommit commit = (await Repository.GetLogAsync(1, _revision, token, all: false)).Single();
        _branch.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("_branchInfo", "Reset branch '{0}' to revision:"), status.Branch ?? "HEAD");
        ResetDialogControls.SetSummary(_summary, commit); Status.Text = "";
    }, cancellationToken: _lifetime.Token);

    private async Task ExecuteAsync()
    {
        if (IsBusy || _confirming || _postProcessing || Applied || _revision is null || !_reset.IsEnabled) { return; }
        GitResetMode mode = Mode;
        if (mode == GitResetMode.Hard)
        {
            _confirming = true; UpdateActions();
            bool confirmed;
            try { confirmed = await ResetDialogControls.ConfirmHardAsync(this, Translations, _lifetime.Token); }
            finally { _confirming = false; UpdateActions(); }
            if (!confirmed || !IsVisible || _lifetime.IsCancellationRequested) { return; }
        }
        bool changedCheckout = false;
        _postProcessing = true; UpdateActions();
        try
        {
            await RunAsync("Reset branch", async token =>
            {
                changedCheckout = (await Repository.GetStatusAsync(token)).Head != _revision;
                try { await Repository.ResetAsync(_revision, mode, token); Applied = true; }
                finally { MarkRepositoryChanged(); }
            }, cancellationToken: _lifetime.Token);
            if (!Applied || !IsVisible || _lifetime.IsCancellationRequested) { return; }
            if (changedCheckout && !_bare) { SubmodulesUpdated = await ResetDialogControls.UpdateSubmodulesAsync(this, Repository, mode == GitResetMode.Hard, _lifetime.Token, _afterSubmoduleUpdate); }
            if (IsVisible && !_lifetime.IsCancellationRequested) { Close(true); }
        }
        finally { _postProcessing = false; UpdateActions(); }
    }
    protected override void UpdateActions()
    {
        if (_reset is null || _body is null) { return; }
        bool available = !IsBusy && !_confirming && !_postProcessing && !Applied && !_lifetime.IsCancellationRequested;
        _body.IsEnabled = available;
        foreach ((GitResetMode mode, RadioButton radio) in _modes) { radio.IsEnabled = available && (!_bare || mode == GitResetMode.Soft); }
        _reset.IsEnabled = available && _revision is not null && (!_bare || Mode == GitResetMode.Soft);
    }
}
