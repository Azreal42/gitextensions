using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class UndoLastCommitConfirmationDialog : Window
{
    private readonly CheckBox _dontShowAgain = new() { Name = "UndoDontShowAgain", MinHeight = 24 };

    private UndoLastCommitConfirmationDialog()
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string Text(string item, string source) => translations.Translate("FormBrowse", item, "Text", source);
        Name = "UndoLastCommitConfirmation"; Title = Text("_undoLastCommitCaption", "Undo last commit");
        Width = 520; MinWidth = 380; MaxHeight = 540; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _dontShowAgain.Content = new AccessText { Text = translations.TranslateAccessKeys("TranslatedStrings", "_dontShowAgain", "Text", "&Don't show me this message again") };
        Button yes = new() { Name = "ConfirmUndoLastCommit", Content = translations.Translate("Yes"), IsDefault = true, MinWidth = 75, Height = 25 };
        Button no = new() { Name = "CancelUndoLastCommit", Content = translations.Translate("No"), IsCancel = true, MinWidth = 75, Height = 25 };
        yes.Click += (_, _) => Close(true); no.Click += (_, _) => Close(false);
        Image warning = ResetDialogControls.Icon("Warning"); warning.Name = "UndoLastCommitWarning";
        Grid message = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        message.Children.Add(warning);
        TextBlock text = new() { Name = "UndoLastCommitMessage", Text = Text("_undoLastCommitText",
            "You will still be able to find all the commit's changes in the staging area\n\nDo you want to continue?"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1); message.Children.Add(text);
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children = { message, _dontShowAgain, ExportDialogControls.Buttons(yes, no) } };
    }

    internal static async Task<bool> ShowAsync(Window owner, BrowseCommandPreferencesStore store, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return false; }
        if (store.Load().DontConfirmUndoLastCommit) { return true; }
        UndoLastCommitConfirmationDialog dialog = new() { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(false); } }));
        try
        {
            token.ThrowIfCancellationRequested();
            bool confirmed = await dialog.ShowDialog<bool>(owner);
            // MessageBoxes.ConfirmSuppressible writes this after the dialog returns, including No/Escape/close.
            if (dialog._dontShowAgain.IsChecked == true) { store.Save(store.Load() with { DontConfirmUndoLastCommit = true }); }
            token.ThrowIfCancellationRequested();
            return confirmed;
        }
        finally { dialog.Close(); }
    }
}
