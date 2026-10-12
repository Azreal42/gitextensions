using System.Text;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DiffViewerToolbarOverflowTests
{
    private const string Patch = "diff --git a/file.cs b/file.cs\n--- a/file.cs\n+++ b/file.cs\n@@ -1 +1 @@\n-int value = 1;\n+int value = 2;\n";

    [AvaloniaTest]
    public void RealHoverResizeAndPopupPreserveOneRowAndExposeUnclippedTrailingControls()
    {
        using Fixture fixture = new();
        double editorHeight = fixture.View.Editor.Bounds.Height;
        Assert.That(fixture.Bar.IsVisible, Is.False);
        fixture.HoverText();
        Assert.That(fixture.Bar.Overflow.IsVisible, Is.False);
        Assert.That(fixture.Bar.Bounds.Height, Is.EqualTo(24));
        Assert.That(fixture.Bar.Bounds.Width, Is.EqualTo(418).Within(1));
        Assert.That(Canvas.GetLeft(fixture.Bar), Is.EqualTo(fixture.View.Bounds.Width - fixture.Bar.Bounds.Width - 40).Within(1));
        Assert.That(fixture.View.Editor.Bounds.Height, Is.EqualTo(editorHeight));

        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme;
            fixture.Resize(150); fixture.HoverText();
            Assert.That(fixture.Bar.Overflow.Name, Is.EqualTo("ProbeOverflow"));
            Assert.That(fixture.Bar.Overflow.IsEffectivelyVisible, Is.True);
            Assert.That(fixture.Bar.Settings.IsEffectivelyVisible, Is.False);
            Assert.That(fixture.Bar.Bounds.Right, Is.LessThanOrEqualTo(fixture.View.Bounds.Width + 0.5));
            Assert.That(fixture.Bar.Bounds.Height, Is.EqualTo(24));
            fixture.OpenOverflow();
            MenuItem[] items = fixture.Bar.OverflowFlyout.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
            Assert.That(items.Select(item => item.Name), Does.Contain("ProbeOverflowEncodingMenu").And.Contain("ProbeOverflowSettings"));
            MenuFlyoutPresenter presenter = items[0].GetVisualAncestors().OfType<MenuFlyoutPresenter>().Single();
            Assert.That(presenter.MaxWidth, Is.EqualTo(double.PositiveInfinity));
            foreach (MenuItem item in items)
            {
                Assert.That(item.IsEffectivelyVisible && item.Bounds.Height > 0, Is.True);
                Point origin = item.TranslatePoint(default, presenter)!.Value;
                Assert.That(origin.Y + item.Bounds.Height, Is.LessThanOrEqualTo(presenter.Bounds.Height + 0.5));
                Assert.That(origin.X + item.Bounds.Width, Is.LessThanOrEqualTo(presenter.Bounds.Width + 0.5));
            }
            TopLevel popup = TopLevel.GetTopLevel(items[0])!;
            fixture.Window.MouseMove(fixture.Outside.TranslatePoint(new Point(5, 5), fixture.Window)!.Value); fixture.Pump();
            popup.MouseMove(items[^1].TranslatePoint(new Point(8, 8), popup)!.Value); Pump(popup);
            Assert.That(fixture.Bar.IsVisible && fixture.Bar.OverflowFlyout.IsOpen, Is.True, "Moving from the text/bar to the popup must not hide its placement target.");
            Point highlight = items[^1].TranslatePoint(new Point(items[^1].Bounds.Width - 10, items[^1].Bounds.Height / 2), popup)!.Value;
            Capture(popup, "diff-toolbar-overflow-" + theme + ".png", frame =>
                Assert.That(Pixel(frame, highlight), Is.EqualTo(Color.Parse(ClassicStyles.OriginalColors(theme == ThemeVariant.Dark)["MenuHighlight"]))));
            fixture.Bar.OverflowFlyout.Hide(); fixture.Pump();
            fixture.Resize(850); fixture.HoverText();
            Assert.That(fixture.Bar.Overflow.IsVisible, Is.False);
            Assert.That(fixture.Bar.Settings.IsEffectivelyVisible && fixture.Bar.EncodingSelector.IsEffectivelyVisible, Is.True);
            Assert.That(fixture.Bar.Bounds.Height, Is.EqualTo(24));
            Assert.That(fixture.View.Editor.Bounds.Height, Is.EqualTo(editorHeight));
        }
    }

    [AvaloniaTest]
    public void PopupClicksUseOriginalRenderingAndComparisonControllersWithoutCopyingOptions()
    {
        using Fixture fixture = new(); fixture.Resize(150);
        int start = Patch.IndexOf("value = 2", StringComparison.Ordinal);
        fixture.View.Select(start, "value".Length); int caret = fixture.View.CaretOffset;
        Assert.That(fixture.View.Editor.SyntaxHighlighting?.Name, Is.EqualTo("C#"));
        int changes = 0, clicks = 0;
        fixture.View.RenderingMenu.OptionsChanged += (_, _) => changes++;
        MenuItem nonprinting = fixture.Open("NonPrintingMenu");
        nonprinting.Click += (_, _) => clicks++;
        Click(nonprinting); fixture.Pump();
        Assert.That(clicks, Is.EqualTo(1), "One real popup click must reach its actual menu item exactly once.");
        Assert.That(changes, Is.EqualTo(1));
        Assert.That(fixture.View.RenderingMenu.Options.ShowNonPrintingChars, Is.True);
        Assert.That(((ToggleButton)fixture.Bar.NonPrinting).IsChecked, Is.True);
        Click(fixture.Open("SyntaxHighlightingMenu")); fixture.Pump();
        Assert.That(fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.False);
        Assert.That(((ToggleButton)fixture.Bar.SyntaxHighlighting).IsChecked, Is.False);
        Assert.That(fixture.View.Editor.SyntaxHighlighting, Is.Null);
        Click(fixture.Open("Whitespace_Eol")); fixture.Pump();
        Assert.That(fixture.View.ComparisonMenu.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Eol));
        Assert.That(((ToggleButton)fixture.Bar.WhitespaceEol).IsChecked, Is.True);
        Click(fixture.Open("Whitespace_AllSpace")); fixture.Pump();
        Assert.That(fixture.View.ComparisonMenu.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace));
        Assert.That(((ToggleButton)fixture.Bar.AllWhitespace).IsChecked, Is.True);
        fixture.OpenOverflow();
        Assert.That(fixture.Item("NonPrintingMenu").IsChecked, Is.True);
        Assert.That(fixture.Item("SyntaxHighlightingMenu").IsChecked, Is.False);
        Assert.That(fixture.Item("Whitespace_Eol").IsChecked && fixture.Item("Whitespace_AllSpace").IsChecked, Is.True);
        fixture.Bar.OverflowFlyout.Hide(); fixture.Pump();
        Assert.That(fixture.View.Text, Is.EqualTo(Patch));
        Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(start));
        Assert.That(fixture.View.SelectionLength, Is.EqualTo("value".Length));
        Assert.That(fixture.View.CaretOffset, Is.EqualTo(caret));
    }

    [AvaloniaTest]
    public async Task PopupEncodingAndSettingsRemainInteractiveAndSettingsReturnRealEditorFocus()
    {
        using Fixture fixture = new(); fixture.Resize(150);
        int start = Patch.IndexOf("value = 2", StringComparison.Ordinal);
        fixture.View.Select(start, "value".Length);
        fixture.OpenOverflow();
        ComboBox selector = (ComboBox)fixture.Item("EncodingMenu").Header!;
        Assert.That(selector.Name, Is.EqualTo("ProbeOverflowEncoding"));
        Click(selector); fixture.Pump();
        Assert.That(selector.IsDropDownOpen, Is.True);
        Assert.That(fixture.Bar.OverflowFlyout.IsOpen, Is.True);
        Assert.That(fixture.Bar.IsVisible, Is.True);
        ComboBoxItem active = (ComboBoxItem)(selector.ContainerFromIndex(selector.SelectedIndex) ?? throw new AssertionException("No realized codec row."));
        Assert.That(TopLevel.GetTopLevel(active), Is.Not.Null);
        DiffViewerEncodingChoice[] choices = selector.Items.OfType<DiffViewerEncodingChoice>().ToArray();
        int target = Array.FindIndex(choices, choice => choice.Name == "windows-1252");
        Assert.That(target, Is.GreaterThanOrEqualTo(0));
        ComboBoxItem codec = (ComboBoxItem)(selector.ContainerFromIndex(target) ?? throw new AssertionException("The Windows-1252 row is among the initially visible native choices."));
        Assert.That(codec.IsEffectivelyVisible, Is.True); Click(codec); fixture.Pump();
        Assert.That(fixture.View.FileEncodingName, Is.EqualTo("windows-1252"));
        Assert.That(((DiffViewerEncodingChoice)fixture.Bar.EncodingSelector.SelectedItem!).Name, Is.EqualTo("windows-1252"));
        // The host reloads a document after a codec change. Keep the same identity,
        // just as Main/Commit do, before opening the shared settings action.
        fixture.View.ShowDocument(Document() with { FileEncodingName = "windows-1252" }); fixture.Pump();
        Click(fixture.Open("Settings"));
        Window dialog = await Owned(fixture);
        Assert.That(dialog, Is.TypeOf<NativeDiffViewerSettingsDialog>());
        Press(dialog, PhysicalKey.Escape);
        await fixture.Bar.PendingSettingsOperation.WaitAsync(TimeSpan.FromSeconds(5)); fixture.Pump();
        Assert.That(fixture.View.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(start));
        Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("value"));
    }

    [AvaloniaTest]
    public void PlainTextResizeAndDocumentLoadingDoNotExposeUnsupportedOrStalePopupActions()
    {
        using Fixture fixture = new(); fixture.Resize(150);
        fixture.View.Mode = DiffViewMode.PlainText; fixture.Pump(); fixture.OpenOverflow();
        Assert.That(fixture.Bar.OverflowFlyout.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Name),
            Is.EqualTo(new[] { "ProbeOverflowEncodingMenu", "ProbeOverflowSettings" }));
        Assert.That(fixture.Bar.NonPrinting.IsEffectivelyVisible, Is.True);
        Assert.That(fixture.Bar.SyntaxHighlighting.IsVisible, Is.False);
        fixture.View.RenderingMenu.SetHostBusy(true);
        fixture.View.BeginDocumentLoad(); fixture.Pump();
        Assert.That(fixture.Bar.OverflowFlyout.IsOpen, Is.False);
        Assert.That(fixture.Bar.IsVisible, Is.False);
        fixture.HoverText(); Assert.That(fixture.Bar.IsVisible, Is.False);
        fixture.View.RenderingMenu.SetHostBusy(false);
        fixture.View.Mode = DiffViewMode.Unified; fixture.View.ShowDocument(Document()); fixture.Pump();
        fixture.OpenOverflow();
        MenuItem item = fixture.Item("NonPrintingMenu");
        Assert.That(((Image)item.Icon!).Source, Is.Not.Null);
        fixture.Resize(850); fixture.Bar.OverflowFlyout.Hide(); fixture.HoverText();
        Assert.That(fixture.Bar.Overflow.IsVisible, Is.False);
        fixture.Resize(150); fixture.OpenOverflow();
        Assert.That(fixture.Item("NonPrintingMenu"), Is.SameAs(item), "A resize must reuse controller-owned menu items and their live bitmaps.");
        Assert.That(((Image)item.Icon!).Source, Is.Not.Null);
    }

    private static GitDiffDocument Document() => new(Patch, Patch, GitDiffAppearance.Patch, true)
        { IsLosslessPatch = true, PatchBytes = Encoding.UTF8.GetBytes(Patch) };
    private static void Pump(TopLevel top) { top.UpdateLayout(); Dispatcher.UIThread.RunJobs(); top.UpdateLayout(); }
    private static void Press(TopLevel top, PhysicalKey key)
    { top.KeyPressQwerty(key, RawInputModifiers.None); if (top is not Window { IsVisible: false }) { top.KeyReleaseQwerty(key, RawInputModifiers.None); } Pump(top); }
    private static void Click(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered overflow control root."); Pump(top);
        Assert.That(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(point); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Pump(top);
    }
    private static void Capture(TopLevel top, string name, Action<WriteableBitmap> inspect)
    {
        Pump(top); using WriteableBitmap frame = top.CaptureRenderedFrame() ?? throw new AssertionException("No overflow popup frame.");
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path);
        inspect(frame);
    }
    private static Color Pixel(WriteableBitmap frame, Point point)
    {
        using var buffer = frame.Lock();
        int offset = (int)point.Y * buffer.RowBytes + (int)point.X * 4;
        bool bgra = buffer.Format == PixelFormat.Bgra8888;
        return Color.FromArgb(Marshal.ReadByte(buffer.Address, offset + 3), Marshal.ReadByte(buffer.Address, offset + (bgra ? 2 : 0)),
            Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + (bgra ? 0 : 2)));
    }
    private static async Task<Window> Owned(Fixture fixture)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!fixture.Window.OwnedWindows.Any(window => window.IsVisible && window is NativeDiffViewerSettingsDialog))
        { fixture.Pump(); await Task.Delay(10, timeout.Token); }
        return fixture.Window.OwnedWindows.Single(window => window.IsVisible && window is NativeDiffViewerSettingsDialog);
    }
    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-toolbar-overflow-" + Guid.NewGuid().ToString("N"));
        internal TextBox Source { get; } = new();
        internal DiffView View { get; }
        internal DiffViewerToolbar Bar => View.Toolbar;
        internal TextBox Outside { get; } = new() { Height = 24, Text = "Outside the viewer" };
        internal Window Window { get; }
        internal Fixture()
        {
            View = new(Source, new DiffViewerPreferencesStore(DirectoryPath)) { SyntaxPath = "file.cs", DocumentIdentity = "fixture/file.cs/HEAD-worktree" };
            Bar.SetNames("Probe"); View.ShowDocument(Document());
            DockPanel content = new(); DockPanel.SetDock(Outside, Dock.Bottom); content.Children.Add(Outside); content.Children.Add(View);
            Window = new() { Width = 850, Height = 320, Content = content }; Window.Show(); Pump();
        }
        // Resize the reader pane inside a normal desktop window. The headless
        // popup positioner's screen follows its owner; a 150px owner would
        // constrain even the menu's native window to that artificial screen.
        internal void Resize(double width) { View.Width = width; View.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; Pump(); }
        internal void HoverText() { Window.MouseMove(View.TranslatePoint(new Point(12, 110), Window)!.Value); Pump(); }
        internal void OpenOverflow()
        {
            Bar.OverflowFlyout.Hide(); HoverText();
            Assert.That(Bar.Overflow.IsEffectivelyVisible, Is.True);
            Assert.That(Bar.OverflowFlyout.Items.OfType<MenuItem>().Any(), Is.True, "The stable source must be populated before the first real click creates its presenter.");
            Click(Bar.Overflow); Pump(); Assert.That(Bar.OverflowFlyout.IsOpen, Is.True);
            foreach (MenuItem item in Bar.OverflowFlyout.Items.OfType<MenuItem>().Where(item => item.IsVisible))
            { Assert.That(TopLevel.GetTopLevel(item), Is.Not.Null, "Every overflow item must be in the actual opened presenter."); }
        }
        internal MenuItem Item(string suffix) => Bar.OverflowFlyout.Items.OfType<MenuItem>().Single(item => item.Name == "ProbeOverflow" + suffix);
        internal MenuItem Open(string suffix) { OpenOverflow(); return Item(suffix); }
        internal void Pump() => DiffViewerToolbarOverflowTests.Pump(Window);
        public void Dispose()
        {
            Bar.OverflowFlyout.Hide(); View.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(DirectoryPath)) { Directory.Delete(DirectoryPath, true); }
        }
    }
}
