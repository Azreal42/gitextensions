using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

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
        stage.Classes.Add("command-link");
        empty.Classes.Add("command-link");
        stage.Template = CommandLinkTemplate();
        empty.Template = CommandLinkTemplate();
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Resource("Window")),
                new Setter(Button.ForegroundProperty, Resource("Text")),
                new Setter(Button.BorderBrushProperty, Brushes.Transparent),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.PaddingProperty, new Thickness(10)),
                new Setter(Button.FontSizeProperty, DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 2),
                new Setter(Button.MinHeightProperty, 56d),
                new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link").Class(":pointerover"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Resource("Hover")), new Setter(Button.BorderBrushProperty, Resource("ButtonBorder")) }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link").Class(":pressed"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Resource("Pressed")) }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link").Class(":focus-visible"))
        {
            Setters = { new Setter(Button.BorderBrushProperty, Resource("Highlight")) }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link").Class(":disabled"))
        {
            Setters = { new Setter(Button.ForegroundProperty, Resource("Disabled")) }
        });
        // Fluent paints its ordinary-button states on PART_ContentPresenter.
        // The command link's surrounding chrome owns those states instead.
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("command-link").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0)),
                new Setter(ContentPresenter.PaddingProperty, new Thickness(0))
            }
        });
        Button cancel = new() { Name = "Cancel", Content = translations.Translate("Cancel"), IsCancel = true, IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right };
        stage.Click += (_, _) => Close(CommitEmptyChoice.StageUnstaged);
        empty.Click += (_, _) => Close(CommitEmptyChoice.MakeEmpty);
        cancel.Click += (_, _) => Close(CommitEmptyChoice.Cancel);
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(CommitEmptyChoice.Cancel); } };
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("32,12,*"), Margin = new Thickness(18, 18, 18, 12) };
        // FormCommit uses TaskDialogIcon.Error. The similarly named FatCow asset
        // is a warning triangle, so preserve the native error symbol instead.
        heading.Children.Add(new Panel
        {
            Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new Ellipse { Fill = Brushes.Red, Width = 32, Height = 32 },
                new PathIcon
                {
                    Data = Geometry.Parse("M 1,3 L 3,1 L 9,7 L 15,1 L 17,3 L 11,9 L 17,15 L 15,17 L 9,11 L 3,17 L 1,15 L 7,9 Z"),
                    Foreground = Brushes.White, Width = 18, Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            }
        });
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

    private static FuncControlTemplate<Button> CommandLinkTemplate() => new((button, scope) =>
    {
        PathIcon arrow = new()
        {
            Data = Geometry.Parse("M 1,6 L 11,6 L 7,2 L 10,2 L 17,9 L 10,16 L 7,16 L 11,12 L 1,12 Z"),
            Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center
        };
        arrow.Bind(PathIcon.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template);
        ContentPresenter label = new()
        {
            Name = "PART_ContentPresenter", Content = button.Content,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center
        };
        label.Bind(ContentPresenter.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template);
        scope.Register(label.Name!, label);
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("18,12,*") };
        content.Children.Add(arrow);
        Grid.SetColumn(label, 2);
        content.Children.Add(label);
        Border chrome = new() { Child = content, CornerRadius = new CornerRadius(3) };
        chrome.Bind(Border.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderThicknessProperty, new TemplateBinding(Button.BorderThicknessProperty), BindingPriority.Template);
        chrome.Bind(Border.PaddingProperty, new TemplateBinding(Button.PaddingProperty), BindingPriority.Template);
        return chrome;
    });
}
