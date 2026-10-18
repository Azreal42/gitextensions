using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public enum CommitEmptyChoice { Cancel, StageUnstaged, MakeEmpty }

/// <summary>The three original FormCommit choices when the index is empty.</summary>
public sealed class CommitEmptyDialog : Window
{
    public CommitEmptyDialog(bool canStage, bool filtered)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string C(string item, string source) => translations.Translate("FormCommit", item, "Text", source);
        Title = C("_noFilesStagedCommitCaption", "Confirm commit");
        Width = 570;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button stage = new() { Name = "StageUnstaged", IsEnabled = canStage, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = filtered ? C("_noFilesStagedCommitAllFilteredUnstagedOption", "Stage and commit the unstaged files that match your filter")
                : C("_noFilesStagedCommitAllUnstagedOption", "Stage and commit all unstaged files"), TextWrapping = TextWrapping.Wrap } };
        Button empty = new() { Name = "MakeEmpty", Content = C("_noFilesStagedMakeEmptyCommitOption", "Make an empty commit"), HorizontalContentAlignment = HorizontalAlignment.Left };
        ClassicCommandLink.Configure(this, "command-link", 56d, disabledForeground: true);
        ClassicCommandLink.Apply(stage, "command-link");
        ClassicCommandLink.Apply(empty, "command-link");
        Button cancel = new() { Name = "Cancel", Content = translations.Translate("Cancel"), IsCancel = true, IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right };
        stage.Click += (_, _) => Close(CommitEmptyChoice.StageUnstaged);
        empty.Click += (_, _) => Close(CommitEmptyChoice.MakeEmpty);
        cancel.Click += (_, _) => Close(CommitEmptyChoice.Cancel);
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(CommitEmptyChoice.Cancel); } };
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("32,12,*"), Margin = new Thickness(18, 18, 18, 12) };
        // FormCommit uses TaskDialogIcon.Error. The similarly named FatCow asset
        // is a warning triangle, so preserve the native error symbol instead.
        heading.Children.Add(ClassicCommandLink.ErrorIcon(VerticalAlignment.Top));
        TextBlock instructions = new()
        {
            Text = C("_noFilesStagedCommitInstructions", "There aren't any changes in the staging area.\nHow do you want to proceed?"),
            FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 3, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(instructions, 2);
        heading.Children.Add(instructions);
        Border footer = new() { Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0), Child = cancel };
        footer.Bind(Border.BackgroundProperty, Resource("Menu"));
        footer.Bind(Border.BorderBrushProperty, Resource("Border"));
        Content = new StackPanel { Children =
        {
            heading,
            new StackPanel { Margin = new Thickness(48, 0, 12, 12), Spacing = 2, Children = { stage, empty } },
            footer
        } };
    }

    private static DynamicResourceExtension Resource(string name) => new("Classic" + name + "Brush");
}
