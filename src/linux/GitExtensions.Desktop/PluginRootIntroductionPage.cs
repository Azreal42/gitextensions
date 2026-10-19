using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original informational root page; it does not discover or activate plugins.</summary>
internal sealed class PluginRootIntroductionPage : UserControl
{
    internal PluginRootIntroductionPage(TranslationCatalog translations)
    {
        Name = "PluginRootIntroductionPage";
        Height = 229;
        Content = new TextBlock
        {
            Name = "label1", Margin = new Thickness(12, 13, 0, 0), Width = 284, Height = 132,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Text = translations.Translate("PluginRootIntroductionPage", "label1", "Text",
                "Select one of the subnodes to view or edit the settings of a Git Extensions Plugin.")
        };
    }
}
