using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public sealed class HistoryFilterDialog : Window
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly Dictionary<string, (CheckBox Enabled, TextBox Input)> _textFields = [];
    private readonly CheckBox _sinceEnabled = new();
    private readonly CheckBox _untilEnabled = new();
    private readonly DatePicker _since = new() { Name = "Since", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DatePicker _until = new() { Name = "Until", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NumericUpDown _limit = new() { Name = "CommitsLimit", Minimum = 1, Maximum = 100000, Increment = 100, Width = 120, FormatString = "0" };
    private readonly CheckBox _currentBranch = new() { Name = "CurrentBranchOnly" };
    private readonly CheckBox _ignoreCase = new() { Name = "IgnoreCase" };
    private readonly CheckBox _firstParent = new() { Name = "OnlyFirstParent" };
    private readonly CheckBox _hideMerges = new() { Name = "HideMergeCommits" };
    private readonly CheckBox _reflog = new() { Name = "Reflog" };
    private readonly CheckBox _decorated = new() { Name = "SimplifyByDecoration" };
    private readonly CheckBox _fullHistory = new() { Name = "FullHistory" };
    private readonly CheckBox _simplifyMerges = new() { Name = "SimplifyMerges" };

    public string RememberedBranchFilter => _textFields["BranchFilter"].Input.Text?.Trim() ?? "";
    public bool IsBranchFilterEnabled => _textFields["BranchFilter"].Enabled.IsChecked == true;

    public HistoryFilterDialog(GitLogQuery query, string? rememberedBranchFilter = null, bool? branchFilterEnabled = null)
    {
        Title = L("$this", "Filter revisions");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("100,26,*"), Margin = new Thickness(10) };
        AddDate(fields, "Since", _sinceEnabled, _since, query.Since);
        AddDate(fields, "Until", _untilEnabled, _until, query.Until);
        AddText(fields, "Author", "Author", query.Author);
        AddText(fields, "Committer", "Committer", query.Committer);
        AddText(fields, "Message", "Message", query.Message);
        AddText(fields, "DiffContent", "Diff content", query.DiffContent);
        ToolTip.SetTip(_textFields["DiffContent"].Input, _translations.Translate("Regular expression"));
        AddText(fields, "PathFilter", "Path", query.Path);
        bool currentBranch = !query.All && (query.BranchFilter ?? query.Revision) == "HEAD";
        AddText(fields, "BranchFilter", "Branch", rememberedBranchFilter ?? (currentBranch ? null : query.BranchFilter ?? query.Revision));
        _textFields["BranchFilter"].Enabled.IsChecked = branchFilterEnabled ?? (!currentBranch && (query.BranchFilter ?? query.Revision) is not null);
        _limit.Value = query.Count;
        AddField(fields, L("_NO_TRANSLATE_lblLimit", "Limit"), null, _limit);
        AddOption(fields, _currentBranch, "CurrentBranchOnlyCheck", "Current branch only", currentBranch);
        AddOption(fields, _ignoreCase, "IgnoreCase", "Ignore case", query.IgnoreCase);
        AddOption(fields, _reflog, "ReflogCheck", "Reflog", query.Reflog);
        AddOption(fields, _firstParent, "OnlyFirstParentCheck", "Only first parent", query.FirstParent);
        AddOption(fields, _hideMerges, "HideMergeCommitsCheck", "Hide merge commits", query.HideMergeCommits);
        AddOption(fields, _decorated, "SimplifyByDecorationCheck", "Simplify by decoration", query.SimplifyByDecoration);
        AddOption(fields, _fullHistory, "FullHistoryCheck", "Full history", query.FullHistory);
        AddOption(fields, _simplifyMerges, "SimplifyMergesCheck", "Simplify merges", query.SimplifyMerges);
        _currentBranch.IsCheckedChanged += (_, _) => _textFields["BranchFilter"].Input.IsEnabled = _currentBranch.IsChecked != true && _textFields["BranchFilter"].Enabled.IsChecked == true;
        _textFields["BranchFilter"].Input.IsEnabled &= _currentBranch.IsChecked != true;

        Button reset = new() { Content = _translations.Translate("Reset") };
        Button cancel = new() { Content = _translations.Translate("Cancel"), IsCancel = true };
        Button accept = new() { Content = L("Ok", "OK"), IsDefault = true };
        reset.Click += (_, _) =>
        {
            _textFields["BranchFilter"].Enabled.IsChecked = false;
            Close(new GitLogQuery { Count = query.Count, Revision = _currentBranch.IsChecked == true ? "HEAD" : null,
                All = _currentBranch.IsChecked != true, Reflog = _reflog.IsChecked == true, IgnoreCase = _ignoreCase.IsChecked == true,
                FullHistory = _fullHistory.IsChecked == true, SimplifyMerges = _simplifyMerges.IsChecked == true });
        };
        cancel.Click += (_, _) => Close(null);
        accept.Click += (_, _) => Close(ReadQuery());
        DockPanel root = new() { LastChildFill = true };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), Children = { reset, cancel, accept } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(fields);
        Content = root;
        Opened += (_, _) => _sinceEnabled.Focus();
    }

    public GitLogQuery ReadQuery() => new()
    {
        Count = (int)(_limit.Value ?? 300),
        Revision = _currentBranch.IsChecked == true ? "HEAD" : null,
        BranchFilter = _currentBranch.IsChecked == true || !IsBranchFilterEnabled ? null : RememberedBranchFilter,
        All = _currentBranch.IsChecked != true,
        Author = Text("Author"),
        Committer = Text("Committer"),
        Message = Text("Message"),
        DiffContent = Text("DiffContent"),
        Path = Text("PathFilter"),
        Since = _sinceEnabled.IsChecked == true ? _since.SelectedDate : null,
        Until = _untilEnabled.IsChecked == true ? _until.SelectedDate?.AddDays(1).AddTicks(-1) : null,
        IgnoreCase = _ignoreCase.IsChecked == true,
        FirstParent = _firstParent.IsChecked == true,
        HideMergeCommits = _hideMerges.IsChecked == true,
        Reflog = _reflog.IsChecked == true,
        SimplifyByDecoration = _decorated.IsChecked == true,
        FullHistory = _fullHistory.IsChecked == true,
        SimplifyMerges = _simplifyMerges.IsChecked == true
    };

    private string? Text(string name)
    {
        (CheckBox enabled, TextBox input) = _textFields[name];
        return enabled.IsChecked == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text : null;
    }

    private string L(string item, string source) => _translations.Translate("FormRevisionFilter", item, "Text", source);

    private void AddText(Grid grid, string name, string label, string? value)
    {
        CheckBox enabled = new() { Name = name + "Check", IsChecked = value is not null, VerticalAlignment = VerticalAlignment.Center };
        TextBox input = new() { Name = name, Text = value, IsEnabled = value is not null, Height = 24 };
        enabled.IsCheckedChanged += (_, _) => input.IsEnabled = enabled.IsChecked == true && (name != "BranchFilter" || _currentBranch.IsChecked != true);
        _textFields[name] = (enabled, input);
        AddField(grid, L("_NO_TRANSLATE_lbl" + name, label), enabled, input);
    }

    private void AddDate(Grid grid, string label, CheckBox enabled, DatePicker picker, DateTimeOffset? date)
    {
        enabled.IsChecked = date is not null;
        enabled.VerticalAlignment = VerticalAlignment.Center;
        picker.SelectedDate = date ?? DateTimeOffset.Now.Date;
        picker.IsEnabled = date is not null;
        enabled.IsCheckedChanged += (_, _) => picker.IsEnabled = enabled.IsChecked == true;
        AddField(grid, L("_NO_TRANSLATE_lbl" + label, label), enabled, picker);
    }

    private static void AddField(Grid grid, string label, Control? enabled, Control input)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        if (enabled is not null) { Grid.SetColumn(enabled, 1); Grid.SetRow(enabled, row); grid.Children.Add(enabled); }
        Grid.SetColumn(input, 2);
        Grid.SetRow(input, row);
        input.Margin = new Thickness(0, 2);
        grid.Children.Add(input);
    }

    private void AddOption(Grid grid, CheckBox check, string item, string label, bool value)
    {
        check.Content = L(item, label);
        check.IsChecked = value;
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(check, 2);
        Grid.SetRow(check, row);
        grid.Children.Add(check);
    }
}
