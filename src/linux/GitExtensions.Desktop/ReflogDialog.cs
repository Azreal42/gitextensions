using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormReflog's reference selector, full SHA/Ref/Action grid and recovery actions.</summary>
public sealed class ReflogDialog : RepositoryDialog
{
    private readonly ComboBox _references = new() { Name = "ReflogReference", Width = 272, Height = 24, MinHeight = 24, Padding = new Thickness(4, 1), IsTextSearchEnabled = true };
    private readonly ListBox _entries = new() { Name = "ReflogEntries", SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly TextBlock _dirty = new() { Name = "ReflogDirtyWorkingDirectory", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsVisible = false };
    private readonly Button _currentLink, _headLink;
    private readonly MenuItem _copy, _create, _reset;
    private readonly Grid _header = new() { Name = "ReflogHeader", Height = 30 };
    private readonly Grid _table = new() { RowDefinitions = new("Auto,*"), MinWidth = 720, ClipToBounds = true };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Bitmap> _icons = [];
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmoduleUpdate;
    private IReadOnlyList<GitReflogEntry> _values = [];
    private string _columns = "270,120,330";
    private string? _currentBranch;
    private bool _changingReference, _loaded;
    private int? _sortColumn;
    private bool _descending;
    public IReadOnlyList<GitReflogEntry> Entries => _values;
    public string SelectedReference => (_references.SelectedItem as ReferenceChoice)?.Name ?? "HEAD";

    public ReflogDialog(GitRepository repository, bool autoNormalize = true, string replacement = "_",
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdate = null) : base(repository, "Reflog")
    {
        Name = "ReflogDialog"; Title = Text("$this", "Reflog"); Width = 782; Height = 555; MinWidth = 400; MinHeight = 200;
        _autoNormalize = autoNormalize; _replacement = replacement; _afterSubmoduleUpdate = afterSubmoduleUpdate;
        ConstrainStatusHeight("ReflogStatus", 42);
        _dirty.Text = Text("lblDirtyWorkingDirectory", "Warning: you've got changes in your working directory that could be lost if you want to reset the current branch to another commit.\r\nStash them before if you don't want to lose them.");
        _currentLink = Link("ReflogCurrentBranch", "linkCurrentBranch", "(Current branch name)", () => SelectReference("refs/heads/" + _currentBranch));
        _headLink = Link("ReflogHead", "linkHead", "HEAD", () => SelectReference("HEAD"));
        WrapPanel selectors = new() { Name = "ReflogSelectors", Orientation = Orientation.Horizontal };
        selectors.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Name = "ReflogReferenceLabel", Text = Text("label2", "Reference:"), VerticalAlignment = VerticalAlignment.Center }, _references } });
        selectors.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Name = "ReflogDisplayLabel", Text = Text("label1", "Display reflog for:"), VerticalAlignment = VerticalAlignment.Center }, _currentLink, _headLink } });
        StackPanel top = new() { Children = { _dirty, selectors } };
        Grid body = new() { Name = "ReflogBody", RowDefinitions = new("Auto,*"), Margin = new Thickness(3), ClipToBounds = true };
        body.Children.Add(top);
        string[] keys = ["Sha", "Ref", "Action"], labels = ["SHA-1", "Ref", "Action"];
        for (int column = 0; column < keys.Length; column++)
        {
            int index = column;
            Button heading = new() { Name = "ReflogHeader" + keys[column], Height = 30, MinHeight = 0, Padding = new Thickness(4, 2), HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = Text(keys[column], labels[column], "HeaderText"), HorizontalAlignment = HorizontalAlignment.Stretch };
            heading.Click += (_, _) => Sort(index);
            Grid.SetColumn(heading, column); _header.Children.Add(heading);
        }
        _header.ColumnDefinitions = new(_columns); _table.Children.Add(_header);
        Grid.SetRow(_entries, 1); _table.Children.Add(_entries);
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        _entries.BorderThickness = new Thickness(0);
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)), new Setter(Layoutable.MinHeightProperty, 24d), new Setter(Layoutable.HeightProperty, 24d) } });
        _entries.ItemTemplate = new FuncDataTemplate<GitReflogEntry>((entry, _) => Row(entry));
        ScrollViewer gridViewport = new() { Name = "ReflogGridViewport", Content = _table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Border grid = ClassicChrome.Border(gridViewport, new Thickness(1)); grid.Name = "ReflogGrid"; grid.ClipToBounds = true;
        Grid.SetRow(grid, 1); body.Children.Add(grid); Root.Children.Add(body);
        _copy = Menu("ReflogCopySha", "copySha1ToolStripMenuItem", "Copy SHA-1", "CommitId", CopyAsync);
        _create = Menu("ReflogCreateBranch", "createABranchOnThisCommitToolStripMenuItem", "Create a branch on this commit...", "BranchCreate", CreateBranchAsync);
        _reset = Menu("ReflogResetBranch", "resetCurrentBranchOnThisCommitToolStripMenuItem", "Reset current branch to this commit...", "ResetCurrentBranchToHere", ResetAsync);
        _entries.ContextMenu = new ContextMenu { ItemsSource = new[] { _copy, _create, _reset } };
        _entries.ContextMenu.Opened += (_, _) => UpdateActions();
        _entries.SelectionChanged += (_, _) => UpdateActions();
        _entries.PointerMoved += (_, args) => SelectHit(args.Source);
        _entries.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (IsBusy) { return; }
            SelectHit(args.Source);
            if (args.GetCurrentPoint(_entries).Properties.IsLeftButtonPressed) { _entries.ContextMenu?.Open(_entries); args.Handled = true; }
        }, RoutingStrategies.Tunnel);
        _references.SelectionChanged += async (_, _) =>
        {
            if (_changingReference || IsBusy || !_loaded) { return; }
            ClearEntries(); await RunAsync("Reflog", ReadEntriesAsync, cancellationToken: _lifetime.Token);
        };
        Track(_references, _entries, _currentLink, _headLink);
        Closed += (_, _) => { _lifetime.Cancel(); _entries.ContextMenu?.Close(); foreach (Bitmap icon in _icons) { icon.Dispose(); } };
        Opened += (_, _) => { UpdateColors(); UpdateActions(); }; ActualThemeVariantChanged += (_, _) => UpdateColors();
        UpdateActions();
    }

    private string Text(string item, string source, string property = "Text") => Translations.Translate("FormReflog", item, property, source);
    private Grid Row(GitReflogEntry? entry)
    {
        Grid row = new() { ColumnDefinitions = new(_columns), Height = 24, Tag = entry };
        string[] values = [entry?.Hash ?? "", entry?.Selector ?? "", entry?.Description ?? ""];
        for (int column = 0; column < values.Length; column++)
        { TextBlock text = new() { Text = values[column], Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, column); row.Children.Add(text); }
        ToolTip.SetTip(row, entry?.Description); return row;
    }
    private Button Link(string name, string key, string caption, System.Action action)
    {
        Button link = new() { Name = name, Content = new TextBlock { Text = Text(key, caption), TextDecorations = TextDecorations.Underline },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(3, 0), MinHeight = 0, Height = 24 };
        link.Click += (_, _) => { if (!IsBusy) { action(); } }; return link;
    }
    private MenuItem Menu(string name, string key, string caption, string icon, Func<Task> action)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon + ".png"));
        Bitmap bitmap = new(stream); _icons.Add(bitmap);
        MenuItem item = new() { Name = name, Header = new AccessText { Text = Translations.TranslateAccessKeys("FormReflog", key, "Text", caption) },
            Icon = new Image { Source = bitmap, Width = 16, Height = 16, Tag = icon + ".png" } };
        item.Click += async (_, _) => await action(); return item;
    }
    private void SelectHit(object? source)
    {
        if (IsBusy || source is not Visual visual) { return; }
        ListBoxItem? row = visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is GitReflogEntry entry) { _entries.SelectedItem = entry; }
    }
    private void SelectReference(string name)
        => _references.SelectedItem = _references.Items.OfType<ReferenceChoice>().FirstOrDefault(choice => choice.Name == name);

    public override Task InitializeAsync() => RunAsync("Reflog", ReloadAsync, cancellationToken: _lifetime.Token);
    private async Task ReloadAsync(CancellationToken token)
    {
        string selected = SelectedReference;
        GitStatus status = await Repository.GetStatusAsync(token);
        IReadOnlyList<GitReference> refs = await Repository.GetReferencesAsync(token);
        _currentBranch = status.IsDetached || status.IsUnborn ? null : status.Branch;
        _dirty.IsVisible = status.Changes.Count != 0;
        _currentLink.IsVisible = _currentBranch is not null;
        string branchCaption = Text("linkCurrentBranch", "(Current branch name)");
        ((TextBlock)_currentLink.Content!).Text = (branchCaption == "(Current branch name)" ? "current branch" : branchCaption) + " (" + _currentBranch + ")";
        List<ReferenceChoice> choices = [new("HEAD", "HEAD")];
        choices.AddRange(refs.Where(reference => reference.IsBranch).OrderBy(reference => reference.ShortName, StringComparer.Ordinal).Select(reference => new ReferenceChoice(reference.Name, reference.ShortName)));
        choices.AddRange(refs.Where(reference => reference.IsRemote).OrderBy(reference => reference.ShortName, StringComparer.Ordinal).Select(reference => new ReferenceChoice(reference.Name, reference.ShortName)));
        _changingReference = true;
        try { _references.ItemsSource = choices; _references.SelectedItem = choices.FirstOrDefault(choice => choice.Name == selected) ?? choices[0]; }
        finally { _changingReference = false; }
        ClearEntries(); await ReadEntriesAsync(token); _loaded = true;
    }
    private void ClearEntries() { _values = []; _entries.ItemsSource = _values; UpdateActions(); }
    private async Task ReadEntriesAsync(CancellationToken token)
    {
        _values = await Repository.GetReflogAsync(count: null, reference: SelectedReference, cancellationToken: token);
        SizeColumns(); PresentEntries(); Status.Text = "";
    }
    private void SizeColumns()
    {
        double Measure(string text) { TextBlock value = new() { Text = text, FontFamily = FontFamily, FontSize = FontSize }; value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return value.DesiredSize.Width + 12; }
        double sha = Math.Max(Measure(Text("Sha", "SHA-1", "HeaderText")), _values.Select(entry => Measure(entry.Hash)).DefaultIfEmpty(270).Max());
        double reference = Math.Max(Measure(Text("Ref", "Ref", "HeaderText")), _values.Select(entry => Measure(entry.Selector)).DefaultIfEmpty(120).Max());
        double action = Math.Max(260, _values.Select(entry => Measure(entry.Description)).DefaultIfEmpty(330).Max());
        _columns = string.Join(',', new[] { sha, reference, action }.Select(width => width.ToString(CultureInfo.InvariantCulture)));
        _header.ColumnDefinitions = new(_columns); _table.MinWidth = Math.Max(720, sha + reference + action);
    }
    private void Sort(int column)
    {
        if (IsBusy) { return; }
        _descending = _sortColumn == column && !_descending; _sortColumn = column; PresentEntries();
    }
    private void PresentEntries()
    {
        GitReflogEntry? selected = _entries.SelectedItem as GitReflogEntry;
        IEnumerable<GitReflogEntry> entries = _values;
        if (_sortColumn is int column)
        {
            string Key(GitReflogEntry entry) => column == 0 ? entry.Hash : column == 1 ? entry.Selector : entry.Description;
            StringComparer comparer = column == 2 ? StringComparer.CurrentCulture : StringComparer.Ordinal;
            entries = _descending ? entries.OrderByDescending(Key, comparer) : entries.OrderBy(Key, comparer);
        }
        GitReflogEntry[] displayed = entries.ToArray(); _entries.ItemsSource = displayed;
        _entries.SelectedItem = selected is not null && displayed.Contains(selected) ? selected : displayed.FirstOrDefault();
    }
    private Task CopyAsync()
    {
        if (IsBusy || _entries.SelectedItem is not GitReflogEntry selected || Clipboard is not { } clipboard) { return Task.CompletedTask; }
        string hash = selected.Hash;
        return RunAsync("Copy SHA-1", async _ => await ClipboardExtensions.SetTextAsync(clipboard, hash), cancellationToken: _lifetime.Token);
    }
    private Task CreateBranchAsync()
    {
        if (IsBusy || _entries.SelectedItem is not GitReflogEntry selected || !IsVisible) { return Task.CompletedTask; }
        string hash = selected.Hash;
        return RunAsync("Create branch", async token =>
        {
            CreateBranchDialog child = new(Repository, hash, autoNormalize: _autoNormalize, replacement: _replacement,
                checkoutAfterCreation: false, userAbleToChangeRevision: false, couldBeOrphan: false);
            await ShowChildAsync(child, token);
            if (IsVisible && !token.IsCancellationRequested) { await ReloadAsync(token); }
        }, cancellationToken: _lifetime.Token);
    }
    private Task ResetAsync()
    {
        if (IsBusy || _entries.SelectedItem is not GitReflogEntry selected || _currentBranch is null || !IsVisible) { return Task.CompletedTask; }
        string hash = selected.Hash;
        return RunAsync("Reset branch", async token =>
        {
            await ReloadAsync(token);
            if (_currentBranch is null) { throw new InvalidOperationException(T("Reset current branch is unavailable while HEAD is detached.")); }
            bool dirty = _dirty.IsVisible;
            string branch = _currentBranch;
            if (dirty && !await ConfirmDirtyAsync(token)) { return; }
            token.ThrowIfCancellationRequested(); if (!IsVisible) { return; }
            GitStatus current = await Repository.GetStatusAsync(token);
            if (current.IsDetached || current.IsUnborn || current.Branch != branch)
            { await ReloadAsync(token); throw new InvalidOperationException(T("The current branch changed while the reflog action was open.")); }
            ResetCurrentBranchDialog child = new(Repository, hash, dirty ? GitResetMode.Soft : GitResetMode.Hard,
                afterSubmoduleUpdate: _afterSubmoduleUpdate, cancellationToken: token);
            await ShowChildAsync(child, token);
            if (IsVisible && !token.IsCancellationRequested) { await ReloadAsync(token); }
        }, cancellationToken: _lifetime.Token);
    }
    private async Task ShowChildAsync(RepositoryDialog child, CancellationToken token)
    {
        child.RequestedThemeVariant = ActualThemeVariant;
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (child.IsVisible) { child.Close(); } }));
        try
        {
            await child.InitializeAsync(); token.ThrowIfCancellationRequested();
            if (IsVisible) { await child.ShowDialog(this); }
        }
        finally { child.Close(); if (child.HasRepositoryChanges) { MarkRepositoryChanged(); } }
    }
    private async Task<bool> ConfirmDirtyAsync(CancellationToken token)
    {
        Window warning = new() { Name = "ReflogDirtyResetWarning", Title = Text("_continueResetCurrentBranchCaptionText", "Changes not committed..."), Width = 520,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button no = new() { Name = "CancelReflogDirtyReset", Content = T("No"), IsDefault = true, IsCancel = true, MinWidth = 75, Height = 25 };
        Button yes = new() { Name = "ConfirmReflogDirtyReset", Content = T("Yes"), MinWidth = 75, Height = 25 };
        no.Click += (_, _) => warning.Close(false); yes.Click += (_, _) => warning.Close(true);
        warning.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ResetDialogControls.Icon("Warning"), new TextBlock
                { Name = "ReflogDirtyResetMessage", Text = Text("_continueResetCurrentBranchEvenWithChangesText", "You have changes in your working directory that could be lost.\n\nDo you want to continue?"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 } } },
            ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (warning.IsVisible) { warning.Close(false); } }));
        bool confirmed = await warning.ShowDialog<bool>(this); token.ThrowIfCancellationRequested(); return confirmed;
    }
    private void UpdateColors()
    {
        Color background = Background is ISolidColorBrush brush ? brush.Color : Colors.White;
        foreach (Button link in new[] { _currentLink, _headLink })
        { SolidColorBrush color = new(FileStatusListColors.Contrasting(Colors.Blue, background)); link.Foreground = color; ((TextBlock)link.Content!).Foreground = color; }
        _dirty.Foreground = new SolidColorBrush(FileStatusListColors.Contrasting(Colors.Red, background));
    }
    protected override void UpdateActions()
    {
        bool ready = _loaded && !IsBusy && !_lifetime.IsCancellationRequested;
        _references.IsEnabled = _entries.IsEnabled = _headLink.IsEnabled = ready;
        _currentLink.IsEnabled = ready && _currentBranch is not null;
        if (_copy is null) { return; }
        bool selected = ready && _entries.SelectedItem is GitReflogEntry;
        _copy.IsEnabled = selected && Clipboard is not null; _create.IsEnabled = selected; _reset.IsEnabled = selected && _currentBranch is not null;
    }
    private sealed record ReferenceChoice(string Name, string Caption) { public override string ToString() => Caption; }
}
