using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitInformationIncrementalGraphTests
{
    [AvaloniaTest, SetCulture("en-US"), SetUICulture("en-US")]
    public async Task AnArrivingChildUpdatesOnlyLocalLinksWhileTheFocusedExistingChildAndLoadedMetadataSurvive()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitCommit selected = (await f.Repository.GetLogAsync(1, all: false)).Single();
        await f.GitAsync("notes", "add", "-m", "Keep the loaded note", selected.Hash);
        await f.ChangeAsync("first child\n", "First child");
        GitCommit first = (await f.Repository.GetLogAsync(1, all: false)).Single();
        await f.GitAsync("checkout", "-b", "late-child", selected.Hash);
        await f.ChangeAsync("late child\n", "Later arriving child");
        GitCommit late = (await f.Repository.GetLogAsync(1, all: false)).Single();
        ObservableCollection<GitCommit> graph = [first, selected];
        ListBox rows = new() { ItemsSource = graph, SelectedItem = selected, Height = 80 };
        using CommitInformationView view = new();
        Grid layout = new() { RowDefinitions = new("Auto,*") };
        layout.Children.Add(rows); Grid.SetRow(view, 1); layout.Children.Add(view);
        Window window = new() { Content = layout, Width = 900, Height = 760 };
        window.Show();
        try
        {
            view.SetRevision(f.Repository, selected, graph.ToArray());
            await view.LoadAsync(); await view.Completion; Pump(window);
            Assert.That(view.MessageControl.Text, Does.Contain("Keep the loaded note"));
            Assert.That(view.Text, Does.Contain("Children: " + first.Hash).And.Not.Contain(late.Hash));
            PushProcessTestDriver.Pointer(view.MessageControl);
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control); Pump(window);
            Assert.That(Math.Abs(view.MessageControl.SelectionEnd - view.MessageControl.SelectionStart), Is.GreaterThan(0));
            Button existing = Child(view, first.Hash);
            PushProcessTestDriver.Pointer(existing); Pump(window);
            Assert.That(existing.IsKeyboardFocusWithin, Is.True);
            string text = view.Text, message = view.MessageControl.Text ?? "", copy = view.CopyText;
            var selection = (view.MessageControl.SelectionStart, view.MessageControl.SelectionEnd);
            long generation = Private<long>(view, "_generation"), loaded = Private<long>(view, "_loadedGeneration");
            Task read = Private<Task>(view, "_pending");
            object source = rows.ItemsSource!; object rowSelection = rows.SelectedItem!;
            int messageChanges = 0;
            view.MessageControl.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) { messageChanges++; } };
            long boundary = GitCommandLog.Snapshot().LastOrDefault()?.Id ?? 0;

            graph.Insert(0, late);
            view.UpdateGraph(f.Repository, graph.ToArray()); Pump(window);
            // A settled repeat/EOF publication must also be purely local and retain every existing link.
            Button added = Child(view, late.Hash);
            view.UpdateGraph(f.Repository, graph.ToArray()); Pump(window);

            Assert.Multiple(() =>
            {
                Assert.That(view.Text, Is.EqualTo(text.Replace("Children: " + first.Hash,
                    "Children: " + late.Hash + " " + first.Hash, StringComparison.Ordinal)));
                Assert.That(view.MessageControl.Text, Is.EqualTo(message)); Assert.That(view.CopyText, Is.EqualTo(copy));
                Assert.That(messageChanges, Is.Zero, "Graph publication must not reset the loaded message document.");
                Assert.That((view.MessageControl.SelectionStart, view.MessageControl.SelectionEnd), Is.EqualTo(selection));
                Assert.That(Private<long>(view, "_generation"), Is.EqualTo(generation));
                Assert.That(Private<long>(view, "_loadedGeneration"), Is.EqualTo(loaded));
                Assert.That(Private<Task>(view, "_pending"), Is.SameAs(read)); Assert.That(view.IsLoading, Is.False);
                Assert.That(Child(view, first.Hash), Is.SameAs(existing)); Assert.That(existing.IsKeyboardFocusWithin, Is.True);
                Assert.That(Child(view, late.Hash), Is.SameAs(added));
                Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(rows.SelectedItem, Is.SameAs(rowSelection));
                Assert.That(GitCommandLog.Snapshot().Where(entry => entry.Id > boundary), Is.Empty);
            });
        }
        finally
        {
            view.Cancel();
            try { await view.Completion; } catch (OperationCanceledException) { }
            window.Close();
        }
    }

    private static Button Child(CommitInformationView view, string hash) => view.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Tag is ValueTuple<CommitInformationCommand, string> target
            && target.Item1 == CommitInformationCommand.NavigateRevision && target.Item2 == hash);
    private static T Private<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
