using GitExtensions.Linux.Desktop.Avatars;
using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

public enum BlameCommand { ThisRevision, PreviousRevision, ShowChanges }
public sealed class BlameCommandEventArgs(BlameCommand command, GitFileBlameLine line) : EventArgs
{
    public BlameCommand Command { get; } = command;
    public GitFileBlameLine Line { get; } = line;
}

public sealed class BlameView : UserControl, IDisposable
{
    private readonly FileHistoryContentView _content = new() { Name = "BlameCode" };
    private readonly AuthorMargin _authors;
    private readonly Bitmap? _avatar;
    private readonly IGourceAvatarProvider _avatarProvider;
    private readonly bool _usesDefaultAvatarProvider;
    private AvatarWindowLifetime? _avatarWindow;
    private readonly Dictionary<string, Bitmap> _avatars = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _avatarOperation;
    private readonly HashSet<Task> _avatarReads = [];
    private Task? _avatarClose;
    private bool _avatarClosing;
    private bool _disposed;
    private bool _showAvatar = true;
    private GitFileBlame? _blame;
    private string? _repositoryPath;
    private string? _displayedRepositoryPath;
    private bool _settingBlame;
    private IBrush[] _ages = [];
    private int _selectedLine;
    private readonly BlamePreferencesStore _preferencesStore = new();
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<Bitmap> _menuIcons = [];
    private readonly List<Action> _updateMenus = [];
    private readonly Dictionary<(string Hash, string Path), string> _commitInformation = [];
    private readonly TextBlock _tooltipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 580 };
    private CancellationTokenSource? _tooltipOperation;
    private GitFileBlameLine? _contextLine;
    private string? _hoveredHash;
    public event EventHandler<BlameCommandEventArgs>? CommandRequested;
    public event Action<string>? InteractionError;
    public event Action<BlamePreferences>? PreferencesChanged;
    private BlamePreferences _preferences = new();
    public BlamePreferences Preferences
    {
        get => _preferences with { UseHistogramDiffAlgorithm = _preferencesStore.Load().UseHistogramDiffAlgorithm };
        private set => _preferences = value;
    }
    public string? PreferenceError { get; private set; }
    public Func<GitFileBlameLine, CancellationToken, Task<string>>? CommitInformationProvider { get; set; }
    public Task TooltipLoading { get; private set; } = Task.CompletedTask;
    public event EventHandler? SelectedLineChanged;
    public GitFileBlameLine? SelectedLine => _blame is not null && _selectedLine > 0 && _selectedLine <= _blame.Lines.Count ? _blame.Lines[_selectedLine - 1] : null;
    public IReadOnlyList<GitFileBlameLine> Lines => _blame?.Lines ?? [];
    public DiffView TextView => _content.TextView;
    public int CurrentFileLine => TextView.CurrentFileLine;
    public string CurrentFileEncodingName => _content.CurrentFileEncodingName;
    public bool ShowAuthor { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowTime { get; set; } = true;
    public bool DisplayAuthorFirst { get; set; }
    public bool ShowLineNumbers { get; set; }
    public bool ShowOriginalPath { get; set; } = true;
    private static readonly IGourceAvatarProvider LegacyDefaultAvatarProvider = new GourceAvatarProvider();
    internal static IGourceAvatarProvider OriginalDefaultAvatarProvider => LegacyDefaultAvatarProvider;
    public static IGourceAvatarProvider DefaultAvatarProvider { get; set; } = LegacyDefaultAvatarProvider;
    public Task AvatarLoading { get; private set; } = Task.CompletedTask;
    internal event Action<Task>? AvatarReadStarted;
    internal Task AvatarCompletion => Task.WhenAll(_avatarReads.ToArray());
    public bool ShowAvatar
    {
        get => _showAvatar;
        set
        {
            if (_showAvatar == value) { return; }
            _showAvatar = value;
            StartAvatarLoading();
            RefreshDisplay();
        }
    }

    public BlameView(IGourceAvatarProvider? avatarProvider = null)
    {
        _avatarProvider = avatarProvider ?? DefaultAvatarProvider;
        _usesDefaultAvatarProvider = avatarProvider is null && ReferenceEquals(_avatarProvider, LegacyDefaultAvatarProvider);
        Uri image = new("avares://GitExtensions/Assets/Icons/User80.png");
        if (AssetLoader.Exists(image)) { using Stream stream = AssetLoader.Open(image); _avatar = new Bitmap(stream); }
        _authors = new(this) { Name = "BlameAuthorMargin", ClipToBounds = true };
        TextView.Editor.PropertyChanged += EditorFontChanged;
        Border tooltip = new() { Child = _tooltipText, Padding = new Thickness(8), BorderThickness = new Thickness(1) };
        tooltip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicTooltipBrush"));
        tooltip.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicBorderBrush"));
        _tooltipText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicTooltipTextBrush"));
        ToolTip.SetTip(_authors, tooltip); ToolTip.SetShowDelay(_authors, 0);
        ToolTip.SetPlacement(_authors, PlacementMode.RightEdgeAlignedTop);
        ToolTip.SetHorizontalOffset(_authors, 60);
        _authors.ContextMenu = CreateContextMenu();
        _authors.DoubleTapped += (_, _) =>
        {
            if (SelectedLine is { } line && !line.Line.Hash.All(character => character == '0'))
            { CommandRequested?.Invoke(this, new(BlameCommand.ThisRevision, line)); }
        };
        TextView.Editor.TextArea.TextView.PointerMoved += (_, args) => Hover(LineAt(TextView.Editor.TextArea.TextView, args.GetPosition(TextView.Editor.TextArea.TextView).Y), showTooltip: false);
        TextView.Editor.TextArea.TextView.PointerExited += (_, _) => ClearHover();
        TextView.Editor.TextArea.LeftMargins.Insert(0, _authors);
        TextView.Editor.TextArea.TextView.BackgroundRenderers.Add(new CommitBackground(this));
        TextView.Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!_settingBlame && !TextView.IsUpdatingText) { SelectLine(TextView.Editor.TextArea.Caret.Line, moveCaret: false); }
        };
        ActualThemeVariantChanged += (_, _) => RefreshDisplay();
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            { _avatarWindow = AvatarWindowLifetime.For(window); _avatarWindow.Changed += AvatarBindingChanged; }
            SetPreferences(_preferencesStore.Load(), persist: false); StartAvatarLoading();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_avatarWindow is not null) { _avatarWindow.Changed -= AvatarBindingChanged; _avatarWindow = null; }
            CancelAvatarLoading(); _authors.ClearAvatarPresenters(); ClearHover();
        };
        TextView.Editor.TextArea.TextView.VisualLinesChanged += (_, _) => _authors.QueueAvatarLayout();
        TextView.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => _authors.QueueAvatarLayout();
        Content = _content;
        SetPreferences(_preferencesStore.Load(), persist: false);
    }
    public async Task ConfigureRepositoryAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settingBlame = true;
        TextView.BeginDocumentLoad();
        try
        {
            await TextView.ConfigureRepositoryAsync(repository, cancellationToken);
            _repositoryPath = repository.Path;
        }
        finally { TextView.BeginDocumentLoad(); _settingBlame = false; }
    }
    public void SetBlame(GitFileBlame blame, int? initialLine = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool sameFile = _blame?.Path == blame.Path && _displayedRepositoryPath == _repositoryPath;
        int target = Math.Clamp(initialLine ?? (sameFile ? CurrentFileLine : 1), 1, Math.Max(1, blame.Lines.Count));
        string? revisionIdentity = blame.Source is { Kind: not GitCompareRevisionKind.Commit } source
            ? "source:" + source.Kind : blame.Revision;
        string identity = string.Join('\0', _repositoryPath, blame.Path, revisionIdentity, "blame");
        bool sameIdentity = TextView.DocumentIdentity == identity;
        CancelAvatarLoading();
        ClearHover(); _contextLine = null;
        _commitInformation.Clear();
        _settingBlame = true;
        try
        {
            _blame = blame;
            _displayedRepositoryPath = _repositoryPath;
            TextView.DocumentIdentity = identity;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long oldest = Math.Min(now.AddYears(-3).Ticks, blame.Lines.Count == 0 ? now.AddYears(-3).Ticks : blame.Lines.Min(item => item.Line.AuthoredAt.Ticks));
            long interval = Math.Max(1, (now.Ticks - oldest + 1) / Ages.Length);
            _ages = blame.Lines.Select(line => Ages[(int)Math.Clamp((line.Line.AuthoredAt.Ticks - oldest) / interval, 0, Ages.Length - 1)]).ToArray();
            _content.ShowFile(blame.Path, blame.Bytes, blame.FileEncodingName);
            if (!_content.IsImage) { TextView.CompletePlainTextDocumentLoad(); }
            if (blame.Lines.Count == 0) { _selectedLine = 0; }
            else
            {
                // A same-document refresh lets DiffView retain the complete selection and scroll.
                // Navigation supplies an explicit remapped line and must replace that position.
                SelectLine(target, moveCaret: initialLine.HasValue || !sameIdentity || CurrentFileLine != target);
            }
        }
        finally { _settingBlame = false; }
        RefreshDisplay();
        StartAvatarLoading();
    }
    public void SelectLine(int number, bool moveCaret = true)
    {
        if (_blame is null || number < 1 || number > _blame.Lines.Count) { return; }
        bool changed = number != _selectedLine;
        _selectedLine = number;
        if (moveCaret && !_content.IsBinary && !_content.IsImage && number <= TextView.Editor.Document.LineCount)
        {
            TextView.CaretOffset = TextView.Editor.Document.GetLineByNumber(number).Offset;
            TextView.Editor.ScrollToLine(number);
        }
        RefreshDisplay();
        if (changed) { SelectedLineChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void RefreshDisplay()
    {
        _authors.InvalidateVisual();
        TextView.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }
    private IBrush GroupBrush => Brush.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#414141" : "#e3e3e3");
    private bool IsSelected(GitFileBlameLine line) => (_hoveredHash ?? SelectedLine?.Line.Hash) == line.Line.Hash;
    private static readonly IBrush[] Ages = new[] { "#f7fcf5", "#c7e9c0", "#a1d99b", "#74c476", "#41ab5d", "#238b45", "#00441b" }.Select(Brush.Parse).ToArray();
    private bool UsesSharedAvatars => _usesDefaultAvatarProvider && _avatarWindow?.HasBinding == true;
    private void AvatarBindingChanged() => StartAvatarLoading();
    internal void TrackAvatarRead(Task read)
    {
        _avatarReads.Add(read); AvatarLoading = read; AvatarReadStarted?.Invoke(read); _ = RetireAvatarReadAsync(read);
    }
    public void CancelAvatarLoading() { _avatarOperation?.Cancel(); _avatarOperation = null; }
    private void StartAvatarLoading()
    {
        CancelAvatarLoading();
        if (UsesSharedAvatars && !_disposed && !_avatarClosing)
        { _authors.QueueAvatarLayout(); return; }
        _authors.ClearAvatarPresenters();
        if (_disposed || _avatarClosing || !_showAvatar || _blame is null || this.GetVisualRoot() is null) { return; }
        CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _avatarOperation = operation;
        int size = Math.Max(1, (int)Math.Ceiling(TextView.Editor.TextArea.TextView.DefaultLineHeight) + 1);
        GitBlameLine[] authors = _blame.Lines.Select(line => line.Line)
            .Where(line => !line.Hash.All(character => character == '0') && !string.IsNullOrWhiteSpace(line.AuthorEmail))
            .DistinctBy(line => line.AuthorEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(line => !_avatars.ContainsKey(line.AuthorEmail.Trim())).ToArray();
        TrackAvatarRead(LoadAvatarsAsync(authors, size, operation));
    }
    private async Task RetireAvatarReadAsync(Task read)
    {
        try { await read; }
        catch { /* The initiating view owns its optional-image fallback. */ }
        finally { _avatarReads.Remove(read); }
    }
    internal Task BeginAvatarClose()
    {
        if (_avatarClose is not null) { return _avatarClose; }
        _avatarClosing = true; CancelAvatarLoading(); _authors.ClearAvatarPresenters();
        return _avatarClose = AvatarCompletion;
    }
    private async Task LoadAvatarsAsync(GitBlameLine[] authors, int size, CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        try { await Task.WhenAll(authors.Select(LoadAsync)); }
        finally
        {
            if (_avatarOperation == operation) { _avatarOperation = null; }
            operation.Dispose();
        }
        async Task LoadAsync(GitBlameLine author)
        {
            try
            {
                byte[]? bytes = await _avatarProvider.GetAvatarPngAsync(author.AuthorEmail, author.AuthorName, size, token);
                token.ThrowIfCancellationRequested();
                if (bytes is null || _disposed) { return; }
                using MemoryStream stream = new(bytes, writable: false);
                Bitmap bitmap = new(stream);
                if (_avatars.TryGetValue(author.AuthorEmail.Trim(), out Bitmap? previous)) { previous.Dispose(); }
                _avatars[author.AuthorEmail.Trim()] = bitmap;
                _authors.InvalidateVisual();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException)
            { /* An unavailable avatar must keep User80 without breaking the Git view. */ }
        }
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _lifetime.Cancel(); CancelAvatarLoading(); _authors.ClearAvatarPresenters(); ClearHover();
        if (_avatarWindow is not null) { _avatarWindow.Changed -= AvatarBindingChanged; _avatarWindow = null; }
        TextView.Editor.PropertyChanged -= EditorFontChanged;
        _content.Dispose(); _avatar?.Dispose();
        foreach (Bitmap avatar in _avatars.Values) { avatar.Dispose(); }
        foreach (Bitmap icon in _menuIcons) { icon.Dispose(); }
        _avatars.Clear(); _lifetime.Dispose();
    }

    public void SetPreferences(BlamePreferences preferences, bool persist = true)
    {
        preferences = preferences.Normalize();
        bool changed = Preferences != preferences;
        Preferences = preferences;
        ShowAuthor = preferences.ShowAuthor; ShowDate = preferences.ShowAuthorDate; ShowTime = preferences.ShowAuthorTime;
        ShowOriginalPath = preferences.ShowOriginalFilePath; DisplayAuthorFirst = preferences.DisplayAuthorFirst;
        ShowLineNumbers = preferences.ShowLineNumbers; ShowAvatar = preferences.ShowAuthorAvatar;
        PreferenceError = null;
        if (persist)
        {
            try { _preferencesStore.Save(preferences); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { PreferenceError = exception.Message; }
        }
        foreach (Action update in _updateMenus) { update(); }
        RefreshDisplay();
        if (changed) { PreferencesChanged?.Invoke(preferences); }
    }

    public string GetAuthorLine(GitFileBlameLine line)
    {
        string? author = ShowAuthor ? line.Line.AuthorName : null;
        string? date = ShowDate ? line.Line.AuthoredAt.ToString(ShowTime ? "g" : "d", CultureInfo.CurrentCulture) : null;
        return string.Join(" - ", new[]
        {
            DisplayAuthorFirst ? author : date, DisplayAuthorFirst ? date : author,
            ShowOriginalPath && line.OriginalPath != _blame?.Path ? ExportDialogControls.DisplayPath(line.OriginalPath) : null
        }.Where(value => value is not null));
    }

    public Button CreateOptionsButton()
    {
        Button button = new() { Name = "BlameOptions", Width = 28, Height = 24, Padding = new Thickness(4), Content = MenuIcon("Blame.png") };
        ToolTip.SetTip(button, Label("toolStripBlameOptions", "Blame options"));
        ContextMenu menu = CreateOptionsMenu(); button.ContextMenu = menu;
        button.Click += (_, _) => menu.Open(button);
        return button;
    }

    private ContextMenu CreateOptionsMenu()
    {
        MenuItem Option(string name, string text, Func<BlamePreferences, bool> read, Func<BlamePreferences, BlamePreferences> toggle)
        {
            MenuItem item = new() { Name = "BlameOption_" + name, Header = Label(name, text), ToggleType = MenuItemToggleType.CheckBox, IsChecked = read(Preferences) };
            item.Click += (_, _) => SetPreferences(toggle(Preferences));
            _updateMenus.Add(() => { item.IsChecked = read(Preferences); if (name == "showAuthorTimeToolStripMenuItem") { item.IsEnabled = Preferences.ShowAuthorDate; } });
            return item;
        }
        MenuItem heading(string name, string text) => new() { Header = Label(name, text), IsEnabled = false };
        ContextMenu menu = new() { Name = "BlameDisplayOptions", ItemsSource = new Control[]
        {
            heading("blameSettingsToolStripMenuItem", "Blame settings:"),
            Option("ignoreWhitespaceToolStripMenuItem", "Ignore whitespace", p => p.IgnoreWhitespace, p => p with { IgnoreWhitespace = !p.IgnoreWhitespace }),
            Option("detectMoveAndCopyInThisFileToolStripMenuItem", "Detect move and copy in this file", p => p.DetectMoves, p => p with { DetectMoves = !p.DetectMoves }),
            Option("detectMoveAndCopyInAllFilesToolStripMenuItem", "Detect move and copy in all files", p => p.DetectCopies, p => p with { DetectCopies = !p.DetectCopies }),
            new Separator(), heading("displaySettingsToolStripMenuItem", "Display result settings:"),
            Option("displayAuthorFirstToolStripMenuItem", "Display author first", p => p.DisplayAuthorFirst, p => p with { DisplayAuthorFirst = !p.DisplayAuthorFirst }),
            Option("showAuthorAvatarToolStripMenuItem", "Show author avatar", p => p.ShowAuthorAvatar, p => p with { ShowAuthorAvatar = !p.ShowAuthorAvatar }),
            Option("showAuthorToolStripMenuItem", "Show author", p => p.ShowAuthor, p => p with { ShowAuthor = !p.ShowAuthor, ShowAuthorDate = !p.ShowAuthor || p.ShowAuthorDate }),
            Option("showAuthorDateToolStripMenuItem", "Show author date", p => p.ShowAuthorDate, p => p with { ShowAuthorDate = !p.ShowAuthorDate, ShowAuthor = p.ShowAuthorDate || p.ShowAuthor }),
            Option("showAuthorTimeToolStripMenuItem", "Show author time", p => p.ShowAuthorTime, p => p with { ShowAuthorTime = !p.ShowAuthorTime }),
            Option("showLineNumbersToolStripMenuItem", "Show line numbers", p => p.ShowLineNumbers, p => p with { ShowLineNumbers = !p.ShowLineNumbers }),
            Option("showOriginalFilePathToolStripMenuItem", "Show original file path", p => p.ShowOriginalFilePath, p => p with { ShowOriginalFilePath = !p.ShowOriginalFilePath }),
            new Separator(),
            Option("UseHistogramDiffAlgorithm", "Use histogram diff algorithm", p => p.UseHistogramDiffAlgorithm, p => p with { UseHistogramDiffAlgorithm = !p.UseHistogramDiffAlgorithm })
        } };
        foreach (Action update in _updateMenus) { update(); }
        return menu;
    }

    private ContextMenu CreateContextMenu()
    {
        MenuItem Command(string name, string text, string icon, BlameCommand command)
        {
            MenuItem item = new() { Name = name, Header = _translations.Translate("BlameControl", name, "Text", text), Icon = MenuIcon(icon) };
            item.Click += (_, _) => { if ((_contextLine ?? SelectedLine) is { } line) { CommandRequested?.Invoke(this, new(command, line)); } };
            return item;
        }
        MenuItem thisRevision = Command("blameRevisionToolStripMenuItem", "Blame this revision", "Blame.png", BlameCommand.ThisRevision);
        MenuItem previousRevision = Command("blamePreviousRevisionToolStripMenuItem", "Blame previous revision", "RecentRepositories.png", BlameCommand.PreviousRevision);
        previousRevision.Header = _translations.Translate("BlameControl", "_blameActualPreviousRevision", "Text", "Blame previous revision");
        MenuItem changes = Command("showChangesToolStripMenuItem", "Show changes", "information.png", BlameCommand.ShowChanges);
        MenuItem Copy(string name, string text, string icon, Func<GitFileBlameLine, CancellationToken, Task<string>> value)
        {
            MenuItem item = new() { Name = name, Header = _translations.Translate("BlameControl", name, "Text", text), Icon = MenuIcon(icon) };
            item.Click += async (_, _) =>
            {
                if ((_contextLine ?? SelectedLine) is not { } line || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { return; }
                try { await ClipboardExtensions.SetTextAsync(clipboard, await value(line, _lifetime.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception exception) when (exception is GitCommandException or IOException or InvalidOperationException)
                { InteractionError?.Invoke(exception.Message); }
            };
            return item;
        }
        MenuItem copy = new() { Name = "copyToClipboardToolStripMenuItem", Header = _translations.Translate("BlameControl", "copyToClipboardToolStripMenuItem", "Text", "Copy to clipboard"), Icon = MenuIcon("CopyToClipboard.png"), ItemsSource = new[]
        {
            Copy("commitHashToolStripMenuItem", "Commit hash", "CommitId.png", (line, _) => Task.FromResult(line.Line.Hash)),
            Copy("commitMessageToolStripMenuItem", "Commit message", "Message.png", (line, _) => Task.FromResult(line.Line.Summary)),
            Copy("allCommitInfoToolStripMenuItem", "All commit info", "CommitSummary.png", GetCommitInformationAsync)
        } };
        ContextMenu menu = new() { Name = "BlameContextMenu", ItemsSource = new Control[] { thisRevision, previousRevision, changes, new Separator(), copy } };
        menu.Opening += (_, _) =>
        {
            ClearHover();
            GitFileBlameLine? line = _contextLine ?? SelectedLine;
            bool committed = line is not null && !line.Line.Hash.All(character => character == '0');
            thisRevision.IsEnabled = changes.IsEnabled = committed && IsEnabled;
            previousRevision.IsEnabled = committed && line!.PreviousHash is not null && IsEnabled;
            copy.IsEnabled = line is not null && IsEnabled;
        };
        menu.Closed += (_, _) => _contextLine = null;
        return menu;
    }

    public async Task<string> GetCommitInformationAsync(GitFileBlameLine line, CancellationToken token)
    {
        if (_commitInformation.TryGetValue((line.Line.Hash, line.OriginalPath), out string? cached)) { return cached; }
        string information = CommitInformationProvider is null ? BasicInformation(line) : await CommitInformationProvider(line, token);
        token.ThrowIfCancellationRequested();
        _commitInformation[(line.Line.Hash, line.OriginalPath)] = information;
        return information;
    }
    private string BasicInformation(GitFileBlameLine line) =>
        $"{_translations.Translate("Author")}: {line.Line.AuthorName}\n{_translations.Translate("Author date")}: {line.Line.AuthoredAt:G}\n{_translations.Translate("Commit hash")}: {line.Line.Hash[..Math.Min(9, line.Line.Hash.Length)]}\n{_translations.Translate("Summary")}: {line.Line.Summary}\n\n{_translations.Translate("FileName")}: {ExportDialogControls.DisplayPath(line.OriginalPath)}";

    private void Hover(GitFileBlameLine? line, bool showTooltip)
    {
        if (_hoveredHash != line?.Line.Hash) { _hoveredHash = line?.Line.Hash; RefreshDisplay(); }
        if (!showTooltip || line is null) { return; }
        if (_tooltipText.Tag is ValueTuple<string, string> current && current == (line.Line.Hash, line.OriginalPath)) { return; }
        _tooltipOperation?.Cancel();
        CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tooltipOperation = operation;
        _tooltipText.Tag = (line.Line.Hash, line.OriginalPath); _tooltipText.Text = BasicInformation(line);
        ToolTip.SetIsOpen(_authors, true);
        TooltipLoading = UpdateTooltipAsync(line, operation);
    }
    private async Task UpdateTooltipAsync(GitFileBlameLine line, CancellationTokenSource operation)
    {
        try
        {
            string text = await GetCommitInformationAsync(line, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _tooltipText.Text = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is GitCommandException or IOException or InvalidOperationException) { }
        finally { if (_tooltipOperation == operation) { _tooltipOperation = null; } operation.Dispose(); }
    }
    private void ClearHover()
    {
        _tooltipOperation?.Cancel(); _tooltipOperation = null;
        _hoveredHash = null; _tooltipText.Tag = null; ToolTip.SetIsOpen(_authors, false);
        RefreshDisplay();
    }
    private GitFileBlameLine? LineAt(Control control, double y)
    {
        var text = TextView.Editor.TextArea.TextView;
        if (!text.VisualLinesValid) { return null; }
        var visual = text.VisualLines.FirstOrDefault(line => y + text.VerticalOffset >= line.VisualTop && y + text.VerticalOffset < line.VisualTop + line.Height);
        return visual is null ? null : Lines.ElementAtOrDefault(visual.FirstDocumentLine.LineNumber - 1);
    }
    private string Label(string name, string fallback) => name == "UseHistogramDiffAlgorithm"
        ? _translations.Translate("GeneralSettingsPage", "chkUseHistogramDiffAlgorithm", "Text", fallback)
        : _translations.Translate("FormFileHistory", name, "Text", fallback);
    private Image? MenuIcon(string name)
    {
        Uri uri = new("avares://GitExtensions/Assets/Icons/" + name);
        if (!AssetLoader.Exists(uri)) { return null; }
        using Stream stream = AssetLoader.Open(uri); Bitmap icon = new(stream); _menuIcons.Add(icon);
        return new Image { Source = icon, Width = 16, Height = 16 };
    }

    private void EditorFontChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (_disposed || change.Property.Name is not ("FontSize" or "FontFamily" or "FontWeight" or "FontStyle" or "FontStretch")) { return; }
        _authors.InvalidateMeasure(); _authors.InvalidateVisual();
    }

    private sealed class AuthorMargin(BlameView owner) : AbstractMargin
    {
        private readonly Dictionary<int, (AvatarImageControl Control, Rect Bounds)> _avatarSlots = [];
        private bool _avatarLayoutQueued;

        internal void QueueAvatarLayout()
        {
            if (_avatarLayoutQueued || owner._disposed || owner._avatarClosing) { return; }
            _avatarLayoutQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _avatarLayoutQueued = false;
                if (!owner._disposed && !owner._avatarClosing) { UpdateAvatarPresenters(); }
            });
        }

        internal void ClearAvatarPresenters()
        {
            if (_avatarSlots.Count == 0) { return; }
            foreach (var slot in _avatarSlots.Values)
            { _ = slot.Control.BeginClose(); VisualChildren.Remove(slot.Control); }
            _avatarSlots.Clear(); InvalidateMeasure();
        }

        private void UpdateAvatarPresenters()
        {
            if (!owner.UsesSharedAvatars || !owner.ShowAvatar || !owner.IsEffectivelyVisible
                || TextView?.VisualLinesValid != true || Bounds.Height <= 0)
            { ClearAvatarPresenters(); return; }
            Dictionary<int, (GitBlameLine Line, Rect Bounds)> visible = [];
            foreach (VisualLine visual in TextView.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= owner.Lines.Count) { continue; }
                GitBlameLine line = owner.Lines[index].Line;
                if (index > 0 && owner.Lines[index - 1].Line.Hash == line.Hash
                    || line.Hash.All(character => character == '0') || string.IsNullOrWhiteSpace(line.AuthorEmail)) { continue; }
                double top = visual.VisualTop - TextView.VerticalOffset, size = Math.Min(visual.Height, 20);
                if (size <= 0 || top + size <= 0 || top >= Bounds.Height) { continue; }
                visible[index] = (line, new Rect(6, top, size, size));
            }
            bool changed = false;
            foreach (int index in _avatarSlots.Keys.Where(index => !visible.ContainsKey(index)).ToArray())
            {
                AvatarImageControl control = _avatarSlots[index].Control;
                _ = control.BeginClose(); VisualChildren.Remove(control); _avatarSlots.Remove(index); changed = true;
            }
            foreach (var item in visible)
            {
                int index = item.Key; var value = item.Value;
                if (!_avatarSlots.TryGetValue(index, out var slot))
                {
                    AvatarImageControl control = new(role: AvatarImageRole.Blame, placeholder: AvatarPlaceholder.User80)
                    { Name = "BlameAuthorAvatar", Tag = index };
                    control.ReadStarted += owner.TrackAvatarRead;
                    control.ImageChanged += InvalidateVisual;
                    VisualChildren.Add(control); slot = (control, value.Bounds); changed = true;
                }
                slot.Control.SetIdentity(value.Line.AuthorEmail, value.Line.AuthorName, value.Line.Hash + ":" + index);
                slot.Control.Width = value.Bounds.Width; slot.Control.Height = value.Bounds.Height;
                if (slot.Bounds != value.Bounds) { changed = true; }
                _avatarSlots[index] = (slot.Control, value.Bounds);
            }
            if (changed) { InvalidateMeasure(); InvalidateArrange(); InvalidateVisual(); }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var slot in _avatarSlots.Values) { slot.Control.Measure(slot.Bounds.Size); slot.Control.Arrange(slot.Bounds); }
            QueueAvatarLayout();
            return finalSize;
        }

        private (Typeface Face, double Size, int Digits)? _numberMetrics;
        private double _numberWidth;
        private double NumberWidth()
        {
            if (!owner.ShowLineNumbers) { return 0; }
            var editor = owner.TextView.Editor;
            Typeface face = new(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
            int digits = owner.Lines.Count.ToString(CultureInfo.InvariantCulture).Length;
            var key = (face, editor.FontSize, digits);
            if (_numberMetrics is not { } previous || !previous.Equals(key))
            {
                _numberWidth = new FormattedText(new string('9', digits) + " ", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, editor.FontSize, editor.Foreground).WidthIncludingTrailingWhitespace;
                _numberMetrics = key;
            }
            return _numberWidth;
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            var editor = owner.TextView.Editor;
            FormattedText sample = new("By yyyy-MM-dd HH:mm:ss 0000000000", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch),
                editor.FontSize, editor.Foreground);
            return new(Math.Max(290, Math.Ceiling(sample.WidthIncludingTrailingWhitespace) + 28), 0);
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(owner.TextView.Editor.Background, null, new Rect(Bounds.Size));
            if (TextView?.VisualLinesValid != true) { return; }
            double numberWidth = NumberWidth();
            using (context.PushClip(new Rect(Bounds.Size)))
            foreach (VisualLine visual in TextView.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= owner.Lines.Count) { continue; }
                GitFileBlameLine line = owner.Lines[index];
                double top = visual.VisualTop - TextView.VerticalOffset;
                if (owner.IsSelected(line)) { context.DrawRectangle(owner.GroupBrush, null, new Rect(0, top, Bounds.Width, visual.Height)); }
                context.DrawRectangle(owner._ages[index], null, new Rect(0, top, 4, visual.Height));
                bool first = index == 0 || owner.Lines[index - 1].Line.Hash != line.Line.Hash;
                double textY = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                double avatarWidth = owner.ShowAvatar ? Math.Min(visual.Height, 20) : 0;
                if (owner.ShowLineNumbers)
                {
                    FormattedText number = new((index + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(owner.TextView.Editor.FontFamily, owner.TextView.Editor.FontStyle, owner.TextView.Editor.FontWeight, owner.TextView.Editor.FontStretch), owner.TextView.Editor.FontSize, owner.TextView.Editor.Foreground);
                    context.DrawText(number, new Point(8 + avatarWidth, textY));
                }
                if (!first) { continue; }
                Bitmap? avatar = owner.UsesSharedAvatars ? owner._avatar
                    : owner._avatars.GetValueOrDefault(line.Line.AuthorEmail.Trim()) ?? owner._avatar;
                if (owner.ShowAvatar && avatar is not null && (!owner.UsesSharedAvatars || !_avatarSlots.ContainsKey(index)))
                { context.DrawImage(avatar, new Rect(avatar.Size), new Rect(6, top, avatarWidth, avatarWidth)); }
                string text = owner.GetAuthorLine(line);
                FormattedText author = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(owner.TextView.Editor.FontFamily, owner.TextView.Editor.FontStyle, owner.TextView.Editor.FontWeight, owner.TextView.Editor.FontStretch), owner.TextView.Editor.FontSize, owner.TextView.Editor.Foreground);
                context.DrawText(author, new Point(8 + avatarWidth + numberWidth, textY));
            }
        }
        protected override void OnPointerPressed(PointerPressedEventArgs args)
        {
            base.OnPointerPressed(args);
            if (TextView?.VisualLinesValid != true) { return; }
            double y = args.GetPosition(this).Y + TextView.VerticalOffset;
            VisualLine? line = TextView.VisualLines.FirstOrDefault(line => y >= line.VisualTop && y < line.VisualTop + line.Height);
            if (line is not null)
            {
                if (args.GetCurrentPoint(this).Properties.IsRightButtonPressed) { owner._contextLine = owner.Lines[line.FirstDocumentLine.LineNumber - 1]; }
                else if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { owner.SelectLine(line.FirstDocumentLine.LineNumber); args.Handled = true; }
            }
        }
        protected override void OnPointerMoved(PointerEventArgs args)
        {
            base.OnPointerMoved(args);
            double y = args.GetPosition(this).Y;
            ToolTip.SetVerticalOffset(this, y + 20);
            owner.Hover(owner.LineAt(this, y), showTooltip: true);
        }
        protected override void OnPointerExited(PointerEventArgs args)
        { base.OnPointerExited(args); owner.ClearHover(); }
    }
    private sealed class CommitBackground(BlameView owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext context)
        {
            if (!view.VisualLinesValid) { return; }
            foreach (VisualLine visual in view.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index < owner.Lines.Count && owner.IsSelected(owner.Lines[index]))
                { context.DrawRectangle(owner.GroupBrush, null, new Rect(0, visual.VisualTop - view.VerticalOffset, view.Bounds.Width, visual.Height)); }
            }
        }
    }
}
