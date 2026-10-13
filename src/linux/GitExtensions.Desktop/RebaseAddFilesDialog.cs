using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original Add files filter, force and dry-run workflow. Arguments go directly to Git, never a shell.</summary>
internal sealed class RebaseAddFilesDialog : RepositoryDialog
{
    private readonly TextBox _filter = new() { Name = "RebaseAddFilesFilter", Text = ".", Height = 24 };
    private readonly CheckBox _force = new() { Name = "RebaseAddFilesForce" };
    private readonly TextBox _output = new() { Name = "RebaseAddFilesOutput", IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap, MinHeight = 80, IsVisible = false };

    internal RebaseAddFilesDialog(GitRepository repository) : base(repository, "Add files")
    {
        Name = "RebaseAddFilesDialog"; Width = 580; MinWidth = 540; SizeToContent = SizeToContent.Height;
        _force.Content = Caption("force", "Force"); ReferenceDialogControls.Compact(_force);
        Button show = Action("RebaseShowFiles", "Show files", () => ExecuteAsync(true)); show.Content = Caption("ShowFiles", "Show files");
        Button add = Action("RebaseAddFiles", "Add files", () => ExecuteAsync(false)); add.Content = Caption("AddFiles", "Add files"); add.IsDefault = true;
        Button cancel = new() { Name = "CancelRebaseAddFiles", Content = T("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        Grid fields = new() { ColumnDefinitions = new("70,*,Auto,Auto"), Margin = new Thickness(10, 8) };
        Label label = new() { Content = Caption("label1", "Filter"), Target = _filter, VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(label); Grid.SetColumn(_filter, 1); fields.Children.Add(_filter);
        show.Margin = new Thickness(6, 0); Grid.SetColumn(show, 2); fields.Children.Add(show); Grid.SetColumn(add, 3); fields.Children.Add(add);
        StackPanel body = new() { Spacing = 4, Children = { fields, _force, _output, ExportDialogControls.Buttons(cancel) } };
        _force.Margin = new Thickness(80, 0, 10, 0); _output.Margin = new Thickness(10, 0, 10, 0);
        Root.Children.Add(body); Track(_filter, _force);
    }

    private AccessText Caption(string item, string source)
        => new() { Text = Translations.TranslateAccessKeys("FormAddFiles", item, "Text", source) };
    public override Task InitializeAsync() => Task.CompletedTask;

    private Task ExecuteAsync(bool dryRun) => RunAsync(dryRun ? "Show files" : "Add files", async token =>
    {
        string[] paths = ParseArguments(_filter.Text ?? "");
        if (paths.Length == 0) { throw new ArgumentException(T("Enter a file filter.")); }
        List<string> arguments = ["add"];
        if (dryRun) { arguments.Add("--dry-run"); }
        if (_force.IsChecked == true) { arguments.Add("-f"); }
        arguments.AddRange(paths);
        if (!dryRun) { MarkRepositoryChanged(); }
        GitResult result = await Repository.RunAsync(arguments, token);
        _output.Text = result.StandardOutput + result.StandardError;
        _output.IsVisible = dryRun || !result.Success;
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        if (!dryRun) { Close(); }
        else { Status.Text = T("Show files"); }
    });

    // Match native process argument quoting, without interpreting shell operators or expansions.
    internal static string[] ParseArguments(string arguments)
    {
        if (arguments.Contains('\0')) { throw new ArgumentException("The file filter cannot contain NUL.", nameof(arguments)); }
        List<string> result = []; StringBuilder part = new(); bool quoted = false, started = false;
        for (int index = 0; index < arguments.Length; index++)
        {
            char value = arguments[index];
            if (value == '\\')
            {
                int end = index; while (end < arguments.Length && arguments[end] == '\\') { end++; }
                int count = end - index;
                if (end < arguments.Length && arguments[end] == '"')
                {
                    part.Append('\\', count / 2);
                    if (count % 2 != 0) { part.Append('"'); } else { quoted = !quoted; }
                    index = end; started = true; continue;
                }
                part.Append('\\', count); index = end - 1; started = true;
            }
            else if (value == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(value) && !quoted)
            { if (started) { result.Add(part.ToString()); part.Clear(); started = false; } }
            else { part.Append(value); started = true; }
        }
        if (quoted) { throw new ArgumentException("The file filter contains an unterminated quote.", nameof(arguments)); }
        if (started) { result.Add(part.ToString()); }
        return result.ToArray();
    }
}
