using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original information and badge; opening its fixed donation site requires an explicit activation.</summary>
internal sealed class DonateDialog : Window
{
    internal DonateDialog(TranslationCatalog? translations = null, Action<Uri>? openUrl = null)
    {
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        Name = "DonateDialog"; Title = text.Translate("FormDonate", "$this", "Text", "Donate");
        Width = 508; MinWidth = 508; MinHeight = 266; SizeToContent = SizeToContent.Height;
        CanMinimize = CanMaximize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TextBlock explanation = new()
        {
            Name = "lblText", Text = text.Translate("FormDonate", "_donateText", "Text", DonationsCatalog.Explanation),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10), MaxWidth = 456,
            // Match the rectangular mouse surface of the original Windows label, including whitespace.
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent
        };
        using Stream stream = AssetLoader.Open(DonationsCatalog.BadgeAsset);
        Bitmap bitmap = new(stream);
        Image image = new() { Name = "DonateBadge", Source = bitmap, Stretch = Stretch.Uniform };
        Button badge = new()
        {
            Name = "pbxDonate", Content = image, Margin = new Thickness(12), MinWidth = 90, MinHeight = 35,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            // The template draws its own keyboard focus border; do not overlay the generic dashed adorner.
            Cursor = new Cursor(StandardCursorType.Hand), Template = BadgeTemplate(), FocusAdorner = null
        };
        badge.Classes.Add("donate-badge"); AutomationProperties.SetName(badge, Title);
        badge.Click += async (_, _) =>
        {
            try { (openUrl ?? OpenInBrowser)(DonationsCatalog.DonationUri); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { if (IsVisible) { await Dialogs.ShowErrorAsync(this, error.Message); } }
        };
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("donate-badge"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderBrushProperty, Brushes.Transparent),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)), new Setter(Button.PaddingProperty, new Thickness(0)) }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("donate-badge").Class(":pointerover"))
        { Setters = { new Setter(Button.BorderBrushProperty, new DynamicResourceExtension("ClassicLinkBrush")) } });
        Styles.Add(new Style(selector => selector.OfType<Button>().Class("donate-badge").Class(":focus-visible"))
        { Setters = { new Setter(Button.BorderBrushProperty, new DynamicResourceExtension("ClassicHighlightBrush")) } });
        Grid layout = new() { Name = "tableLayoutPanel", Margin = new Thickness(16), MinHeight = 234, RowDefinitions = new("Auto,80,*") };
        layout.Children.Add(explanation); Grid.SetRow(badge, 1); layout.Children.Add(badge); Content = layout;
        Opened += (_, _) => { badge.Focus(NavigationMethod.Pointer); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
        Closed += (_, _) => { image.Source = null; bitmap.Dispose(); };
    }

    private static void OpenInBrowser(Uri uri)
    {
        using Process? process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static FuncControlTemplate<Button> BadgeTemplate() => new((button, scope) =>
    {
        ContentPresenter content = new() { Name = "PART_ContentPresenter", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        content.Bind(ContentPresenter.ContentProperty, new TemplateBinding(Button.ContentProperty), BindingPriority.Template);
        scope.Register(content.Name!, content);
        Border border = new() { Name = "DonateBadgeBorder", Child = content };
        border.Bind(Border.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty), BindingPriority.Template);
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty), BindingPriority.Template);
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(Button.BorderThicknessProperty), BindingPriority.Template);
        border.Bind(Border.PaddingProperty, new TemplateBinding(Button.PaddingProperty), BindingPriority.Template);
        return border;
    });
}
