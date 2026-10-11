using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

internal sealed class ComparisonRevisionSelector : UserControl, IDisposable
{
    private readonly ComboBox _kind;
    private readonly TextBox _revision;
    private readonly Border _tint;
    private readonly bool _base;
    private readonly Func<string, string> _translate;
    private readonly List<Bitmap> _icons = [];
    internal Button BranchButton { get; }
    internal Button CommitButton { get; }
    internal event EventHandler? Edited;

    internal ComparisonRevisionSelector(string prefix, bool isBase, string revision, Func<string, string> translate)
    {
        _base = isBase;
        _translate = translate;
        _kind = ReferenceDialogControls.Choice(prefix + "Kind");
        _kind.Width = 115;
        _kind.ItemsSource = new[] { translate("Commit"), translate("Index"), translate("Working directory") };
        _kind.SelectedIndex = 0;
        _revision = ReferenceDialogControls.Text(prefix + "Revision", revision);
        _revision.MinWidth = 120;
        BranchButton = Icon(prefix + "Branch", "BranchCheckout.png", translate("Select another branch"));
        CommitButton = Icon(prefix + "Commit", "SelectRevision.png", translate("Select another commit"));
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        row.Children.Add(_kind);
        _tint = ClassicChrome.Border(_revision, new Thickness(1), new Thickness(0));
        _tint.Margin = new Thickness(4, 0);
        Grid.SetColumn(_tint, 1); row.Children.Add(_tint);
        Grid.SetColumn(BranchButton, 2); row.Children.Add(BranchButton);
        Grid.SetColumn(CommitButton, 3); row.Children.Add(CommitButton);
        Content = ExportDialogControls.Group(translate(isBase ? "BASE" : "Compare"), row);
        _kind.SelectionChanged += (_, _) => { _revision.IsEnabled = _kind.SelectedIndex == 0; Edited?.Invoke(this, EventArgs.Empty); };
        _revision.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { Edited?.Invoke(this, EventArgs.Empty); } };
        ActualThemeVariantChanged += (_, _) => ApplyTint();
        ApplyTint();
    }

    internal GitCompareRevision Read() => new(_revision.Text ?? "HEAD", (GitCompareRevisionKind)Math.Max(0, _kind.SelectedIndex));
    internal void SetSummary(string text) => ToolTip.SetTip(_revision, text);
    internal void Set(GitCompareRevision revision) { _revision.Text = revision.Revision; _kind.SelectedIndex = (int)revision.Kind; }
    internal void SetBare(bool bare)
    {
        GitCompareRevisionKind kind = Read().Kind;
        _kind.ItemsSource = new[]
        {
            new ComboBoxItem { Content = _translate("Commit") },
            new ComboBoxItem { Content = _translate("Index"), IsEnabled = !bare },
            new ComboBoxItem { Content = _translate("Working directory"), IsEnabled = !bare }
        };
        _kind.SelectedIndex = (int)kind;
    }
    private void ApplyTint()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        _tint.Background = Brush.Parse(_base ? dark ? "#6e1919" : "#ffc8c8" : dark ? "#076641" : "#c8ffc8");
        _revision.Background = _tint.Background;
    }
    private Button Icon(string name, string file, string tip)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + file));
        Bitmap icon = new(stream); _icons.Add(icon);
        Button button = new() { Name = name, Content = new Image { Source = icon, Width = 16, Height = 16 }, Width = 24, Height = 24, Padding = new Thickness(2) };
        ToolTip.SetTip(button, tip);
        return button;
    }
    public void Dispose() { foreach (Bitmap icon in _icons) { icon.Dispose(); } }
}
