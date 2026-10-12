using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DiffViewerToolbarTests
{
    private const string Patch = "diff --git a/file.cs b/file.cs\n--- a/file.cs\n+++ b/file.cs\n@@ -1,3 +1,3 @@\n public class Example\n-{ int before = 1; }\n+{ int after = 2; }\n";

    [AvaloniaTest]
    public void RealMouseMovementShowsTheOriginalOrderWithoutReservingDocumentHeightAndKeepsTheFirstClick()
    {
        using Fixture fixture = new();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme; fixture.Pump();
            fixture.Move(fixture.Outside, new Point(2, 2));
            Assert.That(fixture.View.Toolbar.IsVisible, Is.False);
            Rect editorBefore = fixture.View.Editor.Bounds;
            fixture.HoverText();
            DiffViewerToolbar toolbar = fixture.View.Toolbar;
            Assert.That(toolbar.IsEffectivelyVisible, Is.True, "The original shows on MouseMove, without a hover timer.");
            Assert.That(fixture.View.Editor.Bounds, Is.EqualTo(editorBefore), "The toolbar overlays the document instead of docking above it.");
            Point origin = toolbar.TranslatePoint(default, fixture.View)!.Value;
            Assert.That(origin.Y, Is.Zero);
            Assert.That(origin.X + toolbar.Bounds.Width, Is.EqualTo(fixture.View.Bounds.Width - 40));
            StackPanel row = (StackPanel)toolbar.Child!;
            Control[] controls = row.Children.Take(14).Select(control => control is Decorator { Child: { } child } ? child : control).ToArray();
            Assert.That(controls.Select(control => control.Name ?? "separator"), Is.EqualTo(new[]
            {
                "DiffViewerNextChange", "DiffViewerPreviousChange", "separator", "DiffViewerIncreaseContext", "DiffViewerDecreaseContext", "separator",
                "DiffViewerEntireFile", "DiffViewerNonPrinting", "DiffViewerSyntaxHighlighting", "DiffViewerWhitespace_Eol", "DiffViewerWhitespace_Change",
                "DiffViewerWhitespace_AllSpace", "DiffViewerEncoding", "DiffViewerSettings"
            }));
            Assert.That(controls.OfType<Button>().Count(), Is.EqualTo(11));
            Assert.That(controls.OfType<Button>().All(button => button.Bounds.Size == new Size(24, 24)), Is.True);
            fixture.Move(toolbar.WhitespaceEol, new Point(12, 12));
            Assert.That(toolbar.IsVisible, Is.True, "Moving into the toolbar must not hide it before its first click.");
            int changes = 0; fixture.View.ComparisonOptionsChanged += (_, _) => changes++;
            fixture.Click(toolbar.WhitespaceEol);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(((ToggleButton)toolbar.WhitespaceEol).IsChecked, Is.EqualTo(fixture.View.ComparisonMenu.Options.IgnoreWhitespace != GitCommands.Settings.IgnoreWhitespaceKind.None));
            fixture.Capture("diff-viewer-floating-toolbar-" + theme + ".png");
            fixture.Move(fixture.Outside, new Point(2, 2));
            Assert.That(toolbar.IsVisible, Is.False);
            fixture.Window.Width = theme == ThemeVariant.Light ? 1100 : 900; fixture.Pump(); fixture.HoverText();
            origin = toolbar.TranslatePoint(default, fixture.View)!.Value;
            Assert.That(origin.X + toolbar.Bounds.Width, Is.EqualTo(fixture.View.Bounds.Width - 40));
        }
    }

    [AvaloniaTest]
    public void SyntaxButtonMenuAndScopedXKeepTheDocumentSelectionAndFullTextGrammar()
    {
        using Fixture fixture = new();
        fixture.View.SyntaxPath = "literal é[1].cs"; fixture.Pump();
        Assert.That(fixture.View.Editor.SyntaxHighlighting?.Name, Is.EqualTo("C#"));
        int start = fixture.View.Text.IndexOf("after", StringComparison.Ordinal);
        fixture.View.Select(start, 5); fixture.View.Editor.TextArea.Focus();
        var offset = fixture.View.Editor.TextArea.TextView.ScrollOffset;
        string before = fixture.View.Text, canonical = fixture.View.CanonicalText;
        fixture.HoverText(); fixture.Click(fixture.View.Toolbar.SyntaxHighlighting);
        MenuItem menu = fixture.View.ContextMenu!.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "DiffViewerSyntaxHighlightingMenu");
        Assert.That(fixture.View.Editor.SyntaxHighlighting, Is.Null);
        Assert.That(fixture.View.DisplayOptions.SyntaxHighlighting || menu.IsChecked, Is.False);
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.SelectionLength, Is.EqualTo(5));
        fixture.View.Editor.TextArea.Focus(); fixture.Key(PhysicalKey.X); fixture.Pump();
        Assert.That(fixture.View.Editor.SyntaxHighlighting?.Name, Is.EqualTo("C#"));
        Assert.That(fixture.View.DisplayOptions.SyntaxHighlighting && menu.IsChecked, Is.True);
        Assert.That(fixture.View.Editor.TextArea.TextView.ScrollOffset, Is.EqualTo(offset));
        fixture.Outside.Focus(); fixture.Key(PhysicalKey.X); fixture.Pump();
        Assert.That(fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.True, "Typing X outside the reader must keep its normal meaning.");
        fixture.HoverText();
        Assert.That(fixture.View.Toolbar.EncodingSelector.Focus(), Is.True);
        fixture.Key(PhysicalKey.X); fixture.Pump();
        Assert.That(fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.True, "Typing X in the codec selector must not change the document's syntax option.");
        fixture.View.RenderingMenu.ToggleSyntaxHighlighting(); fixture.View.Mode = DiffViewMode.PlainText; fixture.Pump();
        Assert.That(fixture.View.Editor.SyntaxHighlighting?.Name, Is.EqualTo("C#"), "The original full-file view always uses its automatic grammar.");
        Assert.That(fixture.View.Toolbar.SyntaxHighlighting.IsVisible || menu.IsVisible, Is.False);
        fixture.View.Editor.TextArea.Focus(); fixture.Key(PhysicalKey.X); fixture.Pump();
        Assert.That(fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.False);
        Assert.That(fixture.View.Text, Is.EqualTo(before)); Assert.That(fixture.View.CanonicalText, Is.EqualTo(canonical));
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.SelectionLength, Is.EqualTo(5));
        fixture.HoverText();
        Assert.That(fixture.View.Toolbar.NonPrinting.IsVisible && fixture.View.Toolbar.EncodingSelector.IsVisible && fixture.View.Toolbar.Settings.IsVisible, Is.True);
        Assert.That(fixture.View.Toolbar.EntireFile.IsVisible || fixture.View.Toolbar.WhitespaceEol.IsVisible || fixture.View.Toolbar.NextChange.IsVisible, Is.False);
        fixture.Capture("diff-viewer-floating-full-text.png");
    }

    [AvaloniaTest]
    public async Task FloatingSettingsOpensTheOriginalPageWithItsOwnerThemeAndReturnsToTheReader()
    {
        using Fixture fixture = new(); fixture.Window.RequestedThemeVariant = ThemeVariant.Dark; fixture.Pump();
        fixture.View.Editor.TextArea.Focus(); fixture.HoverText(); fixture.Click(fixture.View.Toolbar.Settings); fixture.Pump();
        Window settings = fixture.Window.OwnedWindows.Single(window => window.Name == "NativeDiffViewerSettingsDialog");
        Assert.That(settings.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(settings.GetVisualDescendants().OfType<CheckBox>().Any(item => item.Name == "chkRememberShowNonPrintingCharsPreference"), Is.True);
        Assert.That(settings.GetVisualDescendants().OfType<CheckBox>().Any(item => item.Name == "chkRememberShowSyntaxHighlightingInDiff"), Is.True);
        Button cancel = settings.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "DiffViewerSettingsCancel");
        Click(settings, cancel); await fixture.View.Toolbar.PendingSettingsOperation; fixture.Pump();
        Assert.That(fixture.Window.OwnedWindows, Is.Empty);
        Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
    }

    private static void Click(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Dispatcher.UIThread.RunJobs();
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "gitextensions-toolbar-" + Guid.NewGuid().ToString("N"));
        public Window Window { get; }
        public DiffView View { get; }
        public TextBox Outside { get; } = new() { Name = "OutsideReader", Height = 28 };
        public Fixture()
        {
            View = new(new TextBox { Text = Patch }, new DiffViewerPreferencesStore(_temporary));
            Grid body = new() { RowDefinitions = new RowDefinitions("*,28"), Children = { View, Outside } }; Grid.SetRow(Outside, 1);
            Window = new() { Width = 900, Height = 450, Content = body }; Window.Show(); Pump();
        }
        public void Pump() { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        public void Move(Control control, Point point) { Window.MouseMove(control.TranslatePoint(point, Window)!.Value); Pump(); }
        public void HoverText() => Move(View.Editor, new Point(80, 100));
        public void Click(Control control) { DiffViewerToolbarTests.Click(Window, control); Pump(); }
        public void Key(PhysicalKey key) { Window.KeyPressQwerty(key, RawInputModifiers.None); Window.KeyReleaseQwerty(key, RawInputModifiers.None); }
        public void Capture(string name)
        {
            Pump(); using WriteableBitmap frame = Window.CaptureRenderedFrame() ?? throw new AssertionException("No floating toolbar frame.");
            string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(file); TestContext.AddTestAttachment(file);
        }
        public void Dispose() { Window.Close(); View.Dispose(); if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, true); } }
    }
}
