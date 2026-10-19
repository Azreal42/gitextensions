using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class Dialogs
{
    internal static Task<BugReportDecision> ShowErrorAsync(Window owner, ExceptionReportSnapshot snapshot,
        ExceptionReportContext context, TranslationCatalog translations, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? copyText = null, Func<Uri, CancellationToken, Task>? openIssue = null)
        => DesktopErrorReporter.ShowAsync(ActiveOwner(owner), snapshot, context, translations, cancellationToken, copyText, openIssue);

    public static Task ShowErrorAsync(Window owner, string message)
        => ShowMessageCoreAsync(owner, TranslationCatalog.LoadDefault().Translate("Error"), message, null, default);

    public static Task ShowMessageAsync(Window owner, string title, string message, bool warning,
        CancellationToken cancellationToken = default)
        => ShowMessageCoreAsync(owner, title, message, warning, cancellationToken);

    private static async Task ShowMessageCoreAsync(Window owner, string title, string message, bool? warning,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Window activeOwner = ActiveOwner(owner);
        Window dialog = new() { Name = "MessageDialog", Title = title, Width = 520,
            SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = activeOwner.ActualThemeVariant };
        Button accept = new() { Name = "MessageDialogAccept", Content = translations.Translate("OK"), IsDefault = true, IsCancel = true, MinWidth = 75 };
        accept.Click += (_, _) => dialog.Close();
        Grid body = new() { ColumnDefinitions = new(warning is null ? "*" : "Auto,*"), ColumnSpacing = 10 };
        if (warning is bool isWarning) { body.Children.Add(ResetDialogControls.Icon(isWarning ? "Warning" : "StatusBadgeError")); }
        TextBlock text = new() { Name = "MessageDialogText", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Grid.SetColumn(text, warning is null ? 0 : 1); body.Children.Add(text);
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            body,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { accept } }
        } };
        dialog.Opened += (_, _) =>
        {
            accept.Focus(); ModalWindowActivation.AfterMapping(dialog, () => dialog.IsVisible && !cancellationToken.IsCancellationRequested);
        };
        using CancellationTokenRegistration close = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        { if (dialog.IsVisible) { dialog.Close(); } }));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await dialog.ShowDialog(activeOwner);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { dialog.Close(); }
    }

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
        dialog.Opened += (_, _) => { inputs.FirstOrDefault()?.Focus(); ModalWindowActivation.AfterMapping(dialog, () => dialog.IsVisible); };
        return await dialog.ShowDialog<string[]?>(ActiveOwner(owner));
    }

    public static Task<bool> YesNoAsync(Window owner, string title, string message)
        => ConfirmAsync(owner, title, message, "Yes", "No", true);

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string? acceptLabel = null, string? cancelLabel = null, bool acceptIsDefault = false)
        => await ConfirmChoiceAsync(owner, title, message, acceptLabel, cancelLabel, acceptIsDefault) == true;

    // A nullable decision distinguishes a declined question from closing it.
    // The bool facade retains the historical false result in both cases.
    internal static async Task<bool?> ConfirmChoiceAsync(Window owner, string title, string message,
        string? acceptLabel = null, string? cancelLabel = null, bool acceptIsDefault = false, CancellationToken cancellationToken = default,
        TranslationCatalog? translations = null, ThemeVariant? requestedThemeVariant = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        translations ??= TranslationCatalog.LoadDefault();
        title = translations.Translate(title);
        message = translations.Translate(message);
        Window dialog = new()
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = requestedThemeVariant
        };
        Button cancel = new() { Content = translations.Translate(cancelLabel ?? "Cancel"), IsCancel = true, IsDefault = !acceptIsDefault };
        Button accept = new() { Content = translations.Translate(acceptLabel ?? title), IsDefault = acceptIsDefault };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        dialog.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; dialog.Close(null); } }, RoutingStrategies.Tunnel);
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
        dialog.Opened += (_, _) =>
        {
            (acceptIsDefault ? accept : cancel).Focus();
            ModalWindowActivation.AfterMapping(dialog, () => !cancellationToken.IsCancellationRequested);
        };
        using CancellationTokenRegistration close = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(null); } }));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool? answer = await dialog.ShowDialog<bool?>(ActiveOwner(owner));
            cancellationToken.ThrowIfCancellationRequested();
            return answer;
        }
        finally { dialog.Close(); }
    }

    private static Window ActiveOwner(Window owner)
    {
        while (owner.OwnedWindows.LastOrDefault(window => window.IsActive && window.IsVisible) is Window child) { owner = child; }
        return owner;
    }
}
