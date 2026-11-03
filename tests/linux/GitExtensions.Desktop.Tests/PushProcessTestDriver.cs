using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

/// <summary>Real acknowledgments only; never acknowledges a process inside its completion/recovery hook.</summary>
internal static class PushProcessTestDriver
{
    internal static IEnumerable<Window> Windows(Window root)
    {
        foreach (Window child in root.OwnedWindows.ToArray())
        {
            yield return child;
            foreach (Window descendant in Windows(child)) { yield return descendant; }
        }
    }

    internal static async Task CompleteAsync(Task task, Window root)
    {
        await UntilAsync(root, () => task.IsCompleted);
        await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    internal static async Task UntilAsync(Window root, Func<bool> condition, string? message = null, Func<Task<string>>? diagnostic = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            Dispatcher.UIThread.RunJobs();
            GitProcessDialog? phase = Windows(root).OfType<GitProcessDialog>().FirstOrDefault(window => window.IsVisible
                && !window.IsRunning && window.OwnedWindows.Count == 0 && window.Outcome?.WorkflowStopRequested != true);
            if (phase is not null) { Pointer(Field<Button>(phase, "Ok")); }
            await Task.Delay(10);
        }
        string details = condition() || diagnostic is null ? "" : await diagnostic();
        Assert.That(condition(), Is.True, message + "\n" + details + "\n" + string.Join("\n", Windows(root).Select(window =>
            window.Name + " visible=" + window.IsVisible + (window is GitProcessDialog process
                ? " running=" + process.IsRunning + " error=" + process.Error + " stop=" + process.WorkflowStopRequested : ""))));
    }

    internal static T Field<T>(TopLevel root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    internal static void Pointer(Control control) => Pointer(control, MouseButton.Left);

    internal static void Pointer(Control control, MouseButton button,
        RawInputModifiers modifiers = RawInputModifiers.None, Point? localPoint = null)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!;
        // Rendering publishes hit-test data through more dispatcher jobs. Settle
        // those frames before the strict check, as the headless input APIs do.
        for (int frame = 0; frame < 10; frame++)
        {
            Dispatcher.UIThread.RunJobs(); top.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
        Point? translated = control.TranslatePoint(localPoint ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        Visual? hit = translated is Point target ? top.InputHitTest(target) as Visual : null;
        bool ready = translated is Point center && control.IsEffectivelyVisible && control.IsEffectivelyEnabled
            && new Rect(default, top.ClientSize).Contains(center) && hit?.GetSelfAndVisualAncestors().Contains(control) == true;
        string details = ready ? "" : CapturePointerFailure(control, top, translated, hit);
        Assert.That(ready, Is.True, "Real pointer target: " + control.Name + "\n" + details);
        Point point = translated!.Value;
        top.MouseMove(point); top.MouseDown(point, button, modifiers); top.MouseUp(point, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static string CapturePointerFailure(Control control, TopLevel top, Point? point, Visual? hit)
    {
        static string Identity(Visual? visual) => visual is null ? "<null>" : visual.GetType().Name + "/" + (visual as Control)?.Name;
        StringBuilder detail = new();
        detail.AppendLine($"Control={Identity(control)} visible={control.IsVisible}/{control.IsEffectivelyVisible} enabled={control.IsEnabled}/{control.IsEffectivelyEnabled}");
        detail.AppendLine($"controlBounds={control.Bounds} translatedCenter={point} client={top.ClientSize} topBounds={top.Bounds} renderScaling={top.RenderScaling}");
        detail.AppendLine("hit=" + Identity(hit) + "; ancestors=" + string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(Identity) ?? []));
        if (top is Window window)
        {
            for (Window? current = window; current is not null; current = current.Owner as Window)
            {
                detail.AppendLine($"Window={Identity(current)} title={current.Title} Owner={Identity(current.Owner)} visible={current.IsVisible}/{current.IsEffectivelyVisible} enabled={current.IsEnabled}/{current.IsEffectivelyEnabled} active={current.IsActive}");
                detail.AppendLine($"  ClientSize={current.ClientSize} Bounds={current.Bounds} Position={current.Position} Width={current.Width} Height={current.Height} SizeToContent={current.SizeToContent} ClosingBehavior={current.ClosingBehavior}");
                if (current is GitProcessDialog process) { detail.AppendLine($"  running={process.IsRunning} workflowStop={process.WorkflowStopRequested} result={process.Result?.ExitCode} error={process.Error}"); }
            }
        }
        foreach (Button choice in top.GetVisualDescendants().OfType<Button>().Where(button => button.Name?.StartsWith("PushChoice", StringComparison.Ordinal) == true))
        {
            Point? origin = choice.TranslatePoint(default, top);
            detail.AppendLine($"Choice={choice.Name} bounds={choice.Bounds} origin={origin} center={choice.TranslatePoint(new Point(choice.Bounds.Width / 2, choice.Bounds.Height / 2), top)} visible={choice.IsVisible}/{choice.IsEffectivelyVisible} enabled={choice.IsEnabled}/{choice.IsEffectivelyEnabled}");
        }
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        string stem = "push-pointer-failure-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            using var frame = top.CaptureRenderedFrame();
            if (frame is not null)
            {
                string png = Path.Combine(directory, stem + ".png"); frame.Save(png);
                TestContext.AddTestAttachment(png); detail.AppendLine("Raw frame=" + png);
            }
            else { detail.AppendLine("Raw frame unavailable: CaptureRenderedFrame returned null."); }
            string log = Path.Combine(directory, stem + ".log"); File.WriteAllText(log, detail.ToString()); TestContext.AddTestAttachment(log);
        }
        catch (Exception error) { detail.AppendLine("Diagnostic capture failed: " + error); }
        TestContext.Progress.WriteLine(detail.ToString());
        return detail.ToString();
    }
}
