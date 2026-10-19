using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ContributorsDialog : Window
{
    internal ContributorsDialog(TranslationCatalog? translations = null)
    {
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        string T(string item, string source) => text.Translate("FormContributors", item, "Text", source);
        ContributorsData names = ContributorsCatalog.Load();
        Name = "ContributorsDialog"; Title = T("_caption", "The application would not be possible without...");
        Width = 624; Height = 442; CanResize = false; CanMinimize = false; CanMaximize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TabControl tabs = new() { Name = "ContributorsTabs", SelectedIndex = 0, Padding = new Thickness(0) };
        TextBox developers = Add("Developers", "_developers", "Developers",
            T("_team", "Team") + ":\n" + names.Team + "\n\n" + T("_contributors", "Contributors") + ":\n" + names.Coders);
        Add("Translators", "_translators", "Translators", names.Translators);
        Add("Designers", "_designers", "Designers", names.Designers);
        Content = tabs;
        Opened += (_, _) => { developers.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }, RoutingStrategies.Tunnel);

        TextBox Add(string name, string item, string caption, string value)
        {
            TextBox content = new()
            {
                Name = "Contributors" + name, Text = value, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Margin = new Thickness(0), Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
            };
            ScrollViewer.SetVerticalScrollBarVisibility(content, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(content, ScrollBarVisibility.Disabled);
            tabs.Items.Add(new TabItem { Name = "Contributors" + name + "Tab", Header = T(item, caption), Content = content });
            return content;
        }
    }
}
