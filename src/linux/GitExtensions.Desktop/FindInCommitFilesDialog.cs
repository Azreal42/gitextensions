using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record FileGrepSettings(string Expression = "", string UserArguments = "", bool MatchCase = true,
    bool MatchWholeWord = false, bool ShowSearchBox = true);

/// <summary>The compact original FormFindInCommitFilesGitGrep, sharing the live file-list filter.</summary>
internal sealed class FindInCommitFilesDialog : Window
{
    private readonly AutoCompleteBox _expression = new() { Name = "GrepExpression", Height = 24, MinimumPrefixLength = 0 };
    private readonly TextBox _arguments = new() { Name = "GrepOptions", Height = 24 };
    private readonly CheckBox _case = new() { Name = "GrepMatchCase", Height = 24 };
    private readonly CheckBox _word = new() { Name = "GrepMatchWholeWord", Height = 24 };
    private readonly CheckBox _show = new() { Name = "GrepShowSearchBox", Height = 28 };
    private readonly Button _find = new() { Name = "GrepFind", IsDefault = true, MinWidth = 75, Height = 25 };
    private readonly Action<FileGrepSettings> _changed;
    private readonly Func<Task> _search;
    private bool _initializing = true;
    internal bool IsSearching { get; private set; }

    internal FindInCommitFilesDialog(FileGrepSettings settings, IReadOnlyList<string> history,
        Action<FileGrepSettings> changed, Func<Task> search)
    {
        _changed = changed; _search = search;
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        string T(string key, string source) => translations.Translate("FormFindInCommitFilesGitGrep", key, "Text", source);
        AccessText Caption(string key, string source) => new() { Text = translations.TranslateAccessKeys("FormFindInCommitFilesGitGrep", key, "Text", source) };
        Name = "FindInCommitFilesGitGrep"; Title = T("$this", "Find in commit files using git-grep");
        Width = 425; MinWidth = 425; MaxWidth = 425; CanResize = false; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _expression.Watermark = translations.Translate("FormFindInCommitFilesGitGrep", "cboFindInCommitFilesGitGrep", "Watermark", "git-grep regular expression...");
        _expression.ItemsSource = history.Prepend(settings.Expression).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        _expression.SelectedItem = settings.Expression.Length == 0 ? null : settings.Expression;
        _arguments.Text = settings.UserArguments;
        _case.Content = Caption("chkMatchCase", "Match &case"); _case.IsChecked = settings.MatchCase;
        _word.Content = Caption("chkMatchWholeWord", "Match &whole word"); _word.IsChecked = settings.MatchWholeWord;
        AccessText showCaption = Caption("chkShowSearchBox", "&Show 'Find in commit files using git-grep'"); showCaption.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _show.Content = showCaption; _show.Height = double.NaN; _show.MinHeight = 28; _show.IsChecked = settings.ShowSearchBox;
        _find.Content = Caption("btnSearch", "&Find");
        Grid fields = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("24,24,24,24,Auto"), RowSpacing = 3 };
        fields.ColumnDefinitions[0].MinWidth = 78;
        Control[] labels = [Caption("label1", "Fi&nd what:"), Caption("lblOptions", "&Options:")];
        Control[] inputs = [_expression, _arguments, _case, _word];
        for (int row = 0; row < inputs.Length; row++)
        {
            if (row < labels.Length) { labels[row].Margin = new Thickness(0, 0, 6, 0); Grid.SetRow(labels[row], row); fields.Children.Add(labels[row]); }
            Grid.SetRow(inputs[row], row); Grid.SetColumn(inputs[row], 1); fields.Children.Add(inputs[row]);
        }
        Grid buttons = new() { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 3, 0, 0) };
        buttons.Children.Add(_show); Grid.SetColumn(_find, 1); buttons.Children.Add(_find);
        Grid.SetRow(buttons, 4); Grid.SetColumn(buttons, 1); fields.Children.Add(buttons); fields.Margin = new Thickness(9);
        Content = fields;
        _expression.PropertyChanged += (_, e) => { if (e.Property == AutoCompleteBox.TextProperty) { Changed(); } };
        _arguments.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { Changed(); } };
        _case.IsCheckedChanged += (_, _) => Changed(); _word.IsCheckedChanged += (_, _) => Changed(); _show.IsCheckedChanged += (_, _) => Changed();
        _find.Click += async (_, _) =>
        {
            if (IsSearching) { return; }
            Changed(); IsSearching = true; _find.IsEnabled = false;
            try { await _search(); if (IsVisible) { Close(); } }
            finally { IsSearching = false; _find.IsEnabled = true; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_expression.IsDropDownOpen) { e.Handled = true; Close(); } };
        Closing += (_, _) => Changed();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _expression.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus());
        _initializing = false;
    }

    private void Changed()
    {
        if (!_initializing) { _changed(new(_expression.Text ?? "", _arguments.Text ?? "", _case.IsChecked == true, _word.IsChecked == true, _show.IsChecked == true)); }
    }
}
