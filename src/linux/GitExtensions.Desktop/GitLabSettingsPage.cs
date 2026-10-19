using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Windows GitlabSettingsUserControl fields, scoped drafts and explicitly activated project lookup.</summary>
internal sealed class GitLabSettingsPage : BuildServerScopedSettingsPage
{
    private const string LookupFailure = "Failed to obtain project from server. Try to specify valid API token or check instance URL";
    private const string TokenManagementTail = "-/profile/personal_access_tokens?name=GitExtensionsIntegration&scopes=api";
    private readonly TextBox _instance, _project, _token;
    private readonly Button _lookup, _manage;
    private readonly TextBlock _status;
    private readonly Action<Uri>? _openBrowser;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private readonly HashSet<GitDistributedSettingsScope> _instanceEdited = [];
    private CancellationTokenSource? _current;
    private Task? _close;
    private long _generation;
    private bool _active, _closing, _rendering, _automaticAttempted, _editedSinceActivation;

    internal bool IsBusy => _work.Count != 0;
    internal Task Completion => Task.WhenAll(_work.ToArray());
    internal event Action? BusyChanged;
    internal Func<GitLabSettings, IGitLabClient> ClientFactory { get; set; } = settings => new GitLabApiClient(settings);

    internal GitLabSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations,
        Action<Uri>? openBrowser = null, bool embedded = false) : base(session, translations, "GitLabSettingsPage")
    {
        _openBrowser = openBrowser;
        // Reuse the shared editor registry and edit/unknown-value semantics, but the original page has one compact Auto/* table.
        _instance = Text(GitLabSettings.InstanceUrlKey, "InstanceUrlTextBox", "Instance URL");
        _project = Text(GitLabSettings.ProjectIdKey, "ProjectIdTextBox", "Project ID");
        _token = Text(GitLabSettings.ApiTokenKey, "ApiTokenTextBox", "Api Token", password: true); _token.RevealPassword = false;
        Grid table = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto") };
        table.RowDefinitions[3].MinHeight = 20;
        _lookup = Link("GetProjectIdLink", "Get Project ID from server", 5);
        _manage = Link("TokenManagementLink", "Go to token management page", 9);
        _status = new()
        {
            Name = "GetProjectIdStatusText", Text = T("GetProjectIdStatusText", LookupFailure), IsVisible = false,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0)
        };
        MoveEditor(_instance, 0, "label1", "Instance URL", 1);
        MoveEditor(_project, 1, "label3", "Project ID", 4);
        MoveEditor(_token, 4, "label2", "Api Token", 8);
        Place(_lookup, 2); Place(_status, 3); Place(_manage, 5); Add(table);
        _lookup.Click += (_, _) => { if (Editable && ValidInstance(out _)) { StartLookup(automatic: false); } };
        _manage.Click += (_, _) =>
        {
            if (_closing || !_active || !IsEffectivelyVisible || !ValidInstance(out Uri? instance)) { return; }
            _openBrowser?.Invoke(new Uri(instance, TokenManagementTail));
        };
        ScopeChanged += _ =>
        { InvalidateRead(); _automaticAttempted = _editedSinceActivation = false; _status.IsVisible = false; QueueAutomaticLookup(); };
        AttachedToVisualTree += (_, _) => QueueAutomaticLookup();
        DetachedFromVisualTree += (_, _) => { InvalidateRead(); _automaticAttempted = false; };
        if (embedded) { HideHeader(); }
        ShowLoadState();

        void MoveEditor(TextBox box, int row, string labelName, string caption, int tab)
        {
            Grid line = (Grid)box.Parent!; Panel parent = (Panel)line.Parent!; parent.Children.Remove(line);
            TextBlock label = line.Children.OfType<TextBlock>().Single(); line.Children.Remove(label); line.Children.Remove(box);
            label.Name = labelName; label.Text = T(labelName, caption); label.Margin = new Thickness(2, 0);
            label.HorizontalAlignment = HorizontalAlignment.Left; box.Margin = new Thickness(2); box.Height = box.MinHeight = 23;
            box.AcceptsReturn = box.AcceptsTab = false; box.TabIndex = tab;
            Grid.SetRow(label, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1); table.Children.Add(label); table.Children.Add(box);
            box.PropertyChanged += (_, args) =>
            {
                if (_rendering || args.Property != TextBox.TextProperty) { return; }
                if (ReferenceEquals(box, _instance)) { _instanceEdited.Add(CurrentScope); }
                _editedSinceActivation = true; InvalidateRead(); _status.IsVisible = false; UpdateActions();
            };
        }
        void Place(Control control, int row) { Grid.SetColumn(control, 1); Grid.SetRow(control, row); table.Children.Add(control); }
    }

    internal void SetProviderActive(bool active)
    {
        if (_closing) { return; }
        if (_active != active)
        {
            _active = active; InvalidateRead(); _automaticAttempted = _editedSinceActivation = false;
            _status.IsVisible = false;
        }
        UpdateActions(); if (_active) { QueueAutomaticLookup(); }
    }

    internal override void ShowLoadState()
    {
        _rendering = true;
        try
        {
            base.ShowLoadState();
            if (_instance is not null && Session.Loaded && !_instanceEdited.Contains(CurrentScope)
                && string.IsNullOrEmpty(Session.Read(CurrentScope, GitLabSettings.InstanceUrlKey)))
            {
                try { LoadText(_instance, Session.GitLab(CurrentScope).InstanceUrl); }
                catch (ArgumentException) { /* Invalid effective binding stays editable; never manufacture a replacement endpoint. */ }
            }
        }
        finally { _rendering = false; }
        if (_lookup is not null) { UpdateActions(); QueueAutomaticLookup(); }
    }

    private bool Editable => !_closing && _active && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective;
    private bool ValidInstance([NotNullWhen(true)] out Uri? instance)
    {
        try { instance = GitLabSettings.ParseInstanceUri(_instance.Text ?? ""); return true; }
        catch (ArgumentException) { instance = null; return false; }
    }
    private void UpdateActions()
    {
        bool valid = ValidInstance(out _);
        _lookup.IsEnabled = Editable && valid;
        _manage.IsEnabled = !_closing && Session.Loaded && valid && _openBrowser is not null;
    }
    private void QueueAutomaticLookup()
    {
        long generation = _generation; GitDistributedSettingsScope scope = CurrentScope;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || generation != _generation || scope != CurrentScope || _automaticAttempted || _editedSinceActivation
                || !Editable || !IsEffectivelyVisible || TopLevel.GetTopLevel(this)?.IsVisible != true || !string.IsNullOrEmpty(_project.Text)
                || !ValidInstance(out _)) { return; }
            _automaticAttempted = true; StartLookup(automatic: true);
        }, DispatcherPriority.Loaded);
    }
    private void InvalidateRead()
    { ++_generation; _current?.Cancel(); }

    private void StartLookup(bool automatic)
    {
        if (!Editable || !ValidInstance(out Uri? instance)) { return; }
        InvalidateRead(); long generation = _generation; GitDistributedSettingsScope scope = CurrentScope;
        string instanceText = _instance.Text ?? "", tokenText = _token.Text ?? "", projectText = _project.Text ?? "";
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _current = request;
        _status.IsVisible = false;
        Task operation = ReadAsync(); _work.Add(operation); BusyChanged?.Invoke(); _ = FinishAsync(operation);
        async Task ReadAsync()
        {
            await Task.Yield();
            bool Current() => !_closing && _active && !request.IsCancellationRequested && generation == _generation && scope == CurrentScope
                && TopLevel.GetTopLevel(this) is not null && IsEffectivelyVisible
                && _instance.Text == instanceText && (_token.Text ?? "") == tokenText && (_project.Text ?? "") == projectText;
            try
            {
                if (!Current()) { return; }
                GitLabRemoteParser parser = new(); GitLabRemote? remote = null;
                foreach (string url in Session.RemoteUrls)
                { if (parser.TryParse(url, out GitLabRemote? candidate, instance)) { remote = candidate; break; } }
                if (remote is null) { if (!automatic) { _status.IsVisible = true; } return; }
                GitLabSettings settings = new() { Provider = GitLabSettings.ProviderName, InstanceUrl = instanceText, ApiToken = tokenText };
                await using IGitLabClient client = ClientFactory(settings);
                GitLabProject project = await client.GetProjectAsync(remote.Namespace, remote.Name, request.Token);
                if (!Current()) { return; }
                if (project.Id <= 0) { if (!automatic) { _status.IsVisible = true; } return; }
                string id = project.Id.ToString(CultureInfo.InvariantCulture);
                Session.Set(scope, GitLabSettings.InstanceUrlKey, instanceText);
                Session.Set(scope, GitLabSettings.ProjectIdKey, id);
                _rendering = true; try { LoadText(_project, id); } finally { _rendering = false; }
                _status.IsVisible = false;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception)
            { if (Current() && !automatic) { _status.IsVisible = true; } }
            finally
            {
                if (ReferenceEquals(_current, request)) { _current = null; }
                request.Dispose();
            }
        }
    }
    private async Task FinishAsync(Task operation)
    { try { await operation; } finally { _work.Remove(operation); BusyChanged?.Invoke(); } }

    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closing = true; InvalidateRead(); _lifetime.Cancel(); UpdateActions();
        _close = Completion; _ = DisposeAfterCloseAsync(_close); return _close;
    }
    private async Task DisposeAfterCloseAsync(Task pending) { await pending; _lifetime.Dispose(); }

    private Button Link(string name, string caption, int tab)
    {
        Button link = new()
        {
            Name = name, Content = T(name, caption), TabIndex = tab, MinHeight = 0, MinWidth = 0,
            Padding = default, BorderThickness = default, Margin = new Thickness(3, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Cursor = new(StandardCursorType.Hand)
        };
        link.Bind(ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush"));
        link.Template = new FuncControlTemplate<Button>((_, _) =>
        {
            TextBlock text = new() { TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap };
            text.Bind(TextBlock.TextProperty, new TemplateBinding(ContentControl.ContentProperty), BindingPriority.Template);
            text.Bind(TextBlock.ForegroundProperty, new TemplateBinding(ForegroundProperty), BindingPriority.Template);
            return new Border { Name = "GitLabLinkFocus", Child = text, BorderThickness = new Thickness(1), Background = Brushes.Transparent };
        });
        link.Styles.Add(new Style(selector => selector.OfType<Button>().Template().OfType<Border>().Name("GitLabLinkFocus"))
        { Setters = { new Setter(Border.BorderBrushProperty, Brushes.Transparent) } });
        link.Styles.Add(new Style(selector => selector.OfType<Button>().Class(":focus-visible").Template().OfType<Border>().Name("GitLabLinkFocus"))
        { Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicHighlightBrush")) } });
        link.Styles.Add(new Style(selector => selector.OfType<Button>().Class(":disabled").Template().OfType<TextBlock>())
        { Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicDisabledBrush")) } });
        return link;
    }
    private string T(string item, string source) => Translations.Translate("GitlabSettingsUserControl", item, "Text", source);
}
