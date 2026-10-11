using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public sealed class DashboardCategoryDialog : Window
{
    private readonly HashSet<string> _existing;
    private readonly string? _original;
    private readonly TextBox _name = new() { Name = "DashboardCategoryName", Height = 24 };
    private readonly TextBlock _error = new() { Name = "DashboardCategoryError", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly Button _accept = new() { Name = "DashboardCategoryAccept", IsDefault = true, MinWidth = 124 };
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();

    public DashboardCategoryDialog(IEnumerable<string> existing, string? original = null)
    {
        _existing = existing.Where(category => category != original).ToHashSet(StringComparer.Ordinal);
        _original = original;
        Title = T(original is null ? "Enter Caption" : "Rename category");
        Width = 400; MinWidth = 400; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = original ?? "";
        _accept.Content = T("OK");
        Button cancel = new() { Content = T("Cancel"), IsCancel = true, MinWidth = 75 };
        cancel.Click += (_, _) => Close();
        _accept.Click += (_, _) => Accept();
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new TextBlock { Text = T("Category name"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        Grid.SetColumn(_name, 1); row.Children.Add(_name);
        Content = new StackPanel { Spacing = 6, Margin = new Thickness(4), Children =
        {
            row, _error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { cancel, _accept } }
        } };
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { _error.IsVisible = false; UpdateActions(); } };
        ActualThemeVariantChanged += (_, _) => _error.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Colors.Red));
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
        _error.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Colors.Red));
        UpdateActions();
    }

    private void Accept()
    {
        string name = _name.Text ?? "";
        if (string.IsNullOrWhiteSpace(name)) { ShowError(T("Category name is required")); return; }
        if (_existing.Contains(name)) { ShowError(T("Category name already exists")); return; }
        Close(name);
    }
    private void ShowError(string message) { _error.Text = message; _error.IsVisible = true; _name.Focus(); }
    private void UpdateActions() => _accept.IsEnabled = _name.Text != _original;
    private string T(string source) => _translations.Translate(source);
}
