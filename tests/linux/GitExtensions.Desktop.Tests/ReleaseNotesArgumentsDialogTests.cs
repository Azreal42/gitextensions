using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ReleaseNotesArgumentsDialogTests
{
    private const string DefaultArguments = "--pretty=\"format:%h@%s%b\" --abbrev-commit {0}..{1}";

    [AvaloniaTest]
    public async Task SharedPluginDialogExposesWindowsArgumentsAndRunsRealNoMergeFilterInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync("anchor\n", "anchor\n", createMerge: true);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            PluginDialog dialog = new(new GitRepository(f.Repository), new ReleaseNotesGeneratorPlugin()) { RequestedThemeVariant = theme };
            try
            {
                dialog.Show(f.Window); dialog.UpdateLayout();
                TextBox arguments = Field<TextBox>(dialog, "arguments"), result = Field<TextBox>(dialog, "PluginText");
                Assert.Multiple(() =>
                {
                    Assert.That(arguments.Text, Is.EqualTo(DefaultArguments), "The exact editable Windows default must reach the actual common field.");
                    Assert.That(arguments.IsReadOnly, Is.False);
                    Assert.That(result.IsReadOnly, Is.True);
                    Assert.That(arguments.IsEffectivelyVisible && arguments.Bounds.Width > 0 && arguments.Bounds.Height > 0, Is.True);
                });
                Field<TextBox>(dialog, "fromRevision").Text = "v1.0";
                await RunAsync(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That(dialog.LastResult!.Table!.Rows.Count, Is.EqualTo(3), "The default includes the real merge.");
                arguments.Text = DefaultArguments + " --no-merges";
                foreach (string format in new[] { "text", "tsv", "html" })
                {
                    Field<ComboBox>(dialog, "format").SelectedItem = format;
                    await RunAsync(dialog);
                    Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                    Assert.That(dialog.LastResult is { Success: true, RepositoryChanged: false }, Is.True);
                    Assert.That(dialog.LastResult!.Table!.Rows.Count, Is.EqualTo(2));
                    Assert.That(Field<ListBox>(dialog, "PluginRows").Items.Cast<PluginRow>().Count(), Is.EqualTo(2));
                    Assert.That(result.Text, Does.Contain("Feature branch change"));
                    if (format == "html") { Assert.That(result.Text, Does.Contain("<table>")); }
                    else if (format == "tsv") { Assert.That(result.Text, Does.Contain("\t")); }
                    Assert.That(result.IsReadOnly, Is.True);
                }
                Assert.That(dialog.HasRepositoryChanges, Is.False);
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task SharedDialogRetainsPreviousActualNotesAndTableWhenFormatQuotesOrGitFail()
    {
        using Fixture f = await Fixture.CreateAsync("anchor\n", "anchor\n", createMerge: true);
        PluginDialog dialog = new(new GitRepository(f.Repository), new ReleaseNotesGeneratorPlugin());
        try
        {
            dialog.Show(f.Window); dialog.UpdateLayout(); Field<TextBox>(dialog, "fromRevision").Text = "v1.0";
            await RunAsync(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            PluginResult previous = dialog.LastResult!; string? text = Field<TextBox>(dialog, "PluginText").Text;
            string sentinel = Path.Combine(Path.GetDirectoryName(f.Repository)!, "release-output-must-not-exist.txt");
            foreach ((string template, Type error, string diagnostic) in new[]
            {
                (DefaultArguments + " {2}", typeof(FormatException), "Git log arguments format"),
                (DefaultArguments + " 'unclosed", typeof(ArgumentException), "Git log arguments quoting"),
                (DefaultArguments + " --not-a-release-notes-option", typeof(GitCommandException), ""),
                (DefaultArguments + " --output=\"" + sentinel + "\"", typeof(ArgumentException), "--output")
            })
            {
                Field<TextBox>(dialog, "arguments").Text = template; await RunAsync(dialog);
                Assert.Multiple(() =>
                {
                    Assert.That(dialog.LastResult, Is.SameAs(previous), "A failed read must preserve the previous actual generated result.");
                    Assert.That(Field<TextBox>(dialog, "PluginText").Text, Is.EqualTo(text));
                    Assert.That(Field<ListBox>(dialog, "PluginRows").Items.Cast<PluginRow>(), Is.EqualTo(previous.Table!.Rows));
                    Assert.That(dialog.LastException, Is.InstanceOf(error));
                    Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty);
                    Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.EqualTo(dialog.LastError).And.Not.EqualTo("Completed"));
                    Assert.That(dialog.HasRepositoryChanges, Is.False);
                });
                if (diagnostic.Length > 0) { Assert.That(dialog.LastError, Does.Contain(diagnostic)); }
            }
            Assert.That(File.Exists(sentinel), Is.False);
            Field<TextBox>(dialog, "arguments").Text = DefaultArguments + " --no-merges"; await RunAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastResult!.Table!.Rows.Count, Is.EqualTo(2), "Correcting the field must allow a new real successful read.");
        }
        finally { dialog.Close(); }
    }
    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static async Task RunAsync(PluginDialog dialog)
    {
        Field<Button>(dialog, "RunPlugin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (dialog.IsBusy) { await Task.Delay(10, timeout.Token); }
        dialog.UpdateLayout();
    }
}
