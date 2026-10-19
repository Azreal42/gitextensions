using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

/// <summary>A private credential draft. The caller receives a fresh model only after explicit OK.</summary>
internal sealed class BuildServerCredentialsDialog : Window
{
    private readonly RadioButton _guest, _user, _bearer;
    private readonly TextBox _username, _password, _token;
    private bool _decided;

    internal BuildServerCredentials? Result { get; private set; }

    internal BuildServerCredentialsDialog(string buildServerUniqueKey, IBuildServerCredentials? credentials = null,
        TranslationCatalog? translations = null)
    {
        ArgumentNullException.ThrowIfNull(buildServerUniqueKey);
        BuildServerCredentials initial = Clone(credentials);
        if (!Enum.IsDefined(initial.BuildServerCredentialsType)) { throw new ArgumentOutOfRangeException(nameof(credentials)); }
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        string T(string item, string source)
        {
            string contextual = text.Translate("FormBuildServerCredentials", item, "Text", source);
            return contextual == source ? text.Translate(source) : contextual;
        }
        Name = "BuildServerCredentialsDialog"; Title = T("$this", "Enter credentials");
        Width = 580; Height = 251; MinWidth = 373; MinHeight = 210;
        CanResize = true; CanMinimize = CanMaximize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ClassicChoiceAppearance.Apply(this, "build-server-credentials");

        string header = string.Format(text.CurrentCulture,
            T("labelHeader", "Please enter the credentials for the build server at {0}."), buildServerUniqueKey);
        TextBlock heading = new() { Name = "labelHeader", Text = header, Height = 28,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(heading, header);
        _guest = Choice("radioButtonGuestAccess", "Guest access", initial.BuildServerCredentialsType == BuildServerCredentialsType.Guest);
        _user = Choice("radioButtonAuthenticatedUser", "Authenticated user", initial.BuildServerCredentialsType == BuildServerCredentialsType.UsernameAndPassword);
        _bearer = Choice("radioButtonBearerToken", "Bearer token", initial.BuildServerCredentialsType == BuildServerCredentialsType.BearerToken);
        _username = Input("textBoxUserName", initial.Username, secret: false);
        _password = Input("textBoxPassword", initial.Password, secret: true);
        _token = Input("textBoxBearerToken", initial.BearerToken, secret: true);
        Grid fields = new() { Name = "BuildServerCredentialFields", ColumnDefinitions = new("Auto,*"), RowDefinitions = new("26,26,26") };
        Field(0, "label2", "Username:", _username); Field(1, "label3", "Password:", _password); Field(2, "label4", "Bearer token:", _token);
        StackPanel modes = new() { Name = "BuildServerCredentialModes", Height = 75, Spacing = 1, Children = { _guest, _user, _bearer } };
        Button ok = new() { Name = "buttonOK", Content = T("buttonOK", "OK"), IsDefault = true, MinWidth = 75, Height = 25, MinHeight = 0 };
        Button cancel = new() { Name = "buttonCancel", Content = T("buttonCancel", "Cancel"), IsCancel = true, MinWidth = 75, Height = 25, MinHeight = 0 };
        ok.Click += (_, _) => Accept(); cancel.Click += (_, _) => Cancel();
        StackPanel actions = new() { Name = "BuildServerCredentialsButtons", Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 6, 0, 0), Children = { ok, cancel } };
        DockPanel layout = new() { Margin = new Thickness(12), LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { heading, modes, fields } }
        });
        Content = layout;
        foreach (RadioButton radio in new[] { _guest, _user, _bearer }) { radio.IsCheckedChanged += (_, _) => UpdateMode(); }
        UpdateMode();
        Opened += (_, _) =>
        {
            if (_user.IsChecked == true) { _username.Focus(); }
            else if (_bearer.IsChecked == true) { _token.Focus(); }
            else { _guest.Focus(); }
            ModalWindowActivation.AfterMapping(this, () => IsVisible);
        };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Cancel(); }
            // WinForms delegates Enter from a radio to the form's AcceptButton.
            // Avalonia's RadioButton consumes it through Button instead.
            else if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None
                && (_guest.IsKeyboardFocusWithin || _user.IsKeyboardFocusWithin || _bearer.IsKeyboardFocusWithin))
            { args.Handled = true; Accept(); }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closing += (_, _) => { if (!_decided) { Result = null; } };
        Closed += (_, _) => { _username.Text = _password.Text = _token.Text = ""; };

        RadioButton Choice(string name, string caption, bool selected)
        {
            RadioButton radio = new() { Name = name, Content = T(name, caption), GroupName = "BuildServerCredentialsAuthentication", IsChecked = selected };
            ReferenceDialogControls.Compact(radio); return radio;
        }
        static TextBox Input(string name, string? value, bool secret) => new()
        {
            Name = name, Text = value, PasswordChar = secret ? '●' : '\0', RevealPassword = false,
            AcceptsReturn = false, AcceptsTab = false, Height = 24, MinHeight = 0, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        void Field(int row, string name, string caption, TextBox input)
        {
            TextBlock label = new() { Name = name, Text = T(name, caption), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(label, row); Grid.SetRow(input, row); Grid.SetColumn(input, 1); fields.Children.Add(label); fields.Children.Add(input);
        }
    }

    private void UpdateMode()
    {
        _username.IsEnabled = _password.IsEnabled = _user.IsChecked == true;
        _token.IsEnabled = _bearer.IsChecked == true;
    }

    private void Accept()
    {
        if (_decided || !IsVisible) { return; }
        BuildServerCredentials accepted = new()
        {
            BuildServerCredentialsType = _user.IsChecked == true ? BuildServerCredentialsType.UsernameAndPassword
                : _bearer.IsChecked == true ? BuildServerCredentialsType.BearerToken : BuildServerCredentialsType.Guest,
            Username = _username.Text ?? "", Password = _password.Text ?? "", BearerToken = _token.Text ?? ""
        };
        _decided = true; Result = accepted; Close(accepted);
    }

    private void Cancel()
    {
        if (_decided || !IsVisible) { return; }
        _decided = true; Result = null; Close(null);
    }

    private static BuildServerCredentials Clone(IBuildServerCredentials? input) => new()
    {
        BuildServerCredentialsType = input?.BuildServerCredentialsType ?? BuildServerCredentialsType.Guest,
        Username = input?.Username, Password = input?.Password, BearerToken = input?.BearerToken
    };
}
