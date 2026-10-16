using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class ScriptInputDialogControls
{
    internal static TextBox Text(string name, string? value = null) => new()
    {
        Name = name, Text = value, AcceptsReturn = false, AcceptsTab = false,
        Padding = new Thickness(3, 1), VerticalContentAlignment = VerticalAlignment.Center
    };
    internal static Button Accept(string name, TranslationCatalog translations, string category, double width, double height) => new()
    {
        Name = name, Content = new AccessText { Text = translations.TranslateAccessKeys(category, name, "Text", "&OK") },
        MinWidth = width, Height = height, MinHeight = height, IsDefault = true, Padding = new Thickness(6, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center
    };
    internal static void Configure(Window window, InputElement input, Func<bool> canActivate, bool selectAll = false)
    {
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Opened += (_, _) =>
        {
            input.Focus();
            if (selectAll && input is TextBox text) { text.SelectAll(); }
            ModalWindowActivation.AfterMapping(window, canActivate);
        };
    }
    // FormFilePrompt uses GitCommands.StringExtensions.Quote, not a shell/argv encoder.
    internal static string QuoteFilePath(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}
