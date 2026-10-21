using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
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
    private readonly CheckBox _issueHelper = new() { Name = "GitHubIssueHelperEnabled" };
    private readonly NumericUpDown _issueCount = ClassicNumericInput.Create("GitHubIssueHelperMaxCount", 0, int.MaxValue, width: 120, clipValueToMinMax: false);
    private readonly IssueCountDraft _issueCountDraft;
    private readonly GitHubIssueHelperSettings _issueBaseline;
    private readonly Func<string, Task>? _openUrl;
    private BuildServerCredentials _credentials;
    private string _credentialOrigin;
    internal GitHubHostingSettingsDialog(GitHubHostingPreferencesStore store, TranslationCatalog? translations = null, Func<string, Task>? openUrl = null)
    {
        _store = store; _baseline = store.Load(); _translations = translations ?? TranslationCatalog.LoadDefault(); _openUrl = openUrl;
        CanMinimize = false; ShowInTaskbar = false; Name = "GitHubHostingSettingsDialog"; Title = T("GitHub settings"); Width = 610; SizeToContent = SizeToContent.Height; MinWidth = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _issueBaseline = GitHubIssueHelperSettings.FromPluginSettings(_baseline);
        _issueHelper.IsChecked = _issueBaseline.Enabled;
        _issueHelper.Content = new TextBlock { Text = _translations.Translate("GitHub3Plugin", "_issueCommitMessageHelperEnabled", "Caption", "Enable commit message issue helper"), TextWrapping = TextWrapping.Wrap };
        ReferenceDialogControls.Compact(_issueHelper);
        string? rawCount = _baseline.GetValueOrDefault(GitHubIssueHelperSettings.MaxCountKey);
        _issueCount.Value = _issueBaseline.MaxCount;
        _issueCountDraft = new(rawCount ?? _issueBaseline.MaxCount.ToString(CultureInfo.CurrentCulture), _issueCount.Value);
        // NumericUpDown applies this converter to its shared PART_TextBox, including template initialization and lost-focus sync.
        _issueCount.TextConverter = _issueCountDraft;
        _issueCount.Text = _issueCountDraft.Text;
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
        AddLink("GitHubCreateToken", "Generate API token", "/settings/tokens/new?description=Token%20for%20GitExtensions&scopes=repo,public_repo");
        AddLink("GitHubManageTokens", "Manage API tokens", "/settings/tokens");
        Grid issues = new() { ColumnDefinitions = new("*,120"), RowDefinitions = new("Auto,Auto"), Margin = new(10, 4, 10, 10), RowSpacing = 5 };
        Grid.SetColumnSpan(_issueHelper, 2); issues.Children.Add(_issueHelper);
        TextBlock countLabel = new() { Text = _translations.Translate("GitHub3Plugin", "_issueCommitMessageHelperMaxCount", "Caption", "Maximum number of issues retrieved"), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        Grid.SetRow(countLabel, 1); issues.Children.Add(countLabel);
        Grid.SetRow(_issueCount, 1); Grid.SetColumn(_issueCount, 1); issues.Children.Add(_issueCount);
        Button save = new() { Name = "GitHubSaveSettings", Content = T("Save"), IsDefault = true, MinWidth = 75, Height = 25 };
        save.Click += (_, _) => Save();
        Button cancel = new() { Name = "GitHubCancelSettings", Content = T("Cancel"), IsCancel = true, MinWidth = 75, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new(10), Children = { save, cancel } };
        StackPanel body = new() { Margin = new(10, 8, 10, 0), Children =
        {
            ClassicChrome.Group("GitHubHostingGroup", "GitHub", fields), links,
            ClassicChrome.Group("GitHubIssueHelperGroup", T("Commit message issue helper"), issues),
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
            GitHubIssueHelperSettings helper = _issueBaseline with { Enabled = _issueHelper.IsChecked == true };
            Dictionary<string, string?> helperDraft = new(helper.WithChanges(_baseline, _issueBaseline), StringComparer.Ordinal);
            // Enter can activate the default button before LostFocus; NumericUpDown bypasses TextConverter for empty input.
            _issueCountDraft.CaptureEmpty(_issueCount.Text);
            if (_issueCountDraft.IsChanged)
            {
                // NumberSettingNumericUpDownBinding treats an empty editor as an explicit unset.
                if (string.IsNullOrEmpty(_issueCountDraft.Text)) { helperDraft.Remove(GitHubIssueHelperSettings.MaxCountKey); }
                else
                {
                    if (!int.TryParse(_issueCountDraft.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int count) || count < 0)
                    { throw new ArgumentException(T("Enter a whole number of issues from 0 to 2147483647.")); }
                    helperDraft[GitHubIssueHelperSettings.MaxCountKey] = count.ToString(CultureInfo.CurrentCulture);
                }
            }
            _store.SaveChanges(_baseline, settings.WithCredentials(helperDraft, _credentials)); Close(true);
        }
        catch (Exception error) { _notice.Text = error.Message; }
    }
    /// <summary>Retain the native edit before NumericUpDown can normalize it back to its last valid number.</summary>
    private sealed class IssueCountDraft(string original, decimal? number) : IValueConverter
    {
        private readonly string _original = original;
        private decimal? _number = number;
        internal string Text { get; private set; } = original;
        internal bool IsChanged => !string.Equals(Text, _original, StringComparison.Ordinal);
        internal void CaptureEmpty(string? text)
        { if (string.IsNullOrEmpty(text)) { Text = ""; _number = null; } }

        // NumericUpDown uses ConvertBack for Value -> text and Convert for text -> Value.
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            decimal? current = value is decimal next ? next : null;
            // A real spin changes Value; a template/lost-focus synchronization of the same Value keeps its raw draft.
            if (current != _number)
            {
                _number = current;
                Text = current?.ToString("0", culture) ?? "";
            }
            return Text;
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            Text = value as string ?? "";
            if (int.TryParse(Text, NumberStyles.Integer, culture, out int count) && count >= 0) { _number = count; }
            // Invalid/legacy raw text remains visible and independently validated at Save; spinning still uses the last number.
            return _number;
        }
    }

}
