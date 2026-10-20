using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original SearchWindow/SearchControl file chooser: literal substring search, twenty candidates and explicit acceptance.</summary>
internal sealed class FileListFindDialog : Window
{
    private readonly TextBox _search = new() { Name = "txtSearchBox", Height = 23, MinHeight = 23 };
    private readonly ListBox _matches = new() { Name = "listBoxSearchResult", SelectionMode = SelectionMode.Single, Padding = default, BorderThickness = default, IsVisible = false };
    private IReadOnlyList<FileStatusListEntry> _candidates;
    private readonly string _workingDirectory;
    internal FileStatusListEntry? SelectedEntry { get; private set; }

    internal FileListFindDialog(IReadOnlyList<FileStatusListEntry> candidates, string workingDirectory, TranslationCatalog translations)
    {
        _candidates = candidates.ToArray(); _workingDirectory = workingDirectory;
        Name = "SearchWindow"; Title = translations.Translate("SearchWindow", "lblEnterFileName", "Text", "Enter File Name");
        Width = MinWidth = 300; Height = 48; CanResize = CanMinimize = CanMaximize = false;
        SystemDecorations = SystemDecorations.None; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _matches.ItemTemplate = new FuncDataTemplate<FileStatusListEntry>((entry, _) => new TextBlock { Text = entry?.Path, TextWrapping = TextWrapping.NoWrap });
        _matches.DoubleTapped += (_, args) => { args.Handled = true; Accept(); };
        Border label = ClassicChrome.Border(new TextBlock { Name = "lblEnterFileName", Text = Title, Padding = new Thickness(0, 7, 0, 0) }, new Thickness(1));
        Grid root = new() { RowDefinitions = new("Auto,23,*") }; root.Children.Add(label);
        Grid.SetRow(_search, 1); root.Children.Add(_search);
        Border list = ClassicChrome.Border(_matches, new Thickness(1)); Grid.SetRow(list, 2); root.Children.Add(list); Content = root;
        _search.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { Find(); } };
        Opened += (_, _) => { _search.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; SelectedEntry = null; Close(); }
            else if (args.Key == Key.Enter) { args.Handled = true; Accept(); }
            else if (args.Key is Key.Down or Key.Up && _matches.ItemCount > 1)
            {
                int delta = args.Key == Key.Down ? 1 : -1;
                _matches.SelectedIndex = (_matches.SelectedIndex + delta + _matches.ItemCount) % _matches.ItemCount;
                _matches.ScrollIntoView(_matches.SelectedItem!); args.Handled = true;
            }
        };
    }
    // Standalone CLI discovery feeds the same selector after its owned HEAD read has drained.
    internal void SetCandidates(IReadOnlyList<FileStatusListEntry> candidates)
    {
        _candidates = candidates.ToArray();
        if (_search.Text is not null) { Find(); }
    }

    private void Accept() { SelectedEntry = _matches.SelectedItem as FileStatusListEntry; Close(); }
    private void Find()
    {
        static string Posix(string value) => Path.DirectorySeparatorChar == '\\' ? value.Replace('\\', '/') : value;
        string pattern = Posix(_search.Text ?? "");
        string directory = Posix(_workingDirectory).TrimEnd('/') + "/";
        bool rooted = pattern.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        if (rooted) { pattern = pattern[directory.Length..].TrimStart('/'); }
        bool Matches(string? name) => name is not null && (rooted ? name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) : name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        FileStatusListEntry[] matches = _candidates.Where(entry => Matches(entry.Path) || Matches(entry.OriginalPath)).Take(20).ToArray();
        _matches.ItemsSource = matches; _matches.SelectedIndex = matches.Length == 0 ? -1 : 0; _matches.IsVisible = matches.Length > 0;
        Typeface face = new(_matches.FontFamily, _matches.FontStyle, _matches.FontWeight, _matches.FontStretch);
        FormattedText Measure(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, _matches.FontSize, Brushes.Black);
        double rowHeight = Math.Ceiling(Measure("Ag").Height);
        Width = Math.Max(300, matches.Select(entry => Measure(entry.Path).WidthIncludingTrailingWhitespace + 4).DefaultIfEmpty(300).Max());
        Height = 48 + (matches.Length == 0 ? 0 : Math.Min(800, rowHeight * (matches.Length + 1)));
    }
}
