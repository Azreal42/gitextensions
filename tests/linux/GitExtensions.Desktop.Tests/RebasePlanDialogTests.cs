using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class RebasePlanDialogTests
{
    [AvaloniaTest]
    public async Task UserCanValidateRewordReorderAndDropCommitsOrCancelWithoutChangingTheOriginalPlan()
    {
        GitRebasePlan plan = new(new string('0', 40), new string('f', 40),
        [
            new(GitRebaseAction.Pick, new string('a', 40), "Add repository browser"),
            new(GitRebaseAction.Pick, new string('b', 40), "Handle literal filenames"),
            new(GitRebaseAction.Pick, new string('c', 40), "Package the Linux desktop"),
            new(GitRebaseAction.Pick, new string('d', 40), "Update translated labels"),
        ]);
        Window owner = new() { Width = 900, Height = 600 };
        try
        {
            owner.Show();
            Task<GitRebasePlan?> result = RebasePlanDialog.ShowAsync(owner, plan);
            Window dialog = owner.OwnedWindows.Single();
            dialog.UpdateLayout();
            ListBox list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            ListBoxItem[] rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.That(rows.Length, Is.EqualTo(4));
            Assert.That(rows.Select(row => row.Bounds.Height), Is.All.EqualTo(24));
            ComboBox[] actions = rows.Select(row => row.GetVisualDescendants().OfType<ComboBox>().Single()).ToArray();
            Button rebase = FindButton(dialog, "Rebase");

            actions[0].SelectedItem = GitRebaseAction.Squash;
            Assert.That(rebase.IsEnabled, Is.False, "Squash cannot be the first retained commit.");
            actions[0].SelectedItem = GitRebaseAction.Pick;
            actions[1].SelectedItem = GitRebaseAction.Reword;
            TextBox message = dialog.GetVisualDescendants().OfType<TextBox>().Single(text => text.AcceptsReturn);
            Assert.That(message.IsEnabled, Is.True);
            Assert.That(message.Text, Is.EqualTo(plan.Steps[1].Subject));
            message.Text = "";
            Assert.That(rebase.IsEnabled, Is.False, "Reword requires a nonempty message.");
            message.Text = "Handle Linux filenames\n\nPreserve shell metacharacters literally.";
            FindButton(dialog, "↓ Move down").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(list.SelectedIndex, Is.EqualTo(2));
            actions[3].SelectedItem = GitRebaseAction.Drop;
            Assert.That(rebase.IsEnabled, Is.True);

            using Avalonia.Media.Imaging.WriteableBitmap? frame = dialog.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
            string screenshot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "rebase-plan.png");
            frame!.Save(screenshot);
            TestContext.AddTestAttachment(screenshot, "Compact rebase editor with reword, reorder and drop");
            rebase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            GitRebasePlan? edited = await result;
            Assert.That(edited, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(edited!.Steps.Select(step => step.Hash), Is.EqualTo(new[] { plan.Steps[0].Hash, plan.Steps[2].Hash, plan.Steps[1].Hash, plan.Steps[3].Hash }));
                Assert.That(edited.Steps[2].Action, Is.EqualTo(GitRebaseAction.Reword));
                Assert.That(edited.Steps[2].Message, Is.EqualTo("Handle Linux filenames\n\nPreserve shell metacharacters literally."));
                Assert.That(edited.Steps[3].Action, Is.EqualTo(GitRebaseAction.Drop));
                Assert.That(plan.Steps.All(step => step.Action == GitRebaseAction.Pick && step.Message is null), Is.True);
            });

            Task<GitRebasePlan?> canceled = RebasePlanDialog.ShowAsync(owner, plan);
            Window canceledDialog = owner.OwnedWindows.Single();
            canceledDialog.UpdateLayout();
            canceledDialog.GetVisualDescendants().OfType<ComboBox>().First().SelectedItem = GitRebaseAction.Drop;
            FindButton(canceledDialog, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(await canceled, Is.Null);
            Assert.That(plan.Steps.All(step => step.Action == GitRebaseAction.Pick), Is.True);
        }
        finally { owner.Close(); }
    }

    private static Button FindButton(Window window, string label)
        => window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == label);
}
