using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ResetChangesDialog : Window
{
    internal enum Decision { Cancel, Reset, ResetAndDelete }
    private readonly CheckBox _deleteNew = new() { Name = "ResetDeleteNewFiles", Height = 24 };

    internal ResetChangesDialog(bool hasExistingFiles, bool hasNewFiles, string message)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string T(string key, string source) => translations.Translate("FormResetChanges", key, "Text", source);
        AccessText Caption(string key, string source) => new() { Text = translations.TranslateAccessKeys("FormResetChanges", key, "Text", source) };
        Name = "ResetChangesConfirmation"; Title = T("$this", "Reset changes");
        Width = 460; MinWidth = 380; MaxHeight = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _deleteNew.Content = Caption("cbDeleteNewFilesAndDirectories", "Also delete &new files and/or directories");
        _deleteNew.IsChecked = !hasExistingFiles;
        _deleteNew.IsEnabled = hasExistingFiles && hasNewFiles;
        Button cancel = new() { Name = "CancelResetChanges", Content = Caption("btnCancel", "&Cancel"), IsDefault = true, IsCancel = true };
        Button reset = new() { Name = "ConfirmResetChanges", Content = Caption("btnReset", "R&eset") };
        cancel.Click += (_, _) => Close(Decision.Cancel);
        reset.Click += (_, _) => Close(_deleteNew.IsChecked == true ? Decision.ResetAndDelete : Decision.Reset);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(Decision.Cancel); } };
        Content = new StackPanel { Margin = new Thickness(8), Spacing = 8, Children =
        {
            new TextBox { Name = "ResetChangesMessage", Text = message, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 340, BorderThickness = default, Focusable = false, IsTabStop = false },
            new TextBlock { Text = T("lblDeleteHint", "This will delete any uncommitted work."), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            _deleteNew,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 5, Children = { reset, cancel } }
        } };
    }
}
