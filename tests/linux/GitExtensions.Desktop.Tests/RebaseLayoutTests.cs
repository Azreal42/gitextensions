using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.RebaseDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RebaseLayoutTests
{
    [AvaloniaTest] public Task LongRealConflictOutputKeepsPopulatedGridAndRecoveryRegionsSeparateLight() => HealthyConflictLayout(false);
    [AvaloniaTest] public Task LongRealConflictOutputKeepsPopulatedGridAndRecoveryRegionsSeparateDark() => HealthyConflictLayout(true);
    [AvaloniaTest] public Task PreservedJournalTooltipRendersItsActualTextAndBackgroundLight() => PreservedJournalTooltip(false);
    [AvaloniaTest] public Task PreservedJournalTooltipRendersItsActualTextAndBackgroundDark() => PreservedJournalTooltip(true);

    private static async Task HealthyConflictLayout(bool dark)
    {
        using OptionalLocksDisabled locks = new();
        const string conflictPath = "conflict café.txt";
        using RebaseUiFixture f = await RebaseUiFixture.Create(conflict: true, conflictPath: conflictPath);
        string hook = f.File(".git/hooks/pre-rebase");
        System.IO.File.WriteAllText(hook, "#!/bin/sh\ni=0\nwhile [ \"$i\" -lt 40 ]; do\n  printf 'Real pre-rebase diagnostic line %s: café and conflict context\\n' \"$i\" >&2\n  i=$((i + 1))\ndone\nprintf '%s\\n' 'LAST HOOK DIAGNOSTIC: complete output stays accessible' >&2\nexit 0\n", new UTF8Encoding(false, true));
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        RebaseDialog dialog = await f.Show(dark);
        Click(Field<Button>(dialog, "ExecuteRebase"));
        await Until(() => !dialog.IsBusy && dialog.State?.IsActive == true && dialog.LastError is not null);
        Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>(), "The long output must come from a real stopped Git rebase.");
        Assert.That(dialog.LastError, Does.Contain("Real pre-rebase diagnostic line 0").And.Contain("Real pre-rebase diagnostic line 39").And.Contain("LAST HOOK DIAGNOSTIC"));
        Assert.That(dialog.State!.JournalError, Is.Null);
        Assert.That(dialog.State.EncodingError, Is.Null);
        Assert.That(dialog.State.InstructionError, Is.Null);
        Assert.That(dialog.State.Steps.Count, Is.EqualTo(2));
        Assert.That(dialog.State.HasConflicts, Is.True);
        Assert.That(dialog.State.Conflicts.Select(conflict => conflict.Path), Does.Contain(conflictPath));
        string before = await f.Witness();
        byte[] indexBefore = System.IO.File.ReadAllBytes(f.File(".git/index"));
        byte[] journalBefore = System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json"));

        foreach (Size size in new[] { new Size(1050, 500), new Size(900, 460) })
        {
            dialog.Width = size.Width; dialog.Height = size.Height; Pump(dialog);
            Assert.That(dialog.ClientSize, Is.EqualTo(size));
            TextBlock label = Field<TextBlock>(dialog, "RebaseCommitsLabel");
            Border header = Field<Border>(dialog, "RebasePatchHeader");
            ListBox patches = Field<ListBox>(dialog, "RebasePatches");
            ScrollViewer recovery = Field<ScrollViewer>(dialog, "RebaseRecoveryContext");
            TextBlock notice = Field<TextBlock>(dialog, "RebaseConflictNotice");
            ListBox conflicts = Field<ListBox>(dialog, "RebaseConflictFiles");
            WrapPanel secondary = Field<WrapPanel>(dialog, "RebaseSecondaryActions");
            StackPanel primary = Field<StackPanel>(dialog, "RebasePrimaryActions");
            ScrollViewer output = Field<ScrollViewer>(dialog, "RebaseCommandOutput");
            TextBlock fullOutput = Field<TextBlock>(dialog, "OperationStatus");
            AssertAbove(label, header, dialog);
            AssertAbove(header, patches, dialog);
            AssertAbove(patches, recovery, dialog);
            AssertAbove(recovery, secondary, dialog);
            AssertAbove(secondary, primary, dialog);
            AssertAbove(primary, output, dialog);
            foreach (Control region in new Control[] { label, header, patches, recovery, secondary, primary, output })
            { AssertInside(region, dialog); }
            Assert.That(patches.Bounds.Height, Is.GreaterThanOrEqualTo(26), "At least one full commit row must remain visible at the supported minimum.");
            ListBoxItem current = (ListBoxItem)patches.ContainerFromIndex(0)!;
            Assert.That(current, Is.Not.Null);
            Assert.That(current.DataContext, Is.EqualTo(dialog.State.Steps[0]));
            Assert.That(Ready(current), Is.True, "The populated grid must be hit-testable without painting over its header or recovery context.");
            Assert.That(notice.IsEffectivelyVisible, Is.True);
            Assert.That(BoundsIn(recovery, dialog).Contains(BoundsIn(notice, dialog)), Is.True, "The real conflict notice must be visible within its viewport.");
            AssertAbove(notice, conflicts, dialog);
            AssertAbove(conflicts, secondary, dialog);
            Assert.That(conflicts.Items.Cast<string>(), Is.EqualTo(new[] { conflictPath }));
            ListBoxItem file = (ListBoxItem)conflicts.ContainerFromIndex(0)!;
            Assert.That(file, Is.Not.Null);
            Assert.That(BoundsIn(recovery, dialog).Contains(BoundsIn(file, dialog)), Is.True, "The entire real conflict filename must remain visible.");
            Assert.That(Ready(file), Is.True);
            foreach (string action in new[] { "AddRebaseFiles", "EditRebaseTodo", "SkipRebase", "ResolveRebaseConflicts", "AbortRebase", "CancelRebase" })
            { Assert.That(Ready(Field<Button>(dialog, action)), Is.True, action); }
            Assert.That(fullOutput.Text, Is.EqualTo(dialog.LastError), "Bounding the output must not truncate the command's diagnostics.");
            Assert.That(output.Bounds.Height, Is.LessThanOrEqualTo(60));
            Assert.That(output.Extent.Height, Is.GreaterThan(output.Viewport.Height));
            output.ScrollToEnd(); Pump(dialog);
            Assert.That(output.Offset.Y, Is.GreaterThan(0));
            Assert.That(BoundsIn(fullOutput, dialog).Bottom, Is.InRange(BoundsIn(output, dialog).Top, BoundsIn(output, dialog).Bottom + 1), "The last diagnostic line must be reachable by scrolling.");
            ScrollViewer help = Field<ScrollViewer>(dialog, "RebaseHelpViewport");
            Image original = Field<Image>(dialog, "OriginalCommandHelpImage");
            Assert.That(original.Bounds.Size, Is.EqualTo(new Size(289, 373)), "The original Windows help diagram keeps its native dimensions.");
            AssertAbove(help, output, dialog);
            help.ScrollToEnd(); Pump(dialog);
            Assert.That(BoundsIn(original, dialog).Bottom, Is.LessThanOrEqualTo(BoundsIn(help, dialog).Bottom + 1), "The bottom of the original help remains accessible at minimum height.");
            Capture(dialog, "rebase-long-conflict-layout-" + (dark ? "dark" : "light") + "-" + (int)size.Height + ".png");
        }
        Assert.That(await f.Witness(), Is.EqualTo(before), "Resizing and scrolling recovery UI must preserve Git/index/worktree/todo bytes.");
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(indexBefore));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/gitextensions-rebase-native/state.json")), Is.EqualTo(journalBefore));
    }

    private static async Task PreservedJournalTooltip(bool dark)
    {
        using OptionalLocksDisabled locks = new();
        using RebaseUiFixture f = await RebaseUiFixture.Create();
        string journal = f.File(".git/gitextensions-rebase-native"); Directory.CreateDirectory(journal);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(journal, "state.json"), [0x7b, 0xff, 0x00]);
        RebaseDialog dialog = await f.Show(dark);
        Click(Field<Button>(dialog, "PreserveRebaseJournal"));
        await Until(() => !dialog.IsBusy && dialog.State?.HasRecoverableJournal == false);
        Assert.That(dialog.LastError, Is.Null);
        TextBlock notice = Field<TextBlock>(dialog, "RebaseJournalNotice");
        string originalTip = (string)ToolTip.GetTip(notice)!;
        string preserved = Directory.GetDirectories(f.File(".git"), "gitextensions-rebase-native-preserved-*").Single();
        Assert.That(originalTip, Does.Contain(preserved));
        string before = await f.Witness();
        byte[] indexBefore = System.IO.File.ReadAllBytes(f.File(".git/index"));
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        Color expectedBackground = Color.Parse(colors["Info"]), expectedText = Color.Parse(colors["InfoText"]);
        foreach (string text in new[] { "Journal preserved", originalTip })
        {
            // A string content still uses the real Fluent-generated TextBlock, not a hand-styled replacement.
            ToolTip tip = new() { Content = text };
            ToolTip.SetTip(notice, tip);
            try
            {
                ToolTip.SetIsOpen(notice, true);
                await Until(() => TopLevel.GetTopLevel(tip) is not null && tip.IsEffectivelyVisible);
                // Headless has no native IPopupImpl: the open tip is hosted in the owner's overlay layer.
                OverlayPopupHost overlay = tip.GetVisualAncestors().OfType<OverlayPopupHost>().Single();
                Assert.That(TopLevel.GetTopLevel(tip), Is.SameAs(dialog));
                Assert.That(overlay.Content, Is.SameAs(tip), "The actual opened overlay must contain this string-content tooltip.");
                Assert.That(overlay.GetVisualParent(), Is.TypeOf<OverlayLayer>());
                await Task.Delay(200); Pump(dialog);
                Assert.That(ToolTip.GetIsOpen(notice), Is.True);
                AssertInside(overlay, dialog);
                Assert.That(tip.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
                Border background = tip.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
                ContentPresenter presenter = tip.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
                TextBlock renderedText = presenter.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.That(renderedText.Text, Is.EqualTo(text));
                Assert.That(ColorOf(background.Background), Is.EqualTo(expectedBackground));
                Assert.That(ColorOf(presenter.Foreground), Is.EqualTo(expectedText));
                Assert.That(ColorOf(renderedText.Foreground), Is.EqualTo(expectedText), "The actual glyph foreground must match InfoText, including a long string wrapped by Fluent.");
                Assert.That(Contrast(ColorOf(renderedText.Foreground), ColorOf(background.Background)), Is.GreaterThan(4), "Retain the original Info/InfoText palette's readable contrast.");
                Rect backgroundBounds = BoundsIn(background, dialog), textBounds = BoundsIn(renderedText, dialog);
                AssertInside(background, dialog); AssertInside(renderedText, dialog);
                Assert.That(BoundsIn(overlay, dialog).Contains(backgroundBounds), Is.True);
                Assert.That(backgroundBounds.Contains(textBounds), Is.True);
                using WriteableBitmap? frame = dialog.CaptureRenderedFrame();
                Assert.That(frame, Is.Not.Null);
                Assert.That(CountPixels(frame!, backgroundBounds, expectedBackground, dialog.RenderScaling), Is.GreaterThan(100), "Only the actual overlay background's rectangle may prove rendered Info pixels.");
                Assert.That(CountPixels(frame!, textBounds, expectedText, dialog.RenderScaling), Is.GreaterThan(0), "Only the actual generated TextBlock's rectangle may prove rendered InfoText glyphs.");
                string name = "rebase-journal-tooltip-" + (dark ? "dark" : "light") + "-" + (text == originalTip ? "long" : "simple") + ".png";
                string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
                Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, name);
                frame!.Save(path); TestContext.AddTestAttachment(path, "Opened Headless OverlayPopupHost; background ROI " + backgroundBounds + "; text ROI " + textBounds);
            }
            finally { ToolTip.SetIsOpen(notice, false); ToolTip.SetTip(notice, originalTip); }
        }
        Assert.That(await f.Witness(), Is.EqualTo(before), "Opening tooltips must preserve the completed journal recovery's Git preimage.");
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(indexBefore));
        Assert.That(System.IO.File.ReadAllBytes(System.IO.Path.Combine(preserved, "state.json")), Is.EqualTo(new byte[] { 0x7b, 0xff, 0x00 }));
    }

    private static Rect BoundsIn(Control control, Visual host) => new(control.TranslatePoint(default, host)!.Value, control.Bounds.Size);
    private static void AssertAbove(Control above, Control below, Window dialog)
        => Assert.That(BoundsIn(above, dialog).Bottom, Is.LessThanOrEqualTo(BoundsIn(below, dialog).Top + 0.5), above.Name + " must not overlap " + below.Name);
    private static void AssertInside(Control control, Window dialog)
    {
        Rect bounds = BoundsIn(control, dialog);
        Assert.That(control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0, Is.True, control.Name);
        Assert.That(new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, control.Name + " must stay inside the client area");
    }
    private static Color ColorOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static int CountPixels(WriteableBitmap bitmap, Rect bounds, Color color, double renderScaling)
    {
        using ILockedFramebuffer pixels = bitmap.Lock();
        Assert.That(pixels.Format, Is.EqualTo(PixelFormat.Bgra8888).Or.EqualTo(PixelFormat.Rgba8888));
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        int left = Math.Clamp((int)Math.Floor(bounds.Left * renderScaling), 0, pixels.Size.Width);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right * renderScaling), 0, pixels.Size.Width);
        int top = Math.Clamp((int)Math.Floor(bounds.Top * renderScaling), 0, pixels.Size.Height);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * renderScaling), 0, pixels.Size.Height);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                if (data[offset] == (bgra ? color.B : color.R) && data[offset + 1] == color.G && data[offset + 2] == (bgra ? color.R : color.B) && data[offset + 3] == 255) { count++; }
            }
        }
        return count;
    }
    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        public OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _before);
    }
}
