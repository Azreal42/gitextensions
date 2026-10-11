using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original read-only local/global proxy preview and Set/Unset actions.</summary>
public sealed class ProxySwitcherDialog : RepositoryDialog
{
    private readonly ProxySwitcherPlugin _plugin = new();
    private readonly TextBox _local = new() { Name = "LocalHttpProxy_TextBox", IsReadOnly = true, Height = 24 };
    private readonly TextBox _global = new() { Name = "GlobalHttpProxy_TextBox", IsReadOnly = true, Height = 24 };
    private readonly CheckBox _applyGlobally = new() { Name = "ApplyGlobally_CheckBox" };
    private readonly Button _set;
    private ProxySwitcherOptions _options;
    public ProxySwitcherState? CurrentState { get; private set; }
    public ProxySwitcherOptions CurrentOptions => _options;
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>
    {
        ["host"] = HostWithoutCredentials(_options.Host), ["port"] = _options.Port, ["username"] = _options.Username,
        ["global"] = (_applyGlobally.IsChecked == true).ToString(CultureInfo.InvariantCulture), ["operation"] = "inspect"
    };

    public ProxySwitcherDialog(GitRepository repository, IReadOnlyDictionary<string, string?>? savedValues = null)
        : base(repository, "Proxy Switcher")
    {
        string Value(string key, string fallback = "") => savedValues?.GetValueOrDefault(key) ?? fallback;
        _options = new(Value("host"), Value("port", "8080"), Value("username"), Value("password"));
        Width = 341; Height = 144; MinWidth = 341; MinHeight = 144; CanResize = false;
        _applyGlobally.Content = T("Apply globally");
        Grid fields = new() { Name = "ProxyPreview", ColumnDefinitions = new("101,*"), RowDefinitions = new("26,26,28,29") };
        AddField(fields, 0, "Local http.proxy:", _local);
        AddField(fields, 1, "Global http.proxy:", _global);
        _applyGlobally.Margin = new Thickness(3, 2); Grid.SetRow(_applyGlobally, 2); Grid.SetColumn(_applyGlobally, 1); fields.Children.Add(_applyGlobally);
        Grid actions = new() { ColumnDefinitions = new("*,123,123") };
        Button settings = Action("ProxySettings", "Settings…", SettingsAsync);
        settings.Margin = new Thickness(3); settings.Height = 23; actions.Children.Add(settings);
        _set = Action("SetProxy_Button", "Set proxy", () => ChangeAsync("set"));
        _set.Margin = new Thickness(3); _set.Height = 23; Grid.SetColumn(_set, 1); actions.Children.Add(_set);
        Button unset = Action("UnsetProxy_Button", "Unset proxy", () => ChangeAsync("unset"));
        unset.Margin = new Thickness(3); unset.Height = 23; Grid.SetColumn(unset, 2); actions.Children.Add(unset);
        Grid.SetRow(actions, 3); Grid.SetColumnSpan(actions, 2); fields.Children.Add(actions);
        Root.Children.Add(fields); Track(_applyGlobally);
        Closed += (_, _) => _options = _options with { Password = "" };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("HTTP proxy", RefreshAsync);

    private async Task RefreshAsync(CancellationToken token)
    {
        CurrentState = await ProxySwitcherPlugin.GetStateAsync(Repository, token);
        _local.Text = CurrentState.EffectiveProxy; _global.Text = CurrentState.GlobalProxy;
        _applyGlobally.IsChecked = CurrentState.ApplyGlobally;
        Status.Text = string.IsNullOrWhiteSpace(_options.Host) ? T("There is no proxy configured. Please set the proxy host in the plugin settings.") : "";
    }

    private Task ChangeAsync(string operation) => RunAsync(operation == "set" ? "Set proxy" : "Unset proxy", async token =>
    {
        Dictionary<string, string?> values = new(CurrentValues)
        {
            ["host"] = _options.Host, ["password"] = _options.Password, ["operation"] = operation
        };
        await _plugin.ExecuteAsync(new(Repository), values, token);
        await RefreshAsync(token);
    }, changes: true);

    private async Task SettingsAsync()
    {
        ProxySwitcherSettingsDialog dialog = new(_options);
        ProxySwitcherOptions? edited = await dialog.ShowDialog<ProxySwitcherOptions?>(this);
        if (edited is not null) { _options = edited; Status.Text = ""; UpdateActions(); }
    }

    protected override void UpdateActions() => _set.IsEnabled = !string.IsNullOrWhiteSpace(_options.Host);

    private void AddField(Grid grid, int row, string label, Control input)
    {
        TextBlock text = new() { Text = T(label), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
        Grid.SetRow(text, row); grid.Children.Add(text);
        input.Margin = new Thickness(3, 1); Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }

    private static string HostWithoutCredentials(string host)
    {
        int at = host.LastIndexOf('@');
        if (at < 0) return host;
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        return (scheme >= 0 ? host[..(scheme + 3)] : "") + host[(at + 1)..];
    }
}

/// <summary>The four original plugin settings; password lives only in this modal session.</summary>
public sealed class ProxySwitcherSettingsDialog : Window
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly TextBox _host = new() { Name = "ProxyHost", Height = 24 };
    private readonly TextBox _port = new() { Name = "ProxyPort", Height = 24 };
    private readonly TextBox _username = new() { Name = "ProxyUsername", Height = 24 };
    private readonly TextBox _password = new() { Name = "ProxyPassword", Height = 24, PasswordChar = '●' };
    private readonly TextBlock _error = new() { Name = "ProxySettingsError", TextWrapping = TextWrapping.Wrap };

    public ProxySwitcherSettingsDialog(ProxySwitcherOptions options)
    {
        Title = T("Proxy Switcher"); Width = 420; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _host.Text = options.Host; _port.Text = options.Port; _username.Text = options.Username; _password.Text = options.Password;
        Grid fields = new() { ColumnDefinitions = new("125,*"), RowDefinitions = new("30,30,30,30") };
        int row = 0;
        foreach ((string label, TextBox input) in new[] { ("HTTP proxy", _host), ("HTTP proxy port", _port), ("Username", _username), ("Password", _password) })
        {
            TextBlock text = new() { Text = T(label), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row); fields.Children.Add(text);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1); fields.Children.Add(input); row++;
        }
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Button cancel = new() { Name = "CancelProxySettings", Content = T("Cancel"), IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        Button save = new() { Name = "SaveProxySettings", Content = T("OK"), IsDefault = true, MinWidth = 80 };
        save.Click += (_, _) =>
        {
            ProxySwitcherOptions value = new(_host.Text ?? "", _port.Text ?? "", _username.Text ?? "", _password.Text ?? "");
            try { _ = value.BuildProxy(); Close(value); }
            catch (ArgumentException exception) { _error.Text = exception.Message; }
        };
        actions.Children.Add(save); actions.Children.Add(cancel);
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { fields, _error, actions } };
        Closed += (_, _) => _password.Text = null;
    }

    private string T(string text) => _translations.Translate(text);
}
