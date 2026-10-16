using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>BranchComboBox's editable, space-separated selection and shared checked picker.</summary>
internal sealed class BranchSelectionComboBox : UserControl
{
    private readonly ComboBox _branches = new()
    {
        Name = "BranchSelectionText", IsEditable = true, IsTextSearchEnabled = false,
        Height = 24, MinHeight = 24, Padding = new Thickness(3, 0), MaxDropDownHeight = 280,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _multiple = new()
    {
        Name = "SelectMultipleBranches", Content = OriginalMenuItems.Icon("SelectBranch.png"),
        Width = 24, Height = 24, Padding = new Thickness(2), Margin = new Thickness(3, 0, 0, 0)
    };
    private IReadOnlyList<GitReference> _choices = [];
    private bool _picking;

    internal BranchSelectionComboBox()
    {
        Height = 26;
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_multiple, 1);
        grid.Children.Add(_branches); grid.Children.Add(_multiple); Content = grid;
        _branches.PropertyChanged += (_, args) =>
        {
            if (args.Property == ComboBox.TextProperty) { SelectionChanged?.Invoke(this, EventArgs.Empty); }
        };
        _multiple.Click += async (_, _) => await PickMultipleAsync();
    }

    internal event EventHandler? SelectionChanged;
    internal Control Editor => _branches;
    internal string SelectionText { get => _branches.Text ?? ""; set => _branches.Text = value; }
    internal IReadOnlyList<GitReference> Choices
    {
        get => _choices;
        set { _choices = value; _branches.ItemsSource = value.Select(branch => branch.ShortName).ToArray(); }
    }

    internal string[] UnknownNames => Names().Where(name => !_choices.Any(branch => branch.ShortName == name)).ToArray();
    internal GitReference[] SelectedBranches => Names().Select(name => _choices.FirstOrDefault(branch => branch.ShortName == name))
        .OfType<GitReference>().ToArray();
    private string[] Names() => SelectionText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    internal void FocusEditor() => _branches.Focus();

    private async Task PickMultipleAsync()
    {
        if (_picking || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        _picking = true;
        try
        {
            SelectMultipleBranchesDialog picker = new(_choices, SelectedBranches.Select(branch => branch.Name))
            { RequestedThemeVariant = owner.ActualThemeVariant };
            await picker.ShowDialog(owner);
            if (owner.IsVisible) { SelectionText = string.Join(' ', picker.SelectedBranches.Select(branch => branch.ShortName)); FocusEditor(); }
        }
        finally { _picking = false; }
    }
}
