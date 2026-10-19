using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Desktop.Controls;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

internal sealed class GitHubHostingSettingsDialog : Window
{
    private readonly GitHubHostingPreferencesStore _store;
    private readonly IReadOnlyDictionary<string, string?> _baseline;
    private readonly TranslationCatalog _translations;
    private readonly TextBox _web = new() { Name = "GitHubWebUrl" }, _api = new() { Name = "GitHubApiUrl" };
    private readonly TextBlock _mode = new() { Name = "GitHubAuthenticationMode" }, _notice = new() { Name = "GitHubHostingSettingsError", TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, Task>? _openUrl;
    private BuildServerCredentials _credentials;
    private string _credentialOrigin;
    internal GitHubHostingSettingsDialog(GitHubHostingPreferencesStore store, TranslationCatalog? translations = null, Func<string, Task>? openUrl = null)
    {
        _store = store; _baseline = store.Load(); _translations = translations ?? TranslationCatalog.LoadDefault(); _openUrl = openUrl;
        CanMinimize = false; ShowInTaskbar = false; Name = "GitHubHostingSettingsDialog"; Title = T("GitHub settings"); Width = 610; SizeToContent = SizeToContent.Height; MinWidth = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        string host = _baseline.GetValueOrDefault(GitHubHostingSettings.HostnameKey) ?? "github.com";
        _web.Text = _baseline.GetValueOrDefault(GitHubHostingSettings.WebUrlKey) ?? "https://" + host;
        _api.Text = _baseline.GetValueOrDefault(GitHubHostingSettings.ApiUrlKey) ?? (host == "github.com" ? "https://api.github.com" : "");
        _credentialOrigin = _baseline.GetValueOrDefault(GitHubHostingSettings.CredentialsOriginKey) ?? "https://api.github.com";
        string? type = _baseline.GetValueOrDefault(GitHubHostingSettings.CredentialsTypeKey);
        _credentials = new()
        {
            BuildServerCredentialsType = Enum.TryParse(type, out BuildServerCredentialsType parsed) && Enum.IsDefined(parsed) ? parsed
                : string.IsNullOrEmpty(_baseline.GetValueOrDefault(GitHubHostingSettings.TokenKey)) ? BuildServerCredentialsType.Guest : BuildServerCredentialsType.BearerToken,
            Username = _baseline.GetValueOrDefault(GitHubHostingSettings.UsernameKey), Password = _baseline.GetValueOrDefault(GitHubHostingSettings.PasswordKey),
            BearerToken = _baseline.GetValueOrDefault(GitHubHostingSettings.TokenKey)
        };
        Button auth = new() { Name = "GitHubAuthentication", Content = T("Authentication…"), MinWidth = 112, Height = 25 };
        auth.Click += async (_, _) =>
        {
            try
            {
                GitHubHostingSettings settings = Current(); settings.Validate();
                BuildServerCredentialsDialog dialog = new(settings.ApiUrl, _credentials, _translations) { RequestedThemeVariant = ActualThemeVariant };
                if (await dialog.ShowDialog<BuildServerCredentials?>(this) is BuildServerCredentials accepted)
                { _credentials = accepted; _credentialOrigin = settings.ApiUrl; UpdateMode(); _notice.Text = ""; }
            }
            catch (Exception error) { _notice.Text = error.Message; }
        };
        Grid fields = new() { ColumnDefinitions = new("110,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), Margin = new(10, 4, 10, 10) };
        Add(0, T("Web URL"), _web); Add(1, T("API URL"), _api); Add(2, T("Authentication"), _mode);
        Grid.SetColumn(auth, 2); Grid.SetRow(auth, 2); fields.Children.Add(auth);
        StackPanel links = new() { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new(10, 0, 10, 8) };
        AddLink("GitHubCreateToken", "Generate API token", "/settings/tokens/new?description=GitExtensions");
        AddLink("GitHubManageTokens", "Manage API tokens", "/settings/tokens");
        Button save = new() { Name = "GitHubSaveSettings", Content = T("Save"), IsDefault = true, MinWidth = 75, Height = 25 };
        save.Click += (_, _) => Save();
        Button cancel = new() { Name = "GitHubCancelSettings", Content = T("Cancel"), IsCancel = true, MinWidth = 75, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new(10), Children = { save, cancel } };
        StackPanel body = new() { Margin = new(10, 8, 10, 0), Children =
        {
            ClassicChrome.Group("GitHubHostingGroup", "GitHub", fields), links,
            new TextBlock { Text = T("Credentials are saved locally for this user."), TextWrapping = TextWrapping.Wrap, Margin = new(10, 0, 10, 6) },
            _notice, footer
        } };
        Content = body; UpdateMode();
        Opened += (_, _) => { _web.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        void Add(int row, string label, Control input)
        {
            TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 3, 8, 3) };
            Grid.SetRow(caption, row); fields.Children.Add(caption); Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            input.Margin = new(0, 3, 8, 3); fields.Children.Add(input);
        }
        void AddLink(string name, string text, string path)
        {
            Button link = new() { Name = name, Content = T(text), Padding = default, Background = Brushes.Transparent, BorderThickness = default, IsEnabled = openUrl is not null };
            link.Classes.Add("link"); link.Click += async (_, _) =>
            {
                try { GitHubHostingSettings s = Current(); s.Validate(); if (_openUrl is not null) { await _openUrl(s.WebUrl.TrimEnd('/') + path); } }
                catch (Exception error) { _notice.Text = error.Message; }
            };
            links.Children.Add(link);
        }
    }
    private string T(string text) => _translations.Translate("GitHub3Plugin", text, "Text", text);
    private GitHubHostingSettings Current() => new((_web.Text ?? "").Trim(), (_api.Text ?? "").Trim());
    private void UpdateMode() => _mode.Text = T(_credentials.BuildServerCredentialsType switch
    { BuildServerCredentialsType.Guest => "Guest", BuildServerCredentialsType.UsernameAndPassword => "Username and password", _ => "Bearer token" });
    private void Save()
    {
        try
        {
            GitHubHostingSettings settings = Current(); settings.Validate(); GitHubHostingCredentials.From(_credentials).Validate(settings);
            if (_credentials.BuildServerCredentialsType != BuildServerCredentialsType.Guest
                && (!Uri.TryCreate(_credentialOrigin, UriKind.Absolute, out Uri? origin) || !Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out Uri? target)
                    || origin.Scheme != target.Scheme || origin.IdnHost != target.IdnHost || origin.Port != target.Port))
            { throw new InvalidOperationException(T("Enter authentication explicitly for the new API endpoint before saving.")); }
            _store.SaveChanges(_baseline, settings.WithCredentials(_baseline, _credentials)); Close(true);
        }
        catch (Exception error) { _notice.Text = error.Message; }
    }
}
