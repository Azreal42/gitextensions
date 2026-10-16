using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormAddToGitIgnore with Git's cancellable, last-request-wins pattern preview.</summary>
public sealed class AddToGitIgnoreDialog : RepositoryDialog
{
    private readonly bool _localExclude;
    private readonly TextBox _patterns = new() { Name = "IgnorePatterns", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 65 };
    private readonly ListBox _preview = new() { Name = "IgnorePatternPreview" };
    private readonly TextBlock _count = new() { Name = "IgnorePatternCount", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _noMatches;
    private readonly Button _ignore;
    private readonly Control _statusRow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _previewTasks = [];
    private CancellationTokenSource? _previewRequest;
    private GitRulesFileSnapshot? _snapshot;
    private bool _closing, _closed, _draining, _initializing;
    private int _requestVersion;

    public AddToGitIgnoreDialog(GitRepository repository, bool localExclude = false, IEnumerable<string>? patterns = null) : base(repository, "Add file(s) to .gitignore")
    {
        _localExclude = localExclude;
        Name = "AddToGitIgnoreDialog"; Title = localExclude ? C("_addToLocalExcludeTitle", "Add file(s) to .git/info/exclude") : C("$this", "Add file(s) to .gitignore");
        Width = 599; Height = 341; MinWidth = 460; MinHeight = 280;
        _statusRow = (Control)Status.Parent!; _statusRow.IsVisible = false; ConstrainStatusHeight("IgnorePatternError", 80);
        _patterns.Text = string.Join('\n', patterns ?? []);
        _ignore = Action("AppendIgnorePatterns", "Ignore", AppendAsync); _ignore.Content = C("AddToIgnore", "Ignore"); _ignore.Width = 110; _ignore.Height = 25; _ignore.IsDefault = true;
        Button cancel = Action("CancelIgnorePatterns", "Cancel", () => { Close(); return Task.CompletedTask; }); cancel.Content = C("btnCancel", "Cancel"); cancel.Width = 110; cancel.Height = 25; cancel.IsCancel = true;
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 22, Margin = new Thickness(12, 7, 22, 12), Children = { _ignore, cancel } };
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);
        Grid groups = new() { RowDefinitions = new RowDefinitions("93,*") };
        groups.Children.Add(ExportDialogControls.Group(C("groupFilePattern", "Enter a file pattern to ignore:"), _patterns));
        DockPanel preview = new();
        _count.Margin = new Thickness(0, 3, 0, 0); DockPanel.SetDock(_count, Dock.Bottom); preview.Children.Add(_count);
        _preview.ItemTemplate = new FuncDataTemplate<string>((path, _) => new TextBlock { Text = ExportDialogControls.DisplayPath(path ?? ""), TextWrapping = TextWrapping.NoWrap });
        _preview.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            { new Setter(MinHeightProperty, 15d), new Setter(HeightProperty, 15d), new Setter(PaddingProperty, new Thickness(3, 0)) } });
        _noMatches = new StackPanel { Name = "NoIgnorePatternMatches", Orientation = Orientation.Horizontal, Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Children = { OriginalMenuItems.Icon("Unmerged.png"), new TextBlock { Text = C("label2", "No existing files match that pattern.") } } };
        preview.Children.Add(new Grid { Children = { _preview, _noMatches } });
        Border previewGroup = ExportDialogControls.Group(C("groupBox1", "Preview"), preview); Grid.SetRow(previewGroup, 1); groups.Children.Add(previewGroup); Root.Children.Add(groups);
        Track(_patterns);
        _patterns.TextChanged += (_, _) => { if (!_initializing && _snapshot is not null) { QueuePreview(); } };
        Closing += (_, args) =>
        {
            if (_closed) { return; }
            _closing = true; _lifetime.Cancel(); _previewRequest?.Cancel();
            Task[] pending = _previewTasks.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length > 0)
            {
                args.Cancel = true;
                if (!_draining) { _draining = true; _ = CloseAfterPreviewsAsync(pending); }
            }
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _previewRequest?.Dispose(); _lifetime.Dispose(); };
        UpdateActions();
    }

    private string C(string name, string source) => Translations.Translate("FormAddToGitIgnore", name, "Text", source);
    internal string[] CurrentPatterns => (_patterns.Text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Where(line => line.Length > 0).ToArray();
    internal bool IsPreviewLoading { get; private set; }

    public override async Task InitializeAsync()
    {
        _initializing = true;
        try { await RunAsync("Load file", async token => _snapshot = await Repository.GetRulesFileSnapshotAsync(_localExclude ? GitRulesFileKind.LocalExclude : GitRulesFileKind.Ignore, cancellationToken: token)); }
        finally { _initializing = false; }
        _statusRow.IsVisible = LastError is not null;
        if (_snapshot is not null && !_closing) { QueuePreview(immediate: true); }
        if (IsVisible) { _patterns.Focus(); }
    }

    private void QueuePreview(bool immediate = false)
    {
        if (_closing) { return; }
        _previewRequest?.Cancel();
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewRequest = request;
        int version = ++_requestVersion; string[] patterns = CurrentPatterns;
        IsPreviewLoading = true; _preview.IsEnabled = false; _noMatches.IsVisible = false;
        _count.Text = C("_updateStatusString", "Updating ...");
        _previewTasks.RemoveAll(task => task.IsCompleted);
        Task[] previous = _previewTasks.ToArray();
        _previewTasks.Add(LoadPreviewAsync(request, patterns, version, immediate, previous));
    }

    private async Task LoadPreviewAsync(CancellationTokenSource request, string[] patterns, int version, bool immediate, Task[] previous)
    {
        try
        {
            // Cancelled Git actors must finish before a newer preview becomes current.
            await Task.WhenAll(previous);
            request.Token.ThrowIfCancellationRequested();
            if (!immediate) { await Task.Delay(300, request.Token); }
            IReadOnlyList<string> files = patterns.Length == 0 ? [] : await Repository.GetIgnoredFilesAsync(patterns, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_closing || version != _requestVersion) { return; }
            _preview.ItemsSource = files; _preview.IsEnabled = true; _noMatches.IsVisible = files.Count == 0;
            _count.Text = C("_matchingFilesString", "{0} file(s) matched").Replace("{0}", files.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
            Status.Text = ""; _statusRow.IsVisible = false;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing && version == _requestVersion) { Status.Text = error.Message; _statusRow.IsVisible = true; }
        }
        finally
        {
            if (version == _requestVersion) { IsPreviewLoading = false; _previewRequest = null; }
            request.Dispose();
        }
    }

    private async Task AppendAsync()
    {
        if (_snapshot is null || IsBusy || _closing) { return; }
        string[] patterns = CurrentPatterns;
        if (patterns.Length == 0) { Close(); return; }
        await RunAsync("Ignore", async token =>
        {
            _previewRequest?.Cancel();
            await Task.WhenAll(_previewTasks.ToArray());
            token.ThrowIfCancellationRequested();
            try { _snapshot = await Repository.AppendIgnorePatternsAsync(_snapshot, patterns, token); }
            finally { MarkRepositoryChanged(); }
            Close();
        });
        _statusRow.IsVisible = LastError is not null;
    }

    private async Task CloseAfterPreviewsAsync(Task[] pending)
    {
        await Task.WhenAll(pending); _draining = false; Close();
    }

    protected override void UpdateActions() => _ignore.IsEnabled = !IsBusy && !_closing && _snapshot is not null;
}
