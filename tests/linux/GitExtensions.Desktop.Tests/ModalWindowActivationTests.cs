using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform"), NonParallelizable]
public sealed class ModalWindowActivationTests
{
    [AvaloniaTest]
    [TestCase(WindowState.Normal)]
    [TestCase(WindowState.Maximized)]
    public async Task MinimizingOnlyTheModalRestoresItWithoutEndingTheWorkflow(WindowState previousState)
    {
        Window owner = new(); Window modal = new() { WindowState = previousState };
        owner.Show(); Task closed = modal.ShowDialog(owner);
        try
        {
            Assert.That(modal.CanMinimize, Is.False, "App startup must protect every ShowDialog without a per-dialog call.");
            modal.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(previousState));
            Assert.That(modal.IsVisible, Is.True);
            Assert.That(closed.IsCompleted, Is.False);
        }
        finally { modal.Close(); owner.Close(); }
        await closed;
    }

    [AvaloniaTest]
    public async Task MinimizingTheWholeApplicationIsAllowedAndRestoringTheOwnerRestoresTheModal()
    {
        Window owner = new(); Window modal = new();
        owner.Show(); Task closed = modal.ShowDialog(owner);
        try
        {
            owner.WindowState = WindowState.Minimized;
            modal.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Minimized));
            owner.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(closed.IsCompleted, Is.False);
        }
        finally { modal.Close(); owner.Close(); }
        await closed;
    }

    [AvaloniaTest]
    public async Task ClosingBeforeTheQueuedRestorationDoesNotReopenTheModal()
    {
        Window owner = new(); Window modal = new();
        owner.Show(); Task closed = modal.ShowDialog(owner);
        try
        {
            modal.WindowState = WindowState.Minimized;
            modal.Close();
            owner.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.IsVisible, Is.False);
            Assert.That(closed.IsCompleted, Is.True);
            Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { modal.Close(); owner.Close(); }
        await closed;
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void ModelessWindowsKeepTheirMinimizeBehavior(bool owned)
    {
        Window owner = new(); Window window = new(); owner.Show();
        try
        {
            if (owned) { window.Show(owner); } else { window.Show(); }
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.IsDialog, Is.False);
            Assert.That(window.CanMinimize, Is.True);
            Assert.That(window.WindowState, Is.EqualTo(WindowState.Minimized));
        }
        finally { window.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task AModalThatStartsMinimizedIsRestoredAndKeepsItsResult()
    {
        Window owner = new(); Window modal = new() { WindowState = WindowState.Minimized };
        owner.Show(); Task<int> closed = modal.ShowDialog<int>(owner);
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(closed.IsCompleted, Is.False);
            modal.Close(42);
            Assert.That(await closed, Is.EqualTo(42));
        }
        finally { modal.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task NestedModalsWaitForEveryAncestorToBeRestored()
    {
        Window root = new(); Window parent = new(); Window child = new(); root.Show();
        Task parentClosed = parent.ShowDialog(root); Task childClosed = child.ShowDialog(parent);
        try
        {
            root.WindowState = WindowState.Minimized;
            child.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(child.WindowState, Is.EqualTo(WindowState.Minimized), "A normal immediate owner must not restore a minimized application.");
            root.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.That(child.WindowState, Is.EqualTo(WindowState.Normal));
            parent.WindowState = child.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(parent.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(child.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(parentClosed.IsCompleted || childClosed.IsCompleted, Is.False);
            Assert.That(child.IsActive, Is.True, "Recovery must activate the deepest modal, which accepts input.");
        }
        finally { child.Close(); parent.Close(); root.Close(); }
        await Task.WhenAll(parentClosed, childClosed);
    }

    [AvaloniaTest]
    public async Task HidingAndReopeningModelessDoesNotRunTheOldModalRestoration()
    {
        Window owner = new(); Window secondOwner = new(); Window modal = new(); owner.Show(); secondOwner.Show();
        Task closed = modal.ShowDialog(owner);
        try
        {
            modal.WindowState = WindowState.Minimized;
            modal.Hide();
            Assert.That(closed.IsCompleted, Is.True);
            modal.Show(secondOwner);
            modal.WindowState = WindowState.Minimized;
            owner.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.IsDialog, Is.False);
            Assert.That(modal.CanMinimize, Is.True);
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Minimized));
        }
        finally { modal.Close(); secondOwner.Close(); owner.Close(); }
        await closed;
    }

    [AvaloniaTest]
    public async Task ReopeningWithAnotherOwnerUsesOnlyTheCurrentOwnerChain()
    {
        Window owner = new(); Window secondOwner = new(); Window modal = new(); owner.Show(); secondOwner.Show();
        Task first = modal.ShowDialog(owner); Task? second = null;
        try
        {
            modal.WindowState = WindowState.Minimized;
            modal.Hide();
            second = modal.ShowDialog(secondOwner);
            secondOwner.WindowState = modal.WindowState = WindowState.Minimized;
            owner.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Minimized));
            secondOwner.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(second.IsCompleted, Is.False);
        }
        finally { modal.Close(); secondOwner.Close(); owner.Close(); }
        await first;
        if (second is not null) { await second; }
    }

    [AvaloniaTest]
    public async Task CancellingCloseKeepsTheModalProtected()
    {
        Window owner = new(); Window modal = new(); owner.Show(); Task closed = modal.ShowDialog(owner);
        bool cancel = true; modal.Closing += (_, args) => args.Cancel = cancel;
        try
        {
            modal.WindowState = WindowState.Minimized;
            modal.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.That(modal.IsVisible, Is.True);
            Assert.That(modal.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(closed.IsCompleted, Is.False);
        }
        finally { cancel = false; modal.Close(); owner.Close(); }
        await closed;
    }
}
