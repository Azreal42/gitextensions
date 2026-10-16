using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>One literal prompt and one typed response for Git and OpenSSH's askpass protocols.</summary>
internal sealed class AskpassWindow : Window
{
    private readonly TextBox _input;
    private readonly TextBox _fingerprint;
    private readonly SelectableTextBlock _prompt;
    private readonly Button _accept;
    private readonly Button _useFingerprint;
    private bool _decided;

    internal AskpassRequest Request { get; }
    internal AskpassResult Result { get; private set; }
    internal string? Response => Result.Decision switch
    {
        AskpassDecision.Submit => Result.Value,
        AskpassDecision.Yes => "yes",
        AskpassDecision.No => "no",
        _ => null
    };

    internal AskpassWindow(string prompt) : this(AskpassRequest.Create(prompt, null)) { }

    internal AskpassWindow(AskpassRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Prompt);
        if (!Enum.IsDefined(request.Kind)) { throw new ArgumentOutOfRangeException(nameof(request)); }
        Request = request;
        Result = new(request.Kind == AskpassPromptKind.Notification ? AskpassDecision.Dismissed : AskpassDecision.Cancelled);
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Name = "AskpassWindow"; Title = "Git Extensions - git CLI stdin wrapper";
        bool credentials = request.Kind is AskpassPromptKind.Secret or AskpassPromptKind.Username;
        Width = credentials ? 360 : request.Kind == AskpassPromptKind.Notification ? 450 : 480;
        CanResize = false; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true;
        _input = new TextBox
        {
            Name = "AskpassInput", PasswordChar = request.Kind == AskpassPromptKind.Username ? '\0' : '●',
            RevealPassword = false, AcceptsReturn = false, AcceptsTab = false,
            Height = 25, MinHeight = 0, Margin = new Thickness(0, 4, 0, 6)
        };
        _fingerprint = new TextBox
        {
            Name = "AskpassFingerprint", PasswordChar = '\0', RevealPassword = false,
            AcceptsReturn = false, AcceptsTab = false, Height = 25, MinHeight = 0, MinWidth = 0
        };
        _prompt = new SelectableTextBlock
        {
            Name = "AskpassPrompt", Text = request.Prompt, TextWrapping = TextWrapping.Wrap, Focusable = true,
            MinHeight = credentials ? 34 : 32
        };
        _accept = Button("AcceptAskpass", "OK", () => AcceptInput(), isDefault: true);
        _useFingerprint = Button("UseAskpassFingerprint", "Use fingerprint", () => AcceptFingerprint());
        _useFingerprint.IsEnabled = false;
        _input.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty) { _accept.IsEnabled = IsSingleLine(_input.Text ?? ""); } };
        _fingerprint.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty) { _useFingerprint.IsEnabled = AskpassProtocol.IsValidFingerprintResponse(_fingerprint.Text); } };

        StackPanel content = new() { Name = "AskpassContent", Margin = new Thickness(12) };
        if (credentials)
        {
            content.Children.Add(PromptScroll()); content.Children.Add(_input);
            Button cancel = Button("CancelAskpass", "Cancel", Cancel); cancel.IsCancel = true;
            content.Children.Add(Buttons(_accept, cancel));
            Opened += (_, _) => _input.Focus();
        }
        else
        {
            Grid message = new() { Name = "AskpassMessage", ColumnDefinitions = new("26,*"), ColumnSpacing = 8 };
            string icon = request.Kind == AskpassPromptKind.HostVerification ? "Warning"
                : request.Kind == AskpassPromptKind.Permission ? "Key" : "information";
            Image symbol = ResetDialogControls.Icon(icon); symbol.Name = "AskpassMessageIcon"; symbol.VerticalAlignment = VerticalAlignment.Top; symbol.Margin = new Thickness(5, 2, 0, 0);
            message.Children.Add(symbol); Control prompt = PromptScroll(); Grid.SetColumn(prompt, 1); message.Children.Add(prompt); content.Children.Add(message);
            if (request.Kind == AskpassPromptKind.HostVerification)
            {
                StackPanel fingerprint = new() { Name = "AskpassFingerprintControls", Margin = new Thickness(34, 8, 0, 8), Spacing = 4 };
                fingerprint.Children.Add(new TextBlock { Name = "AskpassFingerprintLabel", Text = translations.Translate("Fingerprint") });
                Grid entry = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
                entry.Children.Add(_fingerprint); Grid.SetColumn(_useFingerprint, 1); entry.Children.Add(_useFingerprint); fingerprint.Children.Add(entry); content.Children.Add(fingerprint);
            }
            if (request.Kind == AskpassPromptKind.Notification)
            {
                Button close = Button("CloseAskpass", "Close", () => Decide(AskpassDecision.Dismissed), isDefault: true); close.IsCancel = true;
                StackPanel actions = Buttons(close); actions.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(actions);
                Opened += (_, _) => close.Focus();
            }
            else
            {
                Button yes = Button("YesAskpass", "Yes", () => Decide(AskpassDecision.Yes), isDefault: true);
                Button no = Button("NoAskpass", "No", () => Decide(AskpassDecision.No));
                StackPanel actions = Buttons(yes, no); actions.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(actions);
                Opened += (_, _) => yes.Focus();
            }
        }
        Content = content;
        AddHandler(KeyDownEvent, (_, key) =>
        {
            if (key.Key == Key.Escape) { key.Handled = true; Cancel(); }
            else if (key.Key == Key.Enter && Request.Kind == AskpassPromptKind.HostVerification && _fingerprint.IsKeyboardFocusWithin)
            {
                // An invalid fingerprint must never fall through to the window's default Yes button.
                key.Handled = true; AcceptFingerprint();
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closing += (_, _) => { if (!_decided) { Result = new(Request.Kind == AskpassPromptKind.Notification ? AskpassDecision.Dismissed : AskpassDecision.Cancelled); } };
        Closed += (_, _) => { _input.Text = ""; _fingerprint.Text = ""; _prompt.Text = ""; };

        Button Button(string name, string caption, Action action, bool isDefault = false)
        {
            Button button = new() { Name = name, Content = translations.Translate(caption), IsDefault = isDefault,
                MinWidth = 75, Height = 25, MinHeight = 0, Padding = new Thickness(8, 0) };
            button.Click += (_, _) => action(); return button;
        }
        ScrollViewer PromptScroll() => new()
        {
            Name = "AskpassPromptScroll", Content = _prompt, MaxHeight = 240,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        StackPanel panel = new() { Name = "AskpassButtons", Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, Spacing = 18 };
        foreach (Button button in buttons) { panel.Children.Add(button); }
        return panel;
    }

    private void AcceptInput()
    {
        if (Request.Kind is not (AskpassPromptKind.Secret or AskpassPromptKind.Username)) { return; }
        string value = _input.Text ?? "";
        if (IsSingleLine(value)) { Decide(AskpassDecision.Submit, value); }
    }

    private void AcceptFingerprint()
    {
        if (Request.Kind == AskpassPromptKind.HostVerification && AskpassProtocol.IsValidFingerprintResponse(_fingerprint.Text))
        { Decide(AskpassDecision.Submit, _fingerprint.Text); }
    }

    private void Cancel() => Decide(Request.Kind == AskpassPromptKind.Notification ? AskpassDecision.Dismissed : AskpassDecision.Cancelled);
    private void Decide(AskpassDecision decision, string? value = null)
    {
        if (_decided || !IsVisible) { return; }
        bool allowed = Request.Kind switch
        {
            AskpassPromptKind.Secret or AskpassPromptKind.Username => decision is AskpassDecision.Submit or AskpassDecision.Cancelled,
            AskpassPromptKind.YesNo or AskpassPromptKind.Permission => decision is AskpassDecision.Yes or AskpassDecision.No or AskpassDecision.Cancelled,
            AskpassPromptKind.HostVerification => decision is AskpassDecision.Yes or AskpassDecision.No or AskpassDecision.Submit or AskpassDecision.Cancelled,
            AskpassPromptKind.Notification => decision == AskpassDecision.Dismissed,
            _ => false
        };
        if (!allowed) { return; }
        Result = new(decision, value); _decided = true; Close();
    }

    internal static bool IsSingleLine(string response) => AskpassProtocol.IsSingleLine(response);
}
