using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class AskpassWindow : Window
{
    private readonly TextBox _input;
    private readonly Button _accept;
    internal string? Response { get; private set; }

    internal AskpassWindow(string prompt)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Title = "Git Extensions";
        Width = 450;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        _input = new TextBox
        {
            Name = "AskpassInput",
            PasswordChar = IsUsernamePrompt(prompt) ? '\0' : '●',
            RevealPassword = false,
            AcceptsReturn = false,
            AcceptsTab = false,
            MinHeight = 24,
            Margin = new Thickness(0, 5, 0, 8)
        };
        _accept = new Button { Name = "AcceptAskpass", Content = translations.Translate("OK"), IsDefault = true, MinWidth = 75 };
        Button cancel = new() { Name = "CancelAskpass", Content = translations.Translate("Cancel"), IsCancel = true, MinWidth = 75 };
        _accept.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        _input.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
            {
                _accept.IsEnabled = IsSingleLine(_input.Text ?? "");
            }
        };
        KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                Close();
                key.Handled = true;
            }
        };
        Opened += (_, _) => _input.Focus();
        Closed += (_, _) => _input.Text = "";
        Content = new StackPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                new TextBlock { Text = translations.Translate("Send input"), FontWeight = FontWeight.SemiBold },
                new TextBlock { Name = "AskpassPrompt", Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
                _input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { _accept, cancel } }
            }
        };
    }

    private void Accept()
    {
        string value = _input.Text ?? "";
        if (IsSingleLine(value))
        {
            Response = value;
            Close();
        }
    }

    private static bool IsUsernamePrompt(string prompt)
        => prompt.StartsWith("Username", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSingleLine(string response) => response.IndexOfAny(['\0', '\r', '\n']) < 0;
}
