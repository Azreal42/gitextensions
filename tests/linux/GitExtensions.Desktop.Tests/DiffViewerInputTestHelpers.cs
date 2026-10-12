using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal static class DiffViewerInputTestHelpers
{
    internal static void RevealToolbar(Window owner, Control control)
    {
        if (control.GetVisualAncestors().OfType<DiffViewerToolbar>().FirstOrDefault() is not { } toolbar) { return; }
        DiffView view = toolbar.GetVisualAncestors().OfType<DiffView>().Single();
        owner.MouseMove(view.Editor.TranslatePoint(new Point(80, 80), owner)!.Value);
        Dispatcher.UIThread.RunJobs(); owner.UpdateLayout();
        Assert.That(toolbar.IsEffectivelyVisible && control.IsEffectivelyVisible, Is.True, "The actual mouse movement must reveal the viewer command.");
    }

    internal static ComboBox VisibleEncoding(Window owner)
    {
        ComboBox selector = owner.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DiffViewerEncoding"
            && control.GetVisualAncestors().OfType<DiffView>().Single().IsEffectivelyVisible);
        RevealToolbar(owner, selector);
        return selector;
    }
}
