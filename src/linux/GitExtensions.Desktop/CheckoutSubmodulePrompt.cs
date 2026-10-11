using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record SubmoduleCheckoutDecision(bool Update, bool Remember);

internal sealed class CheckoutSubmodulePrompt : Window
{
    private readonly CheckBox _remember = new() { Name = "RememberSubmoduleCheckout", Height = 24, MinHeight = 24, Padding = new Thickness(0) };

    internal CheckoutSubmodulePrompt()
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string T(string item, string source) => translations.Translate("MessageBoxes", item, "Text", source);
        Title = T("_updateSubmodules", "Update submodules");
        Width = 520; MinWidth = 460; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _remember.Content = T("_rememberChoice", "&Remember choice").Replace("&", "", StringComparison.Ordinal);
        Button yes = new() { Name = "UpdateCheckoutSubmodules", Content = translations.Translate("Yes"), IsDefault = true, Height = 24, MinHeight = 24, MinWidth = 75, Padding = new Thickness(8, 0) };
        Button no = new() { Name = "SkipCheckoutSubmodules", Content = translations.Translate("No"), Height = 24, MinHeight = 24, MinWidth = 75, Padding = new Thickness(8, 0) };
        yes.Click += (_, _) => Close(new SubmoduleCheckoutDecision(true, _remember.IsChecked == true));
        no.Click += (_, _) => Close(new SubmoduleCheckoutDecision(false, _remember.IsChecked == true));
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(null); } };
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children =
        {
            new TextBlock { Name = "CheckoutSubmoduleHeading", Text = T("_theRepositorySubmodules", "Update submodules on checkout?"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Name = "CheckoutSubmoduleDescription", Text = T("_updateSubmodulesToo", "Since this repository has submodules, it's necessary to update them on every checkout.\r\n\r\nThis will just checkout on the submodule the commit determined by the superproject."), TextWrapping = TextWrapping.Wrap },
            _remember,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { yes, no } }
        } };
    }

    internal static async Task<SubmoduleCheckoutDecision?> ShowAsync(Window owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        CheckoutSubmodulePrompt dialog = new();
        void OwnerClosed(object? sender, EventArgs args) => dialog.Close(null);
        owner.Closed += OwnerClosed;
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(null)));
        try { SubmoduleCheckoutDecision? result = await dialog.ShowDialog<SubmoduleCheckoutDecision?>(owner); token.ThrowIfCancellationRequested(); return result; }
        finally { owner.Closed -= OwnerClosed; }
    }
}
