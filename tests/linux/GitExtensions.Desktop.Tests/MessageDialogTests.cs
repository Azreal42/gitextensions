using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class MessageDialogTests
{
    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(false, true)]
    [TestCase(true, false)] [TestCase(true, true)]
    public async Task WarningAndErrorAreOwnedThemedOneOkModalsThatReturnToTheOwner(bool warning, bool dark)
    {
        Window owner = Owner(dark); Task? completion = null;
        try
        {
            owner.Show(); completion = Dialogs.ShowMessageAsync(owner, "Literal title [one]", "Literal message <b>&[two]</b>\nsecond line", warning);
            Window dialog = await MessageAsync(owner); Button accept = Field<Button>(dialog, "MessageDialogAccept");
            Assert.Multiple(() =>
            {
                Assert.That(completion.IsCompleted, Is.False, "The caller must remain pending while the modal is visible.");
                Assert.That(dialog.Owner, Is.SameAs(owner)); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(owner.ActualThemeVariant));
                Assert.That(dialog.Title, Is.EqualTo("Literal title [one]"));
                Assert.That(Field<TextBlock>(dialog, "MessageDialogText").Text, Is.EqualTo("Literal message <b>&[two]</b>\nsecond line"));
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
                Assert.That(accept.IsDefault && accept.IsCancel, Is.True); Assert.That(accept.IsKeyboardFocusWithin, Is.True);
                Assert.That(dialog.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null), Is.EqualTo(1));
            });
            PushProcessTestDriver.Pointer(accept); await completion.WaitAsync(TimeSpan.FromSeconds(5)); Pump(owner);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { CloseChildren(owner); owner.Close(); if (completion is not null) { await ObserveCleanupAsync(completion); } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EscapeDismissesTheOnlyOkMessageAndReturnsWithoutClosingItsOwner(bool dark)
    {
        Window owner = Owner(dark); Task? completion = null;
        try
        {
            owner.Show(); completion = Dialogs.ShowMessageAsync(owner, "Notice", "Acknowledge or press Escape.", warning: true);
            Window dialog = await MessageAsync(owner); dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(owner);
            await completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(dialog.IsVisible, Is.False);
            Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { CloseChildren(owner); owner.Close(); if (completion is not null) { await ObserveCleanupAsync(completion); } }
    }

    [AvaloniaTest]
    public async Task MessageUsesTheActiveModalOwnerAndItsThemeThenReturnsToThatModal()
    {
        Window owner = Owner(false), child = Owner(true); Task? childCompletion = null, completion = null;
        try
        {
            owner.Show(); childCompletion = child.ShowDialog(owner); child.Activate(); Pump(child);
            Assert.That(child.IsActive, Is.True, "The nested child must really be active before resolving the message owner.");
            completion = Dialogs.ShowMessageAsync(owner, "Nested notice", "Message belongs to the active child.", warning: false);
            Window message = await MessageAsync(child);
            Assert.That(message.Owner, Is.SameAs(child)); Assert.That(message.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            PushProcessTestDriver.Pointer(Field<Button>(message, "MessageDialogAccept")); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(child.IsVisible, Is.True); Assert.That(childCompletion.IsCompleted, Is.False); Assert.That(owner.IsVisible, Is.True);
            child.Close(); await childCompletion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(owner.IsVisible, Is.True);
        }
        finally
        {
            CloseChildren(child); child.Close(); owner.Close();
            if (completion is not null) { await ObserveCleanupAsync(completion); } if (childCompletion is not null) { await ObserveCleanupAsync(childCompletion); }
        }
    }

    [AvaloniaTest]
    public async Task AlreadyCanceledTokenOpensNothingAndNeverPublishesCallerSuccess()
    {
        Window owner = Owner(false); using CancellationTokenSource cancel = new(); int publications = 0; Task? operation = null;
        try
        {
            owner.Show(); cancel.Cancel(); operation = RunAsync();
            await AssertCanceledAsync(operation); Pump(owner); await Task.Yield(); Pump(owner);
            Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(publications, Is.Zero); Assert.That(owner.IsVisible, Is.True);
        }
        finally { CloseChildren(owner); owner.Close(); if (operation is not null) { await ObserveCleanupAsync(operation); } }
        async Task RunAsync() { await Dialogs.ShowMessageAsync(owner, "Canceled", "Must never appear.", false, cancel.Token); publications++; }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CancellationDuringTheVisibleModalDrainsItWithoutLateSuccessOrClosingTheNextMessage(bool dark)
    {
        Window owner = Owner(dark); using CancellationTokenSource cancel = new(); int publications = 0;
        Task? operation = null, next = null;
        try
        {
            owner.Show(); operation = RunAsync(); Window canceled = await MessageAsync(owner);
            Assert.That(operation.IsCompleted, Is.False); cancel.Cancel();
            await UntilAsync(() => operation.IsCompleted && !canceled.IsVisible, owner); await AssertCanceledAsync(operation);
            Assert.That(publications, Is.Zero); Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
            next = Dialogs.ShowMessageAsync(owner, "Next notice", "The next independent message stays open.", true);
            Window fresh = await MessageAsync(owner); Assert.That(fresh, Is.Not.SameAs(canceled));
            // Drain posted callbacks and continuations from the old registration before acknowledging the next message.
            for (int frame = 0; frame < 3; frame++) { await Task.Yield(); Pump(fresh); }
            Assert.That(fresh.IsVisible, Is.True); Assert.That(next.IsCompleted, Is.False); Assert.That(publications, Is.Zero);
            PushProcessTestDriver.Pointer(Field<Button>(fresh, "MessageDialogAccept")); await next.WaitAsync(TimeSpan.FromSeconds(5)); Pump(owner);
            Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(publications, Is.Zero);
        }
        finally
        {
            CloseChildren(owner); owner.Close(); if (operation is not null) { await ObserveCleanupAsync(operation); } if (next is not null) { await ObserveCleanupAsync(next); }
        }
        async Task RunAsync() { await Dialogs.ShowMessageAsync(owner, "Pending", "Cancel while this message is visible.", true, cancel.Token); publications++; }
    }

    private static Window Owner(bool dark) => new() { Width = 640, Height = 360, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
    private static async Task<Window> MessageAsync(Window owner)
    {
        await UntilAsync(() => owner.OwnedWindows.Any(window => window.Name == "MessageDialog" && window.IsVisible), owner);
        Window dialog = owner.OwnedWindows.Single(window => window.Name == "MessageDialog" && window.IsVisible); Pump(dialog); return dialog;
    }
    private static T Field<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task UntilAsync(Func<bool> condition, TopLevel root)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Pump(root); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The message transition must finish within five seconds.");
    }
    private static async Task AssertCanceledAsync(Task operation)
    {
        bool canceled = false;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
        Assert.That(canceled, Is.True, "Cancellation must reach the caller instead of reporting acknowledgement.");
    }
    private static void CloseChildren(Window owner)
    { foreach (Window child in owner.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } Pump(owner); }
    private static async Task ObserveCleanupAsync(Task operation)
    { try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
}
