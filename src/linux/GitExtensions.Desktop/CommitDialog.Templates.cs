using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private CommitMessagePreferencesStore _messagePreferencesStore = null!;
    private CommitMessagePreferences _messagePreferences = new();
    private readonly TextBlock _templateNotice = new() { Name = "CommitTemplateNotice", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private MenuItem _templatesMenu = null!, _conventionalMenu = null!;
    private MenuKeyboardSelection _templateKeyboardSelection = null!;
    private bool _formattingMessage, _conventionalScope;
    private long _messageFormatGeneration;

    private void InitializeMessageTemplates(string profileRoot)
    {
        _messagePreferencesStore = new(profileRoot); ReadMessagePreferences();
        Opened += (_, _) => { CommitMessagePreferencesStore.Saved += MessagePreferencesSaved; ReadMessagePreferences(); };
        Closed += (_, _) => CommitMessagePreferencesStore.Saved -= MessagePreferencesSaved;
        _spelling.AdditionalItems = () => new object[] { MenuAction("WordWrapCommitMessageBody", C("_wordWrapCommitMessageBody", "Word wrap (except subject line)"), () =>
        { if (!IsBusy) { FormatMessage(manual: true); } return Task.CompletedTask; }) };
        _spellingView.CompositionChanged += (_, _) => MessageTextChanged();
    }
    private void MessagePreferencesSaved(string path)
    {
        if (path != _messagePreferencesStore.FilePath) { return; }
        void Accept()
        {
            if (_lifetime.IsCancellationRequested || !IsVisible) { return; }
            ReadMessagePreferences(); PaintMessageLimits();
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Accept(); }
        else { Avalonia.Threading.Dispatcher.UIThread.Post(Accept); }
    }
    private void TemplateNotice(string? text)
    { _templateNotice.Text = text; _templateNotice.IsVisible = !string.IsNullOrEmpty(text); }
    private bool ReadMessagePreferences()
    {
        try { _messagePreferences = _messagePreferencesStore.Load(); TemplateNotice(null); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { TemplateNotice(error.Message); return false; }
    }
    private MenuItem BuildTemplatesMenu()
    {
        _templatesMenu = new() { Name = "commitTemplatesToolStripMenuItem", Header = TemplateMenuHeader("commitTemplatesToolStripMenuItem", "Commit &templates", "CommitTemplates.png") };
        _templateKeyboardSelection = new(this, _templatesMenu);
        _templatesMenu.SubmenuOpened += (_, args) => { if (ReferenceEquals(args.Source, _templatesMenu)) { RefreshTemplatesMenu(); } };
        RefreshTemplatesMenu(); return _templatesMenu;
    }
    private void RefreshTemplatesMenu()
    {
        ReadMessagePreferences(); List<object> items = [];
        try
        {
            foreach (var entry in CommitTemplateRegistry.Snapshot()) { Add(entry.Template, entry.Icon, "RegisteredCommitTemplate"); }
        }
        catch (Exception error) { TemplateNotice(error.Message); }
        if (items.Count > 0) { items.Add(new Separator()); }
        int start = items.Count;
        foreach (var template in _messagePreferences.Templates) { Add(template, null, "UserCommitTemplate"); }
        if (items.Count > start) { items.Add(new Separator()); }
        _conventionalMenu = new() { Name = "ConventionalCommits", Header = TemplateMenuHeader("_conventionalCommit", "Conven&tional Commits", "GitCommandLog.png") };
        List<object> conventional = [];
        foreach (string type in CommitMessageFormatting.HeaderTypes)
        {
            MenuItem entry = MenuAction("Conventional_" + type, type, () => { ApplyConventionalHeader(type); return Task.CompletedTask; });
            if (type == "feat") { DesktopHotkeys.Bind(entry, "Commit", "ConventionalCommit_PrefixMessage");
                string shortcuts = string.Join(", ", new[] { DesktopHotkeys.Get("Commit", "ConventionalCommit_PrefixMessage"), DesktopHotkeys.Get("Commit", "ConventionalCommit_PrefixMessageWithScope") }.Where(gesture => gesture is not null));
                entry.TemplateApplied += (_, args) =>
                {
                    if (args.NameScope.Find<TextBlock>("PART_InputGestureText") is { } hint)
                    { hint.Text = shortcuts; hint.IsVisible = shortcuts.Length > 0; }
                }; }
            conventional.Add(entry);
        }
        conventional.Add(new Separator());
        foreach (string footer in CommitMessageFormatting.FooterTypes)
        {
            conventional.Add(MenuAction("ConventionalFooter_" + footer, footer, () => { AppendConventionalFooter(footer + ": ", false); return Task.CompletedTask; }));
        }
        conventional.Add(MenuAction("ConventionalSkipCi", "[skip ci]", () => { AppendConventionalFooter("[skip ci]", true); return Task.CompletedTask; }));
        conventional.Add(new Separator());
        MenuItem documentation = MenuAction("ConventionalDocumentation", C("_conventionalCommitDocumentation", "Documentation..."), () =>
        {
            try { OpenConventionalDocumentation(new Uri("https://www.conventionalcommits.org")); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { TemplateNotice(error.Message); }
            return Task.CompletedTask;
        });
        documentation.Header = TemplateMenuHeader("_conventionalCommitDocumentation", "Documentation...", "Information.png"); conventional.Add(documentation);
        _conventionalMenu.ItemsSource = conventional; items.Add(_conventionalMenu); items.Add(new Separator());
        MenuItem settings = MenuAction("CommitMessageSettings", C("_commitMessageSettings", "Edit commit message templates and settings..."), OpenMessageSettingsAsync);
        settings.Header = TemplateMenuHeader("_commitMessageSettings", "&Edit commit message templates and settings...", "Settings.png"); items.Add(settings);
        // Keep the already-supported explicit file loader, separate from the application library.
        items.Add(MenuAction("LoadTemplate", T("Load configured template"), LoadTemplateAsync));
        _templatesMenu.ItemsSource = items;
        void Add(CommitMessageTemplate template, string? icon, string name)
        {
            if (template.Name.Length == 0) { return; }
            MenuItem item = MenuAction(name, template.Name, () => ApplyMessageTemplateAsync(template)); item.Tag = template;
            if (icon is not null) { item.Header = IconLabel(template.Name, icon); }
            items.Add(item);
        }
    }
    private Control TemplateMenuHeader(string item, string source, string icon)
    {
        Grid content = (Grid)IconLabel("", icon);
        AccessText text = new() { Text = _translations.TranslateAccessKeys("FormCommit", item, "Text", source), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1); content.Children.Add(text); return content;
    }
    internal Action<Uri> OpenConventionalDocumentation { get; set; } = uri => Process.Start(new ProcessStartInfo("xdg-open")
        { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } });

    private async Task OpenMessageSettingsAsync()
    {
        if (IsBusy || _confirmationOpen || _lifetime.IsCancellationRequested) { return; }
        _confirmationOpen = true;
        try
        {
            CommitMessageSettingsDialog dialog = new(_messagePreferencesStore) { RequestedThemeVariant = ActualThemeVariant };
            if (await dialog.ShowDialog<bool>(this) && IsVisible && !_lifetime.IsCancellationRequested)
            { _messagePreferences = dialog.Result; TemplateNotice(null); PaintMessageLimits(); }
        }
        finally { _confirmationOpen = false; }
    }
    private async Task ApplyMessageTemplateAsync(CommitMessageTemplate template)
    {
        if (IsBusy || _lifetime.IsCancellationRequested) { return; }
        // Capture the chosen value before any Git await. File-template state remains untouched.
        await RunAsync(T("Commit templates"), async () =>
        {
            string branch = "";
            if (template.IsRegex)
            {
                GitResult current = await new GitRunner(_repository.ExecutionEnvironment).RunAsync(_repository.Path, ["symbolic-ref", "--quiet", "HEAD"], OperationToken);
                if (current.ExitCode == 1) { branch = "(no branch)"; }
                else if (!current.Success) { throw new GitCommandException(new[] { "symbolic-ref", "--quiet", "HEAD" }, current); }
                else { branch = current.StandardOutput.TrimEnd('\r', '\n'); if (branch.StartsWith("refs/heads/", StringComparison.Ordinal)) { branch = branch[11..]; } }
            }
            OperationToken.ThrowIfCancellationRequested();
            CommitTemplateExpansion result = CommitMessageFormatting.Expand(template, branch);
            ReplaceMessageSelection(0, (_message.Text ?? "").Length, result.Text);
            TemplateNotice(result.Diagnostic);
        });
        if (IsVisible && !_lifetime.IsCancellationRequested && !_closeWhenIdle) { _message.Focus(); }
    }
    private void ReplaceMessageSelection(int start, int length, string value)
    {
        string text = _message.Text ?? "";
        if (start < 0 || start + length > text.Length || text.AsSpan(start, length).SequenceEqual(value)) { return; }
        _message.SelectionStart = start; _message.SelectionEnd = start + length; _message.SelectedText = value;
    }
    private void MessageTextChanged()
    {
        long generation = ++_messageFormatGeneration;
        PaintMessageLimits();
        if (_formattingMessage || _message.IsReplaying || _spellingView.IsComposing) { return; }
        string text = _message.Text ?? ""; long interaction = _message.InteractionVersion;
        bool Current() => generation == _messageFormatGeneration && interaction == _message.InteractionVersion
            && text == (_message.Text ?? "") && !_message.IsReplaying && !_spellingView.IsComposing
            && !_lifetime.IsCancellationRequested && !_spellingClosing && !_closeWhenIdle && IsVisible;
        // TextProperty changes before TextBox finishes its native input/caret/undo update.
        // Acquire that final native selection only after the edit returns, then revalidate it before applying.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!Current()) { return; }
            int caret = _message.CaretIndex, start = _message.SelectionStart, end = _message.SelectionEnd;
            bool focused = _message.IsKeyboardFocusWithin;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!Current() || caret != _message.CaretIndex || start != _message.SelectionStart
                    || end != _message.SelectionEnd || focused != _message.IsKeyboardFocusWithin) { return; }
                FormatMessage();
            });
        });
    }
    private void FormatMessage(bool manual = false)
    {
        if (_formattingMessage || _message.IsReplaying || _spellingView.IsComposing || _lifetime.IsCancellationRequested) { return; }
        string text = _message.Text ?? ""; var edits = CommitMessageFormatting.Format(text, _messagePreferences, manual);
        if (edits.Count == 0) { return; }
        int start = _message.SelectionStart, end = _message.SelectionEnd, caret = _message.CaretIndex;
        _formattingMessage = true;
        try
        {
            foreach (var edit in edits.Reverse()) { ReplaceMessageSelection(edit.Start, edit.Length, edit.Text); }
            int length = (_message.Text ?? "").Length;
            _message.CaretIndex = Math.Clamp(CommitMessageFormatting.MapPosition(caret, edits), 0, length);
            _message.SelectionStart = Math.Clamp(CommitMessageFormatting.MapPosition(start, edits), 0, length);
            _message.SelectionEnd = Math.Clamp(CommitMessageFormatting.MapPosition(end, edits), 0, length);
        }
        finally { _formattingMessage = false; }
        PaintMessageLimits();
    }
    private void PaintMessageLimits()
    {
        string text = _message.Text ?? ""; List<SpellingRange> ranges = [];
        var lines = CommitMessageFormatting.Lines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            int limit = i == 0 ? _messagePreferences.MaxFirstLineLength : _messagePreferences.SecondLineMustBeEmpty && i == 1 ? 0 : _messagePreferences.MaxLineLength;
            if (limit > 0 && lines[i].Text.Length > limit) { ranges.Add(new(lines[i].Start + limit, lines[i].Text.Length - limit)); }
        }
        _spellingView.SetValidationRanges(text, ranges);
    }
    private Task OpenConventionalMenuAsync(bool withScope)
    {
        _conventionalScope = withScope; _templateKeyboardSelection.Begin();
        _commitToolbarOverflow?.Reveal(_templatesMenu); _templatesMenu.Open();
        if (_templatesMenu.Parent is Menu toolbar) { toolbar.SelectedItem = _templatesMenu; }
        _templatesMenu.SelectedItem = _conventionalMenu; _conventionalMenu.Open();
        MenuItem feat = _conventionalMenu.Items.OfType<MenuItem>().Single(item => item.Name == "Conventional_feat");
        _conventionalMenu.SelectedItem = feat; feat.Focus(NavigationMethod.Directional);
        return Task.CompletedTask;
    }
    private void ApplyConventionalHeader(string keyword)
    {
        if (IsBusy || !_message.IsEnabled || _lifetime.IsCancellationRequested) { return; }
        string message = _message.Text ?? "";
        (string title, int caret) = CommitMessageFormatting.PrefixKeyword(message, _message.CaretIndex, keyword, _conventionalScope);
        ReplaceMessageSelection(0, CommitMessageFormatting.Lines(message)[0].Text.Length, title);
        _message.CaretIndex = Math.Clamp(caret, 0, (_message.Text ?? "").Length);
        _message.SelectionStart = _message.SelectionEnd = _message.CaretIndex; _message.Focus();
    }
    private void AppendConventionalFooter(string footer, bool keepCaret)
    {
        if (IsBusy || !_message.IsEnabled || _lifetime.IsCancellationRequested) { return; }
        int start = _message.SelectionStart, end = _message.SelectionEnd, caret = _message.CaretIndex;
        string message = _message.Text ?? ""; ReplaceMessageSelection(message.Length, 0, Environment.NewLine + footer);
        _message.CaretIndex = keepCaret ? Math.Min(caret, (_message.Text ?? "").Length) : (_message.Text ?? "").Length;
        _message.SelectionStart = keepCaret ? start : _message.CaretIndex; _message.SelectionEnd = keepCaret ? end : _message.CaretIndex; _message.Focus();
    }
    private async Task<bool> ValidateCommitMessageAsync()
    {
        bool preferencesRead = ReadMessagePreferences(); // Fresh at commit; publication keeps formatting current without per-keystroke IO.
        if (_kind.SelectedIndex == 1) { return true; } // Git generates a fixup message; the disabled editor is not the message source.
        string text = _message.Text ?? "";
        if (_kind.SelectedIndex == 2) { text = "squash! " + text; }
        var warnings = CommitMessageFormatting.Warnings(text, _messagePreferences, out string? diagnostic);
        if (preferencesRead) { TemplateNotice(diagnostic); }
        else if (diagnostic is not null) { TemplateNotice(_templateNotice.Text + Environment.NewLine + diagnostic); }
        foreach (var warning in warnings)
        {
            if (_lifetime.IsCancellationRequested || !IsVisible) { return false; }
            string message = C(warning.Item, warning.Source);
            if (warning.Line is not null) { message = string.Format(CultureInfo.CurrentCulture, message, warning.Line); }
            if (!await ConfirmCommitWarningAsync(message)) { return false; }
            if (_lifetime.IsCancellationRequested || !IsVisible) { return false; }
        }
        return true;
    }
    private async Task<bool> ConfirmCommitWarningAsync(string message)
    {
        Window dialog = new() { Name = "CommitValidation", Title = C("_commitValidationCaption", "Commit validation"), Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button yes = new() { Name = "CommitValidationYes", Content = T("Yes"), IsDefault = true, MinWidth = 75 };
        Button no = new() { Name = "CommitValidationNo", Content = T("No"), IsCancel = true, MinWidth = 75 };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        Grid warningBody = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        warningBody.Children.Add(IconLabel("", "Warning.png"));
        TextBlock warningText = new() { Name = "CommitValidationMessage", Text = message, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(warningText, 1); warningBody.Children.Add(warningText);
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            warningBody, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { yes, no } }
        } };
        dialog.Opened += (_, _) => { ModalWindowActivation.AfterMapping(dialog, () => IsVisible); yes.Focus(); };
        return await dialog.ShowDialog<bool>(this);
    }
}
