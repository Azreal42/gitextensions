using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class Dialogs
{
    public static async Task<string[]?> PromptAsync(Window owner, string title, params (string Label, string Value)[] fields)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        title = translations.Translate(title);
        Window dialog = new()
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        StackPanel content = new() { Margin = new Thickness(12), Spacing = 6 };
        List<TextBox> inputs = [];
        foreach ((string label, string value) in fields)
        {
            content.Children.Add(new TextBlock { Text = translations.Translate(label), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            TextBox input = new() { Text = value };
            content.Children.Add(input);
            inputs.Add(input);
        }

        Button cancel = new() { Content = translations.Translate("Cancel"), IsCancel = true };
        Button accept = new() { Content = title, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        accept.Click += (_, _) => dialog.Close(inputs.Select(i => i.Text ?? "").ToArray());
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, accept }
        });
        dialog.Content = content;
        dialog.Opened += (_, _) => inputs.FirstOrDefault()?.Focus();
        return await dialog.ShowDialog<string[]?>(ActiveOwner(owner));
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        title = translations.Translate(title);
        message = translations.Translate(message);
        Window dialog = new()
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Button cancel = new() { Content = translations.Translate("Cancel"), IsCancel = true, IsDefault = true };
        Button accept = new() { Content = title };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, accept } }
            }
        };
        return await dialog.ShowDialog<bool>(ActiveOwner(owner));
    }

    private static Window ActiveOwner(Window owner)
    {
        while (owner.OwnedWindows.LastOrDefault(window => window.IsActive && window.IsVisible) is Window child) { owner = child; }
        return owner;
    }
}
