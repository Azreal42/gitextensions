using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormSelectMultipleBranches: check on click, with one explicit OK action.</summary>
public sealed class SelectMultipleBranchesDialog : Window
{
    private readonly IReadOnlyList<GitReference> _branches;
    private readonly HashSet<string> _checked;

    public SelectMultipleBranchesDialog(IReadOnlyList<GitReference> branches, IEnumerable<string>? selected = null)
    {
        _branches = branches;
        _checked = new(selected ?? [], StringComparer.Ordinal);
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string Text(string key, string value) => translations.Translate("FormSelectMultipleBranches", key, "Text", value);
        Name = "SelectMultipleBranchesDialog";
        Title = Text("$this", "Select multiple branches");
        Width = 263; Height = 252; MinWidth = MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DockPanel root = new() { Margin = new Thickness(12, 9, 12, 6) };
        TextBlock label = new() { Text = Text("selectBranchesLabel", "Select branches"), Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Top); root.Children.Add(label);
        Button accept = new() { Name = "AcceptBranchSelection", Content = Text("okButton", "OK"), IsDefault = true, MinWidth = 75, Height = 25,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        accept.Click += (_, _) => Close();
        DockPanel.SetDock(accept, Dock.Bottom); root.Children.Add(accept);
        // Preserve every check while scrolling, without using list selection as checked state.
        StackPanel choices = new() { Name = "MultipleBranchChoices", Spacing = 0 };
        foreach (GitReference branch in branches)
        {
            CheckBox choice = new() { Content = branch.ShortName, Tag = branch.Name, IsChecked = _checked.Contains(branch.Name),
                Height = 20, MinHeight = 20, Padding = new Thickness(0), Margin = new Thickness(2, 0) };
            choice.IsCheckedChanged += (_, _) => { if (choice.IsChecked == true) { _checked.Add(branch.Name); } else { _checked.Remove(branch.Name); } };
            choices.Children.Add(choice);
        }
        root.Children.Add(ClassicChrome.Border(new ScrollViewer { Content = choices }, new Thickness(1)));
        Content = root;
        Opened += (_, _) => choices.Children.FirstOrDefault()?.Focus();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    public IReadOnlyList<GitReference> SelectedBranches => _branches.Where(branch => _checked.Contains(branch.Name)).ToArray();
}
