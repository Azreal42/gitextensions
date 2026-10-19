using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The Windows suppressible Yes/No decision; suppression is independent of the answer.</summary>
internal static class ConfirmationPrompt
{
    internal static async Task<bool> ShowAsync(Window owner, string title, string message, bool suppressed,
        Action remember, CancellationToken token, bool warning = true, string? acceptLabel = null, string? cancelLabel = null, bool acceptIsDefault = true)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return false; }
        if (suppressed) { return true; }
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Window dialog = new() { Name = "SuppressibleConfirmation", Title = title, Width = 530, SizeToContent = SizeToContent.Height,
            CanMinimize = false, CanMaximize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant };
        CheckBox check = new() { Name = "ConfirmationDontShowAgain", Content = new AccessText
            { Text = translations.TranslateAccessKeys("TranslatedStrings", "_dontShowAgain", "Text", "&Don't show me this message again") }, MinHeight = 24 };
        Button yes = new() { Name = "ConfirmationYes", Content = acceptLabel ?? translations.Translate("Yes"), IsDefault = acceptIsDefault, MinWidth = 75 };
        Button no = new() { Name = "ConfirmationNo", Content = cancelLabel ?? translations.Translate("No"), IsCancel = true, IsDefault = !acceptIsDefault, MinWidth = 75 };
        bool answered = false;
        yes.Click += (_, _) => { answered = true; dialog.Close(true); };
        no.Click += (_, _) => { answered = true; dialog.Close(false); };
        Grid body = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        body.Children.Add(ResetDialogControls.Icon(warning ? "Warning" : "information"));
        TextBlock text = new() { Name = "ConfirmationMessage", Text = message, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1); body.Children.Add(text);
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children = { body, check, ExportDialogControls.Buttons(yes, no) } };
        dialog.Opened += (_, _) => { (acceptIsDefault ? yes : no).Focus(); ModalWindowActivation.AfterMapping(dialog, () => !token.IsCancellationRequested); };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try
        {
            bool accepted = await dialog.ShowDialog<bool>(owner);
            token.ThrowIfCancellationRequested();
            if (!owner.IsVisible) { return false; }
            // Owner/cancellation/X close can complete before owner.IsVisible becomes false.
            // Only an explicit answer may persist suppression.
            if (answered && check.IsChecked == true)
            {
                try { remember(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    await Dialogs.ShowMessageAsync(owner, translations.Translate("Settings"),
                        translations.Translate("Unable to save confirmation preferences."), warning: true, token);
                    return false;
                }
            }
            return accepted;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        finally { dialog.Close(); }
    }

    internal static void Remember(string name, string? profileDirectory = null)
    {
        ConfirmationsSettingsSession session = new(profileDirectory);
        session.Save(session.Initial.With(name, true));
    }
}
