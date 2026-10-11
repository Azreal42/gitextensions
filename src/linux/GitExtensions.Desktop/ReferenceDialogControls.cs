using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

internal static class ReferenceDialogControls
{
    internal static Grid Fields() => new() { ColumnDefinitions = new ColumnDefinitions("150,*,Auto") };
    internal static void Field(Grid grid, string caption, Control input, Control? browse = null)
        => ExportDialogControls.Field(grid, new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap }, input, browse);
    internal static void Field(Grid grid, Control caption, Control input, Control? browse = null)
        => ExportDialogControls.Field(grid, caption, input, browse);
    internal static TextBox Text(string name, string? value = null) => new() { Name = name, Text = value, Height = 24 };
    internal static ComboBox Choice(string name) => new() { Name = name, Height = 24, Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal static void Compact(params ToggleButton[] choices)
    {
        foreach (ToggleButton choice in choices) { choice.Height = choice.MinHeight = 24; choice.Padding = new Thickness(0); }
    }
    internal static void ReferenceTemplate(ComboBox choice)
        => choice.ItemTemplate = new FuncDataTemplate<GitReference>((reference, _) => new TextBlock { Text = reference?.ShortName ?? "", TextTrimming = TextTrimming.CharacterEllipsis });

    internal static async Task<string> SummaryAsync(GitRepository repository, string revision, CancellationToken token)
    {
        GitCommit commit = (await repository.GetLogAsync(1, revision, token)).Single();
        return commit.Subject + "\n" + commit.AuthorName + " <" + commit.AuthorEmail + ">    " + commit.AuthoredAt.LocalDateTime.ToString("g") + "\n" + commit.Hash;
    }
}
