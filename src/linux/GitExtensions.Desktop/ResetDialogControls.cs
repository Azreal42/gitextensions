using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class ResetDialogControls
{
    internal static TextBlock Summary(string name, double minHeight = 147) => new()
    { Name = name, MinHeight = minHeight, MaxHeight = minHeight, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5) };
    internal static void SetSummary(TextBlock summary, GitCommit commit)
    {
        summary.Text = commit.Subject + "\n" + commit.AuthorName + " <" + commit.AuthorEmail + ">\n"
            + commit.AuthoredAt.LocalDateTime.ToString("g") + "\n" + commit.Hash + "\n" + commit.Body.TrimEnd('\n');
        ToolTip.SetTip(summary, new TextBlock { Text = summary.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 650, MaxHeight = 320, TextTrimming = TextTrimming.CharacterEllipsis });
    }

    internal static Image Icon(string name)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name + ".png"));
        Image image = new() { Source = new Bitmap(stream), Width = 16, Height = 16, Tag = name + ".png" };
        image.DetachedFromVisualTree += (_, _) => { if (image.Source is IDisposable bitmap) { bitmap.Dispose(); } image.Source = null; };
        return image;
    }

    internal static void UpdateModeColor(RadioButton radio, Color source)
    {
        bool dark = radio.ActualThemeVariant == ThemeVariant.Dark;
        Color background = dark ? Dim(source, Color.Parse("#323232")) : source;
        radio.Background = new SolidColorBrush(background);
        radio.Foreground = new SolidColorBrush(FileStatusListColors.Contrasting(dark ? Color.Parse("#f0f0f0") : Colors.Black, background));
    }
    // OtherColors.DimColor averages the light color and EditorBackground in linear sRGB.
    private static Color Dim(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static byte Channel(byte a, byte b)
        { double value = (Linear(a) + Linear(b)) / 2; return (byte)Math.Round(255 * (value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055)); }
        return Color.FromArgb(first.A, Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
    }

    internal static async Task<bool> ConfirmHardAsync(Window owner, TranslationCatalog translations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string Text(string item, string source) => translations.Translate("FormResetCurrentBranch", item, "Text", source);
        Window warning = new() { Name = "ResetHardWarning", Title = Text("_resetCaption", "Reset branch"), Width = 510,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant };
        Button yes = new() { Name = "ConfirmHardReset", Content = translations.Translate("Yes"), IsDefault = true, MinWidth = 75, Height = 25 };
        Button no = new() { Name = "CancelHardReset", Content = translations.Translate("No"), IsCancel = true, MinWidth = 75, Height = 25 };
        yes.Click += (_, _) => warning.Close(true); no.Click += (_, _) => warning.Close(false);
        warning.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Icon("Warning"), new TextBlock
                { Text = Text("_resetHardWarning", "You are about to discard ALL local changes, are you sure?"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 } } },
            ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (warning.IsVisible) { warning.Close(false); } }));
        bool confirmed = await warning.ShowDialog<bool>(owner); token.ThrowIfCancellationRequested(); return confirmed;
    }

    internal static async Task<bool> UpdateSubmodulesAsync(Window owner, GitRepository repository, bool promptIfUnset,
        CancellationToken token, Func<Window, CancellationToken, Task>? afterUpdate)
    {
        token.ThrowIfCancellationRequested();
        if (!owner.IsVisible || (await repository.GetSubmodulesAsync(token)).Count == 0 || !owner.IsVisible) { return false; }
        SubmoduleCheckoutPreferencesStore store = new(); bool? update = store.Load().Decision;
        if (update is null && promptIfUnset)
        {
            CheckoutSubmodulePrompt prompt = new() { RequestedThemeVariant = owner.ActualThemeVariant };
            using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (prompt.IsVisible) { prompt.Close(null); } }));
            SubmoduleCheckoutDecision? decision = await prompt.ShowDialog<SubmoduleCheckoutDecision?>(owner);
            token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return false; }
            update = decision?.Update;
            if (decision is { Remember: true }) { store.Remember(decision.Update); }
        }
        if (update != true || !owner.IsVisible) { return false; }
        bool succeeded = await SubmoduleUpdateDialog.ShowAsync(owner, repository, token);
        token.ThrowIfCancellationRequested();
        if (succeeded && owner.IsVisible && afterUpdate is not null) { await afterUpdate(owner, token); }
        return succeeded;
    }
}
