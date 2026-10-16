using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The three independent revision sources from Windows FormGoToCommit.</summary>
public sealed class GoToCommitDialog : Window
{
    private readonly GitRepository _repository;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _expression = new() { Name = "textboxCommitExpression", Height = 23, MinHeight = 23, TabIndex = 0 };
    private readonly ComboBox _tags = RepositorySourceControls.EditableChoice("comboBoxTags");
    private readonly ComboBox _branches = RepositorySourceControls.EditableChoice("comboBoxBranches");
    private readonly TextBlock _status = new() { Name = "GoToCommitStatus", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _go;
    private readonly Func<CancellationToken, Task<string?>> _readClipboard;
    private IReadOnlyList<GitReference> _tagRefs = [], _branchRefs = [];
    private Control _source;
    private Task? _initialization, _validation;
    private int _expressionVersion;
    private bool _closed;

    public GoToCommitDialog(GitRepository repository, Action<Uri>? openLink = null,
        Func<CancellationToken, Task<string?>>? readClipboard = null)
    {
        _repository = repository; _source = _expression;
        _readClipboard = readClipboard ?? (async token => Clipboard is null ? null : await ClipboardExtensions.TryGetTextAsync(Clipboard).WaitAsync(token));
        openLink ??= uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } });
        Name = "GoToCommitDialog"; Title = Original("$this", "Go to commit");
        Width = MinWidth = 604; Height = MinHeight = 302; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; CanMinimize = CanMaximize = false;
        _tags.TabIndex = 1; _branches.TabIndex = 2;
        _go = new() { Name = "goButton", Content = Original("goButton", "Go"), IsDefault = true, TabIndex = 3, Height = 28, MinWidth = 75 };
        _go.Click += async (_, _) => await GoAsync();
        _expression.GotFocus += (_, _) => _source = _expression;
        _tags.GotFocus += (_, _) => _source = _tags;
        _branches.GotFocus += (_, _) => _source = _branches;
        _expression.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { _expressionVersion++; } };

        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        Grid first = new() { Margin = new Thickness(12, 10, 8, 0), VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions = new ColumnDefinitions("143,*,81"), Height = 28 };
        TextBlock expressionLabel = Caption("label1", "Commit expression:"); first.Children.Add(expressionLabel);
        Grid.SetColumn(_expression, 1); first.Children.Add(_expression);
        Grid.SetColumn(_go, 2); _go.Margin = new Thickness(6, 0, 0, 0); first.Children.Add(_go); root.Children.Add(first);

        Button help = new()
        {
            Name = "linkGitRevParse", Content = new TextBlock { Text = Original("linkGitRevParse", "More see git-rev-parse"), TextDecorations = TextDecorations.Underline },
            Background = Brushes.Transparent, BorderThickness = default, Padding = default, MinHeight = 18,
            HorizontalAlignment = HorizontalAlignment.Left, TabIndex = 4
        };
        help.Click += (_, _) => openLink(new Uri("https://git-scm.com/docs/git-rev-parse#_specifying_revisions"));
        void HelpColor() => help.Foreground = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Brush.Parse("#78b7ff") : Brushes.Blue;
        ActualThemeVariantChanged += (_, _) => HelpColor(); HelpColor();
        StackPanel helpBody = new() { Margin = new Thickness(12, 6, 6, 4), Spacing = 9, Children =
        {
            new TextBlock { Name = "label2", Text = Original("label2", "Commit expression examples:\n- complete commit hash: e. g.: 8eab51fcb9c4538eb74c4dcd4c31ffd693ad25c9\n- partial commit hash (if unique): e. g.: 8eab51fcb9c453\n- tag name\n- branch name"), TextWrapping = TextWrapping.Wrap }, help
        } };
        Grid group = ClassicChrome.Group("groupBox1", Original("groupBox1", "Help"), helpBody);
        group.Margin = new Thickness(45, 5, 89, 0); group.VerticalAlignment = VerticalAlignment.Top; group.MinHeight = 141; Grid.SetRow(group, 1); root.Children.Add(group);
        AddChoice(root, _tags, "label3", "Go to tag:", row: 2, top: 32);
        AddChoice(root, _branches, "label4", "Go to branch:", row: 3, top: 19);
        _status.Margin = new Thickness(12, 2, 8, 4); _status.VerticalAlignment = VerticalAlignment.Top; Grid.SetRow(_status, 4); root.Children.Add(_status);
        Content = root;
        RepositorySourceControls.CompleteHistory(_tags, root, value => CommitChoice(_tags, value), StringComparison.OrdinalIgnoreCase);
        RepositorySourceControls.CompleteHistory(_branches, root, value => CommitChoice(_branches, value), StringComparison.OrdinalIgnoreCase);
        Opened += (_, _) =>
        {
            _expression.Focus();
            ModalWindowActivation.AfterMapping(this, () => !_closed);
        };
        Closing += (_, _) => { _closed = true; _lifetime.Cancel(); };
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    public bool WasAccepted { get; private set; }
    public string? SelectedCommitHash { get; private set; }
    public string? SelectedExpression { get; private set; }
    public Exception? ResolutionError { get; private set; }
    public bool IsValidating { get; private set; }
    public bool IsLoadingTags { get; private set; }
    public bool IsLoadingBranches { get; private set; }
    public Task Completion => Task.WhenAll(_initialization ?? Task.CompletedTask, _validation ?? Task.CompletedTask);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _initialization ??= InitializeCoreAsync(cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        int version = _expressionVersion;
        await Task.WhenAll(LoadReferencesAsync(_tags, tags: true, read.Token), LoadReferencesAsync(_branches, tags: false, read.Token),
            ReadClipboardAsync(version, read.Token));
    }

    private async Task LoadReferencesAsync(ComboBox choice, bool tags, CancellationToken token)
    {
        if (tags) { IsLoadingTags = true; } else { IsLoadingBranches = true; }
        choice.IsEnabled = false; choice.PlaceholderText = _translations.Translate("Loading data...");
        try
        {
            // Independent, bounded native reads keep the expression editable and do not depend on the grid's filters.
            IReadOnlyList<GitReference> refs = await _repository.GetGoToCommitReferencesAsync(
                tags ? GitGoToCommitReferenceKind.Tag : GitGoToCommitReferenceKind.LocalBranch, token);
            if (_closed || token.IsCancellationRequested) { return; }
            if (tags) { _tagRefs = refs; } else { _branchRefs = refs; }
            string? draft = choice.Text; choice.ItemsSource = refs.Select(reference => reference.ShortName).ToArray(); choice.SelectedIndex = -1; choice.Text = draft;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (!_closed && !token.IsCancellationRequested) { _status.Text = exception.Message; } }
        finally
        {
            if (tags) { IsLoadingTags = false; } else { IsLoadingBranches = false; }
            if (!_closed) { choice.PlaceholderText = ""; choice.IsEnabled = !IsValidating; }
        }
    }

    private async Task ReadClipboardAsync(int version, CancellationToken token)
    {
        try
        {
            string value = (await _readClipboard(token) ?? "").Trim();
            if (value.Length == 0) { return; }
            await _repository.ResolveRevisionAsync(value, token);
            if (_closed || token.IsCancellationRequested || _expressionVersion != version || _source != _expression || IsValidating) { return; }
            _expression.Text = value; _expression.SelectAll();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* Clipboard contents are only a proposal; an invalid or unavailable clipboard must remain silent. */ }
    }

    private async void CommitChoice(ComboBox choice, string value)
    {
        if (_closed || IsValidating) { return; }
        _source = choice; choice.Text = value; await GoAsync();
    }

    private Task GoAsync()
    {
        if (_closed || IsValidating) { return _validation ?? Task.CompletedTask; }
        return _validation = ValidateAndCloseAsync();
    }

    private async Task ValidateAndCloseAsync()
    {
        IsValidating = true;
        string revision = _source == _expression ? (_expression.Text ?? "").Trim()
            : (_source == _tags ? _tagRefs : _branchRefs).FirstOrDefault(reference => reference.ShortName == (_source == _tags ? _tags.Text : _branches.Text))?.Hash ?? "";
        SelectedExpression = revision;
        _go.IsEnabled = _expression.IsEnabled = _tags.IsEnabled = _branches.IsEnabled = false;
        try
        {
            if (revision.Length > 0) { SelectedCommitHash = await _repository.ResolveRevisionAsync(revision, _lifetime.Token); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception exception) { ResolutionError = exception; }
        finally
        {
            IsValidating = false;
            if (!_closed)
            {
                _go.IsEnabled = _expression.IsEnabled = true;
                _tags.IsEnabled = !IsLoadingTags; _branches.IsEnabled = !IsLoadingBranches;
            }
        }
        if (!_closed && !_lifetime.IsCancellationRequested) { WasAccepted = true; Close(); }
    }

    private TextBlock Caption(string key, string text) => new() { Text = Original(key, text), VerticalAlignment = VerticalAlignment.Center };
    private string Original(string key, string text) => _translations.Translate("FormGoToCommit", key, "Text", text);
    private void AddChoice(Panel root, ComboBox choice, string label, string text, int row, double top)
    {
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions("143,*,154"), Margin = new Thickness(12, top, 8, 0), VerticalAlignment = VerticalAlignment.Top, Height = 23 };
        Grid.SetRow(line, row); line.Children.Add(Caption(label, text)); Grid.SetColumn(choice, 1); line.Children.Add(choice); root.Children.Add(line);
    }
}
