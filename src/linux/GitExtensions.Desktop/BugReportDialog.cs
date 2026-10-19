using Avalonia.Input.Platform;
using GitExtensions.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class BugReportDialog : Window
{
    private readonly ExceptionReportSnapshot _snapshot;
    private readonly ExceptionReportContext _context;
    private readonly TranslationCatalog _translations;
    private readonly Func<string, CancellationToken, Task>? _copyText;
    private readonly Func<Uri, CancellationToken, Task>? _openIssue;
    private readonly CancellationTokenSource _lifetime;
    private readonly TextBox _description = new() { Name = "descriptionTextBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, ClearSelectionOnLostFocus = false };
    private readonly TextBlock _notice = new() { Name = "BugReportNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Button _copy, _send;
    private readonly ExceptionDetailsControl _details;
    private Task _work = Task.CompletedTask;
    private bool _busy, _closing, _closed, _disposed, _closeScheduled;
    private BugReportDecision _decision;
    internal Task Completion => _work;
    internal bool IsBusy => _busy;
    internal ExceptionReportSnapshot Snapshot => _snapshot;

    internal BugReportDialog(ExceptionReportSnapshot snapshot, ExceptionReportContext context, TranslationCatalog translations,
        Func<string, CancellationToken, Task>? copyText = null, Func<Uri, CancellationToken, Task>? openIssue = null,
        CancellationToken cancellationToken = default)
    {
        _snapshot = snapshot; _context = context; _translations = translations; _copyText = copyText; _openIssue = openIssue;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Name = "BugReportForm"; Title = "Git Extensions " + Text("_title", "Error Report");
        Width = 558; Height = 469; MinWidth = 420; MinHeight = 340; CanMinimize = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        DesktopFonts.Apply(this, DesktopFontRole.Application);
        _details = new(snapshot.Root, translations, property => StartAsync(() => OpenPropertyAsync(property)));
        TabControl tabs = new() { Name = "mainTabs", Items =
        {
            new TabItem { Name = "generalTabPage", Header = Text("generalTabPage", "General"), Content = General() },
            new TabItem { Name = "exceptionTabPage", Header = Text("exceptionTabPage", "Exception"), Content = _details, Padding = new Thickness(3) }
        }, SelectedIndex = context.FocusDetails ? 1 : 0 };
        _copy = Command("btnCopy", "&Copy", "CopyToClipboard");
        _send = Command("sendAndQuitButton", "&Send and Quit", "github");
        Button ignore = Command("IgnoreButton", "&Ignore"); ignore.IsVisible = context.ShowIgnore; ignore.IsEnabled = context.CanIgnore;
        Button quit = new HyperlinkButton { Name = "quitButton", Content = new AccessText { Text = _translations.TranslateAccessKeys("BugReportForm", "quitButton", "Text", "&Quit") }, IsCancel = true, MinHeight = 25 };
        ToolTip.SetTip(_copy, Text("_toolTipCopy", "Copy the issue details into clipboard"));
        ToolTip.SetTip(_send, Text("_toolTipSendQuit", "Report the issue to GitHub and quit application.\nA valid GitHub account is required"));
        ToolTip.SetTip(quit, Text("_toolTipQuit", "Quit application without reporting the issue"));
        _copy.Click += async (_, _) => { string report = _context.CopyText(_snapshot, _description.Text); await StartAsync(() => CopyAsync(report)); };
        _send.Click += async (_, _) => { string description = _description.Text ?? ""; await StartAsync(() => SendAsync(description)); };
        ignore.Click += (_, _) => RequestDecision(BugReportDecision.Ignore);
        quit.Click += (_, _) => RequestDecision(BugReportDecision.Quit);
        Grid footer = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(4, 0) };
        _copy.HorizontalAlignment = HorizontalAlignment.Left; footer.Children.Add(_copy);
        foreach ((Control control, int column) in new[] { ((Control)ignore, 1), (_send, 2), (quit, 3) })
        { Grid.SetColumn(control, column); footer.Children.Add(control); }
        Grid root = new() { RowDefinitions = new("*,Auto,Auto"), Margin = new Thickness(4) };
        root.Children.Add(tabs); Grid.SetRow(_notice, 1); root.Children.Add(_notice); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Opened += (_, _) =>
        {
            UpdateActions();
            if (context.FocusDetails) { _details.FocusDetails(); } else { _description.Focus(); }
            ModalWindowActivation.AfterMapping(this, () => !_closing && !_lifetime.IsCancellationRequested);
        };
        Closing += (_, args) =>
        {
            if (!context.CanIgnore && _decision == BugReportDecision.Dismiss && !_closing && !_lifetime.IsCancellationRequested)
            { args.Cancel = true; return; }
            BeginClose();
            if (!_work.IsCompleted) { args.Cancel = true; ScheduleClose(); }
        };
        Closed += (_, _) => _closed = true;
        UpdateActions();
    }

    private Control General()
    {
        StackPanel heading = new() { Spacing = 5, Margin = new Thickness(8) };
        // This is a recoverable report. Reusing the original fatal-crash warning would be false.
        Grid warning = new() { ColumnDefinitions = new("32,8,*") };
        Image icon = ResetDialogControls.Icon("Warning"); icon.Width = icon.Height = 32; warning.Children.Add(icon);
        TextBlock explanation = new() { Name = "warningLabel", Text = _context.FocusDetails
            ? _translations.Translate("TranslatedStrings", "_externalErrorDescription", "Text", "error from git or from other external application")
            : _translations.Translate("TranslatedStrings", "_reportBugText", "Text", "If you think this was caused by Git Extensions, you can report a bug for the team to investigate."), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(explanation, 2); warning.Children.Add(explanation); heading.Children.Add(warning);
        Grid type = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
        type.Children.Add(new TextBlock { Name = "exceptionTypeLabel", Text = Text("exceptionTypeLabel", "Exception Type:"), VerticalAlignment = VerticalAlignment.Center });
        TextBox typeValue = ExceptionDetailDialog.ReadOnly("exceptionTextBox", _snapshot.Root.Type); Grid.SetColumn(typeValue, 1); type.Children.Add(typeValue); heading.Children.Add(type);
        TextBox message = ExceptionDetailDialog.ReadOnly("exceptionMessageTextBox", _snapshot.Root.Message, true); message.Height = 44; heading.Children.Add(message);
        Grid fields = new() { ColumnDefinitions = new("Auto,*,Auto,*"), ColumnSpacing = 5, RowDefinitions = new("Auto,Auto,Auto,8,Auto,*"), Margin = new Thickness(8, 0) };
        Field("targetSiteLabel", "Target Site:", "targetSiteTextBox", _snapshot.Root.TargetSite, 0, 0, 3);
        Field("applicationLabel", "Application:", "applicationTextBox", "Git Extensions [" + _context.ApplicationVersion + "]", 1, 0);
        Field("_NO_TRANSLATE_GitLabel", "Git:", "gitTextBox", _context.GitVersion ?? "???", 1, 2);
        Field("dateTimeLabel", "Date/Time:", "dateTimeTextBox", _context.OccurredAt.ToString("G", _translations.CurrentCulture), 2, 0);
        Field("_NO_TRANSLATE_ClrLabel", "CLR:", "clrTextBox", _context.Runtime, 2, 2);
        TextBlock descriptionLabel = new() { Name = "errorDescriptionLabel", Text = Text("errorDescriptionLabel", "Please add a brief description (in English) of how we can reproduce the error:"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 3) };
        Grid.SetRow(descriptionLabel, 4); Grid.SetColumnSpan(descriptionLabel, 4); fields.Children.Add(descriptionLabel);
        Grid.SetRow(_description, 5); Grid.SetColumnSpan(_description, 4); fields.Children.Add(_description);
        Grid result = new() { RowDefinitions = new("Auto,*") }; result.Children.Add(heading); Grid.SetRow(fields, 1); result.Children.Add(fields); return result;
        void Field(string labelName, string label, string name, string value, int row, int column, int span = 1)
        {
            TextBlock caption = new() { Name = labelName, Text = Text(labelName, label), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            TextBox text = ExceptionDetailDialog.ReadOnly(name, value);
            Grid.SetRow(caption, row); Grid.SetColumn(caption, column); fields.Children.Add(caption);
            Grid.SetRow(text, row); Grid.SetColumn(text, column + 1); Grid.SetColumnSpan(text, span); fields.Children.Add(text);
        }
    }

    private string Text(string name, string fallback) => _translations.Translate("BugReportForm", name, "Text", fallback);
    private Button Command(string name, string caption, string? icon = null)
    {
        string label = _translations.TranslateAccessKeys("BugReportForm", name, "Text", caption);
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) { content.Children.Add(ResetDialogControls.Icon(icon)); }
        content.Children.Add(new AccessText { Text = label });
        return new() { Name = name, Content = content, MinWidth = 75, MinHeight = 25, Padding = new Thickness(7, 2) };
    }
    private void UpdateActions()
    {
        _copy.IsEnabled = !_busy && !_closing && (_copyText is not null || Clipboard is not null);
        _send.IsEnabled = !_busy && !_closing && !_context.FocusDetails && _openIssue is not null;
    }
    private Task StartAsync(Func<Task> action)
    {
        if (_busy || _closing || _closed) { return Task.CompletedTask; }
        return _work = RunAsync(action);
    }
    private async Task RunAsync(Func<Task> action)
    {
        Control? previousFocus = FocusManager?.GetFocusedElement() as Control;
        _busy = true; _notice.Text = null; UpdateActions();
        try { _lifetime.Token.ThrowIfCancellationRequested(); await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_closing) { _notice.Text = _translations.Translate("Unable to complete this action."); } }
        finally
        {
            _busy = false; UpdateActions();
            if (_closing) { ScheduleClose(); }
            else if (IsVisible && !OwnedWindows.Any(child => child.IsVisible)
                && (FocusManager?.GetFocusedElement() is not Control current || current is Window || !current.Focusable || !current.IsEffectivelyVisible)
                && previousFocus is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true }) { previousFocus.Focus(); }
        }
    }
    private async Task CopyAsync(string report)
    {
        if (_copyText is { } copy) { await copy(report, _lifetime.Token); }
        else if (Clipboard is { } clipboard) { await clipboard.SetTextAsync(report); }
        _lifetime.Token.ThrowIfCancellationRequested();
    }
    private async Task OpenPropertyAsync(ExceptionReportProperty property)
    {
        ExceptionDetailDialog detail = new(property.Name, property.Value, _translations) { RequestedThemeVariant = ActualThemeVariant };
        using CancellationTokenRegistration cancellation = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(detail.Close));
        try { await detail.ShowDialog(this); _lifetime.Token.ThrowIfCancellationRequested(); }
        finally { detail.Close(); if (!_closing && IsVisible) { _details.RestorePropertyFocus(); } }
    }
    private async Task SendAsync(string description)
    {
        if (_openIssue is null || _context.FocusDetails) { return; }
        if (string.IsNullOrWhiteSpace(description))
        {
            await Dialogs.ShowMessageAsync(this, Text("_title", "Error Report"), Text("_noReproStepsSuppliedErrorMessage", "Please provide as much as information as possible to help the developers solve this issue."), true, _lifetime.Token);
            if (!_closing && IsVisible) { _description.Focus(); } return;
        }
        string report = _context.CopyText(_snapshot, description);
        if (!await ConfirmExternalAsync(report)) { return; }
        _lifetime.Token.ThrowIfCancellationRequested();
        await _openIssue(DesktopErrorReporter.IssueUri(_snapshot, _context, description), _lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested(); RequestDecision(BugReportDecision.Quit);
    }
    private async Task<bool> ConfirmExternalAsync(string report)
    {
        Window confirmation = new() { Name = "ConfirmExternalErrorReport", Title = Text("_title", "Error Report"), Width = 620, Height = 470,
            MinWidth = 420, MinHeight = 320, CanMinimize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant };
        TextBox preview = ExceptionDetailDialog.ReadOnly("ErrorReportPreview", report, true);
        StackPanel heading = new() { Spacing = 6, Children =
        {
            new TextBlock { Text = Text("_submitGitHubMessage", "Give as much as information as possible please to help the developers solve this issue. Otherwise, your issue ticket may be closed without any follow-up from the developers.\n\nBecause of this, make sure to fill in all the fields in the report template please.\n\nSend report?"), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Name = "ErrorReportDestination", Text = DesktopErrorReporter.IssueTarget.AbsoluteUri, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = _translations.Translate("Opening GitHub shares the report shown below with your browser. Review paths, exception details and your description before continuing."), TextWrapping = TextWrapping.Wrap }
        } };
        Button no = new() { Name = "CancelErrorReportSubmission", Content = _translations.Translate("No"), IsCancel = true, IsDefault = true, MinWidth = 75 };
        Button yes = new() { Name = "ConfirmErrorReportSubmission", Content = _translations.Translate("Yes"), MinWidth = 75 };
        no.Click += (_, _) => confirmation.Close(false); yes.Click += (_, _) => confirmation.Close(true);
        Grid layout = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(12) };
        layout.Children.Add(heading); Grid.SetRow(preview, 1); layout.Children.Add(preview);
        Control buttons = ExportDialogControls.Buttons(yes, no); Grid.SetRow(buttons, 2); layout.Children.Add(buttons); confirmation.Content = layout;
        confirmation.Opened += (_, _) => { no.Focus(); ModalWindowActivation.AfterMapping(confirmation, () => !_closing); };
        using CancellationTokenRegistration cancellation = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(confirmation.Close));
        try { bool confirmed = await confirmation.ShowDialog<bool>(this); _lifetime.Token.ThrowIfCancellationRequested(); return confirmed; }
        finally { confirmation.Close(); }
    }

    private void RequestDecision(BugReportDecision decision)
    { _decision = decision; BeginClose(); ScheduleClose(); }
    internal Task BeginClose()
    {
        if (!_closing)
        {
            _closing = true; if (!_disposed) { _lifetime.Cancel(); }
            foreach (Window child in OwnedWindows.ToArray()) { child.Close(); }
            UpdateActions();
        }
        return _work;
    }
    private void ScheduleClose()
    {
        if (_closeScheduled || _closed) { return; } _closeScheduled = true;
        _ = CloseAfterWorkAsync();
    }
    private async Task CloseAfterWorkAsync()
    {
        await _work;
        Dispatcher.UIThread.Post(() => { _closeScheduled = false; if (!_closed && IsVisible) { Close(_decision); } });
    }
    internal void DisposeWork()
    { if (!_disposed) { _disposed = true; _lifetime.Dispose(); } }
}
